using System.Text.Json;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

public interface IBackupManager
{
    Task<OperationResult<BackupManifest>> CreateBackupAsync(
        string sourceRoot,
        IEnumerable<BackupSourceItem> items,
        BackupKind kind,
        string displayName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult<RestoreReport>> RestoreAsync(
        BackupManifest manifest,
        bool overwriteConflicts = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates verified, manifest-backed file backups and performs conflict-aware restores.
/// The class and mutation methods are virtual so cleanup tests can inject a deterministic
/// backup failure without touching a real game installation.
/// </summary>
public class BackupManager : IBackupManager
{
    public const string ManifestFileName = "manifest.json";
    private readonly HashService _hashService;
    private readonly FileSystemService _fileSystem;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupManager(
        string backupRoot,
        HashService? hashService = null,
        FileSystemService? fileSystem = null,
        LoggingService? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        BackupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
        _hashService = hashService ?? new HashService();
        _fileSystem = fileSystem ?? new FileSystemService();
        _logger = logger;
    }

    public BackupManager(
        AppPaths paths,
        HashService? hashService = null,
        FileSystemService? fileSystem = null,
        LoggingService? logger = null)
        : this(paths?.BackupsDirectory ?? throw new ArgumentNullException(nameof(paths)), hashService, fileSystem, logger)
    {
    }

    public string BackupRoot { get; }

    public virtual async Task<OperationResult<BackupManifest>> CreateBackupAsync(
        string sourceRoot,
        IEnumerable<BackupSourceItem> items,
        BackupKind kind,
        string displayName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(items);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
            if (!Directory.Exists(source))
            {
                return OperationResult<BackupManifest>.Fail("BackupSourceMissing", source);
            }

            RejectReparsePoint(source);
            var files = ExpandAndValidateItems(source, items, cancellationToken);
            if (files.Count == 0)
            {
                return OperationResult<BackupManifest>.Fail("BackupHasNoFiles");
            }

            var id = Guid.NewGuid().ToString("N");
            var kindRoot = Path.Combine(BackupRoot, kind.ToString());
            Directory.CreateDirectory(kindRoot);
            var folderName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{id[..8]}";
            var finalDirectory = Path.Combine(kindRoot, folderName);
            staging = Path.Combine(kindRoot, $".staging-{id}");
            Directory.CreateDirectory(staging);

            var manifest = new BackupManifest
            {
                Id = id,
                Kind = kind,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? kind.ToString() : displayName.Trim(),
                SourceRoot = source,
                BackupDirectory = finalDirectory,
                Timestamp = DateTimeOffset.UtcNow
            };

            var totalBytes = files.Sum(x => new FileInfo(x.FullPath).Length);
            long completed = 0;
            foreach (var item in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelative(item.RelativePath);
                var stagedFile = ResolveContainedPath(Path.Combine(staging, "files"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);

                var sourceHash = await _hashService.ComputeFileSha256Async(item.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                var size = new FileInfo(item.FullPath).Length;
                await CopyWithProgressAsync(item.FullPath, stagedFile, relative, totalBytes,
                        () => completed, value => completed = value, progress, cancellationToken)
                    .ConfigureAwait(false);
                var backupHash = await _hashService.ComputeFileSha256Async(stagedFile, cancellationToken)
                    .ConfigureAwait(false);
                if (!sourceHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase) ||
                    new FileInfo(stagedFile).Length != size)
                {
                    throw new IOException($"Backup verification failed for '{relative}'.");
                }

                manifest.Files.Add(new BackupEntry
                {
                    OriginalPath = ResolveContainedPath(source, relative),
                    RelativePath = relative,
                    BackupPath = ResolveContainedPath(Path.Combine(finalDirectory, "files"), relative),
                    Sha256 = sourceHash,
                    Size = size,
                    Classification = item.Classification
                });
            }

            await _fileSystem.AtomicWriteJsonAsync(
                    Path.Combine(staging, ManifestFileName), manifest, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, finalDirectory);
            staging = null;
            await LogInfoAsync($"Created verified {kind} backup '{manifest.Id}' with {manifest.Files.Count} files.")
                .ConfigureAwait(false);
            return OperationResult<BackupManifest>.Ok(manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            await LogErrorAsync("Backup creation failed; no backup was published.", ex).ConfigureAwait(false);
            return OperationResult<BackupManifest>.Fail("BackupCreationFailed", ex.Message);
        }
        finally
        {
            if (staging is not null)
            {
                _fileSystem.TryDeleteDirectory(staging);
            }

            _gate.Release();
        }
    }

    public virtual Task<OperationResult<BackupManifest>> CreateBackupAsync(
        string sourceRoot,
        IEnumerable<CleanupFinding> findings,
        BackupKind kind,
        string displayName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var items = findings.Select(x => new BackupSourceItem
        {
            FullPath = x.FullPath,
            RelativePath = x.RelativePath,
            IsDirectory = x.IsDirectory,
            Classification = x.Classification.ToString()
        });
        return CreateBackupAsync(sourceRoot, items, kind, displayName, progress, cancellationToken);
    }

    public virtual async Task<OperationResult<RestoreReport>> RestoreAsync(
        BackupManifest manifest,
        bool overwriteConflicts = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? transaction = null;
        var created = new List<string>();
        var replaced = new List<(string Target, string Previous)>();
        var report = new RestoreReport { BackupId = manifest.Id };
        try
        {
            var backupDirectory = ValidateBackupDirectory(manifest.BackupDirectory);
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.SourceRoot));
            Directory.CreateDirectory(sourceRoot);
            RejectReparsePoint(sourceRoot);

            var verified = new List<(BackupEntry Entry, string BackupFile, string Target)>();
            foreach (var entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelative(entry.RelativePath);
                var backupFile = ResolveContainedPath(Path.Combine(backupDirectory, "files"), relative);
                var target = ResolveContainedPath(sourceRoot, relative);
                if (!File.Exists(backupFile))
                {
                    return OperationResult<RestoreReport>.Fail("BackupFileMissing", relative);
                }

                RejectReparsePoint(backupFile);
                var hash = await _hashService.ComputeFileSha256Async(backupFile, cancellationToken)
                    .ConfigureAwait(false);
                if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult<RestoreReport>.Fail("BackupIntegrityFailed", relative);
                }

                verified.Add((entry, backupFile, target));
                if (!File.Exists(target))
                {
                    continue;
                }

                RejectReparsePoint(target);
                var existingHash = await _hashService.ComputeFileSha256Async(target, cancellationToken)
                    .ConfigureAwait(false);
                if (existingHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    report.Entries.Add(new RestoreEntryResult
                    {
                        RelativePath = relative,
                        Status = RestoreEntryStatus.Skipped,
                        Message = "Existing file already matches the backup."
                    });
                }
                else if (!overwriteConflicts)
                {
                    report.Entries.Add(new RestoreEntryResult
                    {
                        RelativePath = relative,
                        Status = RestoreEntryStatus.Conflict,
                        Message = "A different file already exists; explicit overwrite approval is required."
                    });
                }
            }

            if (report.ConflictCount > 0)
            {
                return FailWithValue("RestoreConflicts", "No files were restored.", report);
            }

            transaction = Path.Combine(backupDirectory, $".restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(transaction);
            var completed = 0;
            foreach (var item in verified)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelative(item.Entry.RelativePath);
                if (report.Entries.Any(x =>
                        x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) &&
                        x.Status == RestoreEntryStatus.Skipped))
                {
                    progress?.Report(new OperationProgress("BackupRestore", ++completed, verified.Count, relative));
                    continue;
                }

                EnsureNoReparseAncestors(sourceRoot, Path.GetDirectoryName(item.Target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                var temporary = item.Target + $".skeldforge-restore-{Guid.NewGuid():N}.tmp";
                try
                {
                    await CopyFileAsync(item.BackupFile, temporary, cancellationToken).ConfigureAwait(false);
                    if (File.Exists(item.Target))
                    {
                        var previous = ResolveContainedPath(transaction, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                        File.Move(item.Target, previous);
                        replaced.Add((item.Target, previous));
                    }
                    else
                    {
                        created.Add(item.Target);
                    }

                    File.Move(temporary, item.Target);
                }
                finally
                {
                    TryDeleteFile(temporary);
                }

                report.Entries.Add(new RestoreEntryResult
                {
                    RelativePath = relative,
                    Status = RestoreEntryStatus.Restored,
                    Message = "Restored from verified backup."
                });
                progress?.Report(new OperationProgress("BackupRestore", ++completed, verified.Count, relative));
            }

            _fileSystem.TryDeleteDirectory(transaction);
            transaction = null;
            await LogInfoAsync($"Restored backup '{manifest.Id}' ({report.RestoredCount} files).")
                .ConfigureAwait(false);
            return OperationResult<RestoreReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            RollBackRestore(created, replaced);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RollBackRestore(created, replaced);
            report.Entries.Add(new RestoreEntryResult
            {
                Status = RestoreEntryStatus.Error,
                Message = ex.Message
            });
            await LogErrorAsync("Backup restore failed and was rolled back.", ex).ConfigureAwait(false);
            return FailWithValue("BackupRestoreFailed", ex.Message, report);
        }
        finally
        {
            if (transaction is not null && created.Count == 0 && replaced.Count == 0)
            {
                _fileSystem.TryDeleteDirectory(transaction);
            }

            _gate.Release();
        }
    }

    public virtual async Task<IReadOnlyList<BackupManifest>> GetBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(BackupRoot))
            {
                return [];
            }

            var backups = new List<BackupManifest>();
            foreach (var manifestPath in Directory.EnumerateFiles(
                         BackupRoot, ManifestFileName, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (manifestPath.Contains($"{Path.DirectorySeparatorChar}.staging-", StringComparison.OrdinalIgnoreCase) ||
                    manifestPath.Contains($"{Path.DirectorySeparatorChar}.restore-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    await using var stream = File.OpenRead(manifestPath);
                    var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                        stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                    if (manifest is not null)
                    {
                        manifest.BackupDirectory = Path.GetDirectoryName(manifestPath)!;
                        backups.Add(manifest);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A corrupt entry is omitted; diagnostics can report it from the log.
                    await LogErrorAsync($"Could not read backup manifest '{manifestPath}'.", ex).ConfigureAwait(false);
                }
            }

            return backups.OrderByDescending(x => x.Timestamp).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public virtual async Task<OperationResult> DeleteBackupAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = ValidateBackupDirectory(manifest.BackupDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            await _fileSystem.DeleteDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult.Fail("BackupDeleteFailed", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<BackupSourceItem> ExpandAndValidateItems(
        string sourceRoot,
        IEnumerable<BackupSourceItem> items,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, BackupSourceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(item.RelativePath);
            var expected = ResolveContainedPath(sourceRoot, relative);
            var supplied = Path.GetFullPath(item.FullPath);
            if (!expected.Equals(supplied, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup source does not match its relative path: '{relative}'.");
            }

            if (item.IsDirectory)
            {
                if (!Directory.Exists(expected))
                {
                    throw new DirectoryNotFoundException(expected);
                }

                RejectReparsePoint(expected);
                foreach (var file in EnumerateFilesSafely(expected, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(file);
                    EnsureNoReparseAncestors(sourceRoot, Path.GetDirectoryName(file)!);
                    var childRelative = NormalizeRelative(Path.GetRelativePath(sourceRoot, file));
                    files[childRelative] = new BackupSourceItem
                    {
                        FullPath = file,
                        RelativePath = childRelative,
                        Classification = item.Classification
                    };
                }
            }
            else
            {
                if (!File.Exists(expected))
                {
                    throw new FileNotFoundException("Planned backup file is missing.", expected);
                }

                RejectReparsePoint(expected);
                EnsureNoReparseAncestors(sourceRoot, Path.GetDirectoryName(expected)!);
                files[relative] = new BackupSourceItem
                {
                    FullPath = expected,
                    RelativePath = relative,
                    Classification = item.Classification
                };
            }
        }

        return files.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            RejectReparsePoint(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(file);
                yield return file;
            }

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(directory);
                pending.Push(directory);
            }
        }
    }

    private string ValidateBackupDirectory(string path)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = BackupRoot + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory))
        {
            throw new InvalidDataException("Backup directory is missing or outside the configured backup root.");
        }

        RejectReparsePoint(directory);
        return directory;
    }

    private static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new InvalidDataException("Backup manifest contains a rooted path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or ".."))
        {
            throw new InvalidDataException("Backup manifest contains path traversal.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonical = Path.GetFullPath(Path.Combine(
            canonicalRoot, NormalizeRelative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!canonical.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup path escaped its configured root.");
        }

        return canonical;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Reparse points are not allowed in backup operations: '{path}'.");
        }
    }

    private static void EnsureNoReparseAncestors(string root, string directory)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!canonicalDirectory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalDirectory.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Directory escaped the permitted root.");
        }

        var current = canonicalRoot;
        RejectReparsePoint(current);
        foreach (var segment in Path.GetRelativePath(canonicalRoot, canonicalDirectory)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current);
            }
        }
    }

    private static async Task CopyWithProgressAsync(
        string source,
        string destination,
        string relative,
        long total,
        Func<long> getCompleted,
        Action<long> setCompleted,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            var completed = checked(getCompleted() + read);
            setCompleted(completed);
            progress?.Report(new OperationProgress("BackupCreate", completed, total, relative));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RollBackRestore(
        IEnumerable<string> created,
        IEnumerable<(string Target, string Previous)> replaced)
    {
        foreach (var path in created.Reverse())
        {
            TryDeleteFile(path);
        }

        foreach (var (target, previous) in replaced.Reverse())
        {
            try
            {
                TryDeleteFile(target);
                if (File.Exists(previous))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(previous, target);
                }
            }
            catch
            {
                // Preserve transaction content for manual recovery.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logger is not null)
        {
            try { await _logger.InfoAsync(message).ConfigureAwait(false); } catch { }
        }
    }

    private async Task LogErrorAsync(string message, Exception exception)
    {
        if (_logger is not null)
        {
            try { await _logger.ErrorAsync(message, exception).ConfigureAwait(false); } catch { }
        }
    }

    private static OperationResult<T> FailWithValue<T>(string key, string message, T value) => new()
    {
        Success = false,
        ErrorKey = key,
        TechnicalMessage = message,
        Value = value
    };
}
