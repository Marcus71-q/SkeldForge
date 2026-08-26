using System.IO.Compression;

namespace SkeldForge.Core;

/// <summary>
/// Normalizes user-selected packages to the ZIP-based analysis pipeline. A loose
/// DLL becomes a temporary one-file archive and is therefore subjected to the
/// same hashing, metadata inspection, review, conflict and ownership rules.
/// </summary>
public sealed class ModPackageInput : IDisposable
{
    private ModPackageInput(string archivePath, string? temporaryDirectory)
    {
        ArchivePath = archivePath;
        _temporaryDirectory = temporaryDirectory;
    }

    private readonly string? _temporaryDirectory;
    public string ArchivePath { get; }

    public static ModPackageInput Prepare(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Mod package was not found.", fullPath);

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return new ModPackageInput(fullPath, null);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .zip and .dll mod packages are supported.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "SkeldForge", "LoosePackages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var archivePath = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(fullPath) + ".zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(Path.GetFileName(fullPath), CompressionLevel.Optimal);
            using var source = File.OpenRead(fullPath);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }

        return new ModPackageInput(archivePath, temporaryDirectory);
    }

    public void Dispose()
    {
        if (_temporaryDirectory is null) return;
        try { Directory.Delete(_temporaryDirectory, recursive: true); }
        catch { }
    }
}
