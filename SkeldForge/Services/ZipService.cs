using System.IO.Compression;
using SkeldForge.Models;

namespace SkeldForge.Services;

/// <summary>
/// Conservative limits used while inspecting and extracting user supplied archives.
/// They prevent accidental resource exhaustion without imposing a small, mod-specific
/// package-size limit.
/// </summary>
public sealed class ZipSafetyLimits
{
    public int MaximumEntries { get; init; } = 50_000;
    public long MaximumEntryBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 10_000;
}

/// <summary>
/// Reads and extracts ZIP files without trusting archive entry paths. Unsafe entries,
/// duplicate targets, symbolic links, and extraction through reparse points are rejected.
/// </summary>
public sealed class ZipService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly ZipSafetyLimits _limits;

    public ZipService(ZipSafetyLimits? limits = null)
    {
        _limits = limits ?? new ZipSafetyLimits();
    }

    public IReadOnlyList<PackageEntryInfo> Inspect(string archivePath) => ValidateArchive(archivePath);

    /// <summary>
    /// Validates the complete central directory. No files are written. Invalid archives
    /// throw <see cref="InvalidDataException"/> with a non-localized technical reason.
    /// </summary>
    public IReadOnlyList<PackageEntryInfo> ValidateArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The ZIP archive does not exist.", archivePath);
        }

        using var archive = ZipFile.OpenRead(archivePath);
        return ValidateEntries(archive, string.Empty)
            .Select(x => ToModel(x.Entry, x.RelativePath))
            .ToArray();
    }

    public Task ExtractSafeAsync(
        string archivePath,
        string destinationDirectory,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExtractSafeAsync(archivePath, destinationDirectory, string.Empty, overwrite, progress, cancellationToken);

    /// <summary>
    /// Safely extracts an archive. <paramref name="contentRootPrefix"/> may remove one
    /// analyzer-approved wrapper directory. Existing files are never replaced unless
    /// <paramref name="overwrite"/> is explicitly true, and replacements are rolled back
    /// if extraction fails.
    /// </summary>
    public async Task ExtractSafeAsync(
        string archivePath,
        string destinationDirectory,
        string contentRootPrefix,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The ZIP archive does not exist.", archivePath);
        }

        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root);

        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        var replacements = new List<(string Target, string Backup)>();
        var temporaryFiles = new List<string>();

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = ValidateEntries(archive, contentRootPrefix);
            var total = entries.Where(x => !x.IsDirectory).Sum(x => x.Entry.Length);
            long completed = 0;

            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveContainedPath(root, item.RelativePath);
                EnsureExistingAncestorsAreNotReparsePoints(root, Path.GetDirectoryName(target) ?? root);

                if (item.IsDirectory)
                {
                    if (File.Exists(target))
                    {
                        throw new IOException($"A file blocks archive directory '{item.RelativePath}'.");
                    }

                    if (!Directory.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                        createdDirectories.Add(target);
                    }

                    EnsureNotReparsePoint(target);
                    continue;
                }

                var parent = Path.GetDirectoryName(target)
                    ?? throw new InvalidDataException($"Entry has no destination parent: '{item.RelativePath}'.");
                if (!Directory.Exists(parent))
                {
                    CreateDirectoryTree(root, parent, createdDirectories);
                }

                EnsureExistingAncestorsAreNotReparsePoints(root, parent);
                if (Directory.Exists(target))
                {
                    throw new IOException($"A directory blocks archive file '{item.RelativePath}'.");
                }

                if (File.Exists(target) && !overwrite)
                {
                    throw new IOException($"Destination file already exists: '{item.RelativePath}'.");
                }

                var temporary = target + $".skeldforge-extract-{Guid.NewGuid():N}.tmp";
                temporaryFiles.Add(temporary);
                await using (var input = item.Entry.Open())
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[128 * 1024];
                    long entryBytes = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        entryBytes = checked(entryBytes + read);
                        completed = checked(completed + read);
                        if (entryBytes > _limits.MaximumEntryBytes || completed > _limits.MaximumTotalBytes)
                        {
                            throw new InvalidDataException("Archive expanded beyond the configured safety limit.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new OperationProgress("ZipExtract", completed, total, item.RelativePath));
                    }

                    if (entryBytes != item.Entry.Length)
                    {
                        throw new InvalidDataException($"Entry length changed while extracting '{item.RelativePath}'.");
                    }
                }

                if (File.Exists(target))
                {
                    var backup = target + $".skeldforge-replaced-{Guid.NewGuid():N}.bak";
                    File.Move(target, backup);
                    replacements.Add((target, backup));
                }
                else
                {
                    createdFiles.Add(target);
                }

                File.Move(temporary, target);
                temporaryFiles.Remove(temporary);
            }

            foreach (var (_, backup) in replacements)
            {
                File.Delete(backup);
            }
        }
        catch
        {
            foreach (var temporary in temporaryFiles)
            {
                TryDeleteFile(temporary);
            }

            foreach (var created in createdFiles.AsEnumerable().Reverse())
            {
                TryDeleteFile(created);
            }

            foreach (var (target, backup) in replacements.AsEnumerable().Reverse())
            {
                TryDeleteFile(target);
                if (File.Exists(backup))
                {
                    try
                    {
                        File.Move(backup, target);
                    }
                    catch
                    {
                        // Preserve the backup if the original path cannot be restored.
                    }
                }
            }

            foreach (var directory in createdDirectories
                         .Distinct(PathComparer)
                         .OrderByDescending(x => x.Length))
            {
                TryDeleteEmptyDirectory(directory);
            }

            throw;
        }
    }

    public static string NormalizeEntryPath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Archive contains an empty or invalid entry name.");
        }

        var value = entryName.Replace('\\', '/');
        if (value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(value) || value.Contains(':'))
        {
            throw new InvalidDataException($"Archive entry uses a rooted path: '{entryName}'.");
        }

        var isDirectory = value.EndsWith('/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or ".."))
        {
            throw new InvalidDataException($"Archive entry contains path traversal: '{entryName}'.");
        }

        if (segments.Any(x => x.TrimEnd(' ', '.') != x || string.IsNullOrWhiteSpace(x)))
        {
            throw new InvalidDataException($"Archive entry contains an ambiguous Windows path: '{entryName}'.");
        }

        return string.Join('/', segments) + (isDirectory ? "/" : string.Empty);
    }

    public static string ResolveContainedPath(string destinationRoot, string relativePath)
    {
        var root = Path.GetFullPath(destinationRoot);
        var normalized = NormalizeEntryPath(relativePath).TrimEnd('/');
        var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry escapes the destination: '{relativePath}'.");
        }

        return target;
    }

    private IReadOnlyList<ValidatedEntry> ValidateEntries(ZipArchive archive, string contentRootPrefix)
    {
        if (archive.Entries.Count > _limits.MaximumEntries)
        {
            throw new InvalidDataException($"Archive contains more than {_limits.MaximumEntries:N0} entries.");
        }

        var prefix = NormalizePrefix(contentRootPrefix);
        var results = new List<ValidatedEntry>(archive.Entries.Count);
        var targets = new HashSet<string>(PathComparer);
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"Archive contains a symbolic-link entry: '{entry.FullName}'.");
            }

            var normalized = NormalizeEntryPath(entry.FullName);
            if (prefix.Length > 0)
            {
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Entry is outside the selected package content root: '{entry.FullName}'.");
                }

                normalized = normalized[prefix.Length..];
                if (normalized.Length == 0)
                {
                    continue;
                }
            }

            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0;
            if (!targets.Add(normalized.TrimEnd('/')))
            {
                throw new InvalidDataException($"Archive maps multiple entries to '{normalized}'.");
            }

            if (!isDirectory)
            {
                if (entry.Length < 0 || entry.Length > _limits.MaximumEntryBytes)
                {
                    throw new InvalidDataException($"Archive entry is too large: '{entry.FullName}'.");
                }

                total = checked(total + entry.Length);
                if (total > _limits.MaximumTotalBytes)
                {
                    throw new InvalidDataException("Archive is larger than the configured extraction limit.");
                }

                if (entry.CompressedLength == 0 && entry.Length > 0 ||
                    entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > _limits.MaximumCompressionRatio)
                {
                    throw new InvalidDataException($"Archive entry has an unsafe compression ratio: '{entry.FullName}'.");
                }
            }

            results.Add(new ValidatedEntry(entry, normalized, isDirectory));
        }

        return results;
    }

    private static string NormalizePrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeEntryPath(value.EndsWith('/') ? value : value + '/');
        return normalized;
    }

    private static PackageEntryInfo ToModel(ZipArchiveEntry entry, string path) => new()
    {
        RelativePath = path,
        CompressedSize = entry.CompressedLength,
        UncompressedSize = entry.Length,
        IsDirectory = entry.Name.Length == 0 || path.EndsWith('/')
    };

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixType == 0xA000 || dosAttributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void CreateDirectoryTree(string root, string target, ICollection<string> created)
    {
        var missing = new Stack<string>();
        var current = target;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new IOException($"Cannot resolve parent directory for '{target}'.");
        }

        EnsureExistingAncestorsAreNotReparsePoints(root, current);
        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            EnsureNotReparsePoint(directory);
            created.Add(directory);
        }
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string root, string targetDirectory)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var canonicalTarget = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalTarget.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalTarget.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Destination parent escapes the extraction root.");
        }

        EnsureNotReparsePoint(canonicalRoot);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
        var current = canonicalRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                EnsureNotReparsePoint(current);
            }
        }
    }

    private static void EnsureNotReparsePoint(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Extraction through a reparse point is not allowed: '{directory}'.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original extraction exception.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Preserve the original extraction exception.
        }
    }

    private sealed record ValidatedEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
