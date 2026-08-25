using System.Text;
using System.Text.Json;
using System.IO;
using SkeldForge.Models;

namespace SkeldForge.Services;

public sealed class FileSystemService
{
    private const int CopyBufferSize = 128 * 1024;
    private const long DefaultSafetyReserve = 64L * 1024 * 1024;

    public async Task<long> GetDirectorySizeAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await CreateSnapshotAsync(directoryPath, cancellationToken).ConfigureAwait(false);
        return snapshot.Files.Sum(file => file.Length);
    }

    public Task<DiskSpaceInfo> CheckDiskSpaceAsync(
        string destinationPath,
        long contentBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (contentBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentBytes));
        }

        var reserve = Math.Max(DefaultSafetyReserve, contentBytes / 20);
        var required = checked(contentBytes + reserve);
        var available = GetAvailableFreeSpace(destinationPath);
        return Task.FromResult(new DiskSpaceInfo(required, available));
    }

    public bool HasEnoughDiskSpace(string destinationPath, long requiredBytes, out long availableBytes)
    {
        if (requiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        availableBytes = GetAvailableFreeSpace(destinationPath);
        return availableBytes >= requiredBytes;
    }

    public long GetAvailableFreeSpace(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException($"Could not determine the drive for '{fullPath}'.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>
    /// Copies a directory to a sibling staging path, then publishes it with one
    /// directory move. The destination is never partially populated.
    /// </summary>
    public async Task CopyDirectoryTransactionalAsync(
        string sourceDirectory,
        string destinationDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeExistingDirectory(sourceDirectory);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        ValidateCopyRelationship(source, destination);

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"The destination already exists: '{destination}'.");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new IOException($"The destination has no parent directory: '{destination}'.");
        Directory.CreateDirectory(parent);

        var snapshot = await CreateSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        var space = await CheckDiskSpaceAsync(destination, snapshot.TotalBytes, cancellationToken).ConfigureAwait(false);
        if (!space.HasEnoughSpace)
        {
            throw new InsufficientDiskSpaceException(space.RequiredBytes, space.AvailableBytes);
        }

        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid():N}");
        try
        {
            await CopySnapshotAsync(snapshot, staging, false, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Copies into an existing or new directory and reports actual bytes written.</summary>
    public async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeExistingDirectory(sourceDirectory);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        ValidateCopyRelationship(source, destination);
        var snapshot = await CreateSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        await CopySnapshotAsync(snapshot, destination, overwrite, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyFileAsync(
        string sourceFile,
        string destinationFile,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var source = Path.GetFullPath(sourceFile);
        var destination = Path.GetFullPath(destinationFile);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException($"The destination file has no parent: '{destination}'.");
        Directory.CreateDirectory(parent);

        await using var input = OpenSourceFile(source);
        await using var output = OpenDestinationFile(destination, overwrite);
        await input.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    public async Task AtomicWriteJsonAsync<T>(
        string destinationFile,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var destination = Path.GetFullPath(destinationFile);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException($"The destination file has no parent: '{destination}'.");
        Directory.CreateDirectory(parent);

        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task AtomicWriteTextAsync(
        string destinationFile,
        string contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var destination = Path.GetFullPath(destinationFile);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException($"The destination file has no parent: '{destination}'.");
        Directory.CreateDirectory(parent);

        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public string GetSafeDestinationPath(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A relative path was expected.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathInside(root, combined))
        {
            throw new InvalidDataException($"The path escapes its allowed root: '{relativePath}'.");
        }

        return combined;
    }

    public bool IsPathInside(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DeleteDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        await Task.Run(() => DeleteDirectoryCore(fullPath, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public void TryDeleteDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directoryPath))
            {
                DeleteDirectoryCore(Path.GetFullPath(directoryPath), CancellationToken.None);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<DirectorySnapshot> CreateSnapshotAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var root = NormalizeExistingDirectory(directoryPath);
            RejectReparsePoint(root);

            var files = new List<FileSnapshot>();
            var directories = new List<string> { string.Empty };
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();

                foreach (var childDirectory in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(childDirectory);
                    directories.Add(Path.GetRelativePath(root, childDirectory));
                    pending.Push(childDirectory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(file);
                    var info = new FileInfo(file);
                    files.Add(new FileSnapshot(file, Path.GetRelativePath(root, file), info.Length));
                }
            }

            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
            directories.Sort(StringComparer.OrdinalIgnoreCase);
            return new DirectorySnapshot(root, directories, files);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopySnapshotAsync(
        DirectorySnapshot snapshot,
        string destinationRoot,
        bool overwrite,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var relativeDirectory in snapshot.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relativeDirectory.Length > 0)
            {
                Directory.CreateDirectory(Path.Combine(destinationRoot, relativeDirectory));
            }
        }

        long completed = 0;
        progress?.Report(new OperationProgress("CopyingGameFiles", completed, snapshot.TotalBytes));

        var buffer = new byte[CopyBufferSize];
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var input = OpenSourceFile(file.FullPath);
            await using var output = OpenDestinationFile(destination, overwrite);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                completed += read;
                progress?.Report(new OperationProgress(
                    "CopyingGameFiles", completed, snapshot.TotalBytes, file.RelativePath));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file.FullPath));
            var attributes = File.GetAttributes(file.FullPath) & ~FileAttributes.ReparsePoint;
            File.SetAttributes(destination, attributes);
        }

        progress?.Report(new OperationProgress("CopyingGameFiles", snapshot.TotalBytes, snapshot.TotalBytes));
    }

    private static FileStream OpenSourceFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        CopyBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenDestinationFile(string path, bool overwrite) => new(
        path,
        overwrite ? FileMode.Create : FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        CopyBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string NormalizeExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: '{fullPath}'.");
        }

        return fullPath;
    }

    private static void ValidateCopyRelationship(string source, string destination)
    {
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The copy destination cannot be the source directory or one of its children.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Reparse points are not copied for safety: '{path}'.");
        }
    }

    private static void DeleteDirectoryCore(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, false);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(path))
        {
            DeleteDirectoryCore(child, cancellationToken);
        }

        var directoryAttributes = File.GetAttributes(path);
        if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, directoryAttributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, false);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record FileSnapshot(string FullPath, string RelativePath, long Length);

    private sealed record DirectorySnapshot(
        string Root,
        IReadOnlyList<string> Directories,
        IReadOnlyList<FileSnapshot> Files)
    {
        public long TotalBytes => Files.Sum(file => file.Length);
    }
}

public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException(long requiredBytes, long availableBytes)
        : base($"Not enough disk space. Required: {requiredBytes} bytes; available: {availableBytes} bytes.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
}
