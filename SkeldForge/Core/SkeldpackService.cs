using System.IO.Compression;
using System.Text.Json;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Reads and writes the metadata-only .skeldpack exchange format. The archive contains
/// exactly one manifest.json entry—never game files or third-party mod archives.
/// </summary>
public sealed class SkeldpackService
{
    private const string ManifestEntryName = "manifest.json";
    private const long MaximumManifestBytes = 1024 * 1024;
    private readonly ZipService _zipService;
    private readonly LoggingService? _logger;

    public SkeldpackService(ZipService? zipService = null, LoggingService? logger = null)
    {
        _zipService = zipService ?? new ZipService();
        _logger = logger;
    }

    public async Task<OperationResult<SkeldpackManifest>> ExportAsync(
        ModpackInfo modpack,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        if (!Path.GetExtension(destination).Equals(".skeldpack", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<SkeldpackManifest>.Fail("SkeldpackExtensionInvalid");
        }

        var manifest = new SkeldpackManifest
        {
            Name = modpack.Name,
            Version = modpack.Version,
            ExportedAt = DateTimeOffset.UtcNow,
            Mods = modpack.Mods.Select(x => new SkeldpackModReference
            {
                Name = x.Name,
                Version = x.Version,
                Hash = x.PackageHash,
                Enabled = x.IsEnabled
            }).ToList()
        };
        var validation = ValidateManifest(manifest);
        if (!validation.Success)
        {
            return OperationResult<SkeldpackManifest>.Fail(validation.ErrorKey, validation.TechnicalMessage);
        }

        var parent = Path.GetDirectoryName(destination);
        if (parent is null)
        {
            return OperationResult<SkeldpackManifest>.Fail("SkeldpackDestinationInvalid");
        }

        Directory.CreateDirectory(parent);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                entry.LastWriteTime = DateTimeOffset.Now;
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            await LogInfoAsync($"Exported metadata-only skeldpack '{manifest.Name}'.").ConfigureAwait(false);
            return OperationResult<SkeldpackManifest>.Ok(manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            await LogErrorAsync("Skeldpack export failed.", ex).ConfigureAwait(false);
            return OperationResult<SkeldpackManifest>.Fail("SkeldpackExportFailed", ex.Message);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task<OperationResult<SkeldpackImportPreview>> PreviewImportAsync(
        string skeldpackPath,
        ModLibraryService library,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        var manifestResult = await ReadManifestAsync(skeldpackPath, cancellationToken).ConfigureAwait(false);
        if (!manifestResult.Success || manifestResult.Value is null)
        {
            return OperationResult<SkeldpackImportPreview>.Fail(
                manifestResult.ErrorKey, manifestResult.TechnicalMessage);
        }

        var libraryMods = await library.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byHash = libraryMods
            .GroupBy(x => x.PackageHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var preview = new SkeldpackImportPreview { Manifest = manifestResult.Value };
        foreach (var reference in manifestResult.Value.Mods)
        {
            if (byHash.TryGetValue(reference.Hash, out var existing))
            {
                var found = CloneMod(existing);
                found.IsEnabled = reference.Enabled;
                preview.FoundMods.Add(found);
            }
            else
            {
                preview.MissingMods.Add(new SkeldpackModReference
                {
                    Name = reference.Name,
                    Version = reference.Version,
                    Hash = reference.Hash,
                    Enabled = reference.Enabled
                });
            }
        }

        return OperationResult<SkeldpackImportPreview>.Ok(preview);
    }

    public async Task<OperationResult<SkeldpackManifest>> ReadManifestAsync(
        string skeldpackPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skeldpackPath);
        var path = Path.GetFullPath(skeldpackPath);
        if (!File.Exists(path))
        {
            return OperationResult<SkeldpackManifest>.Fail("SkeldpackNotFound", path);
        }

        try
        {
            var entries = _zipService.ValidateArchive(path);
            var files = entries.Where(x => !x.IsDirectory).ToArray();
            if (files.Length != 1 ||
                !files[0].RelativePath.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase) ||
                files[0].UncompressedSize is <= 0 or > MaximumManifestBytes)
            {
                return OperationResult<SkeldpackManifest>.Fail(
                    "SkeldpackPayloadInvalid",
                    "A metadata-only skeldpack must contain exactly one small manifest.json file.");
            }

            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.Single(x =>
                x.FullName.Replace('\\', '/').Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase));
            await using var stream = entry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<SkeldpackManifest>(
                    stream, JsonDefaults.Options, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                return OperationResult<SkeldpackManifest>.Fail("SkeldpackManifestInvalid");
            }

            var validation = ValidateManifest(manifest);
            if (!validation.Success)
            {
                return OperationResult<SkeldpackManifest>.Fail(validation.ErrorKey, validation.TechnicalMessage);
            }

            return OperationResult<SkeldpackManifest>.Ok(manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            await LogErrorAsync("Skeldpack import validation failed.", ex).ConfigureAwait(false);
            return OperationResult<SkeldpackManifest>.Fail("SkeldpackImportFailed", ex.Message);
        }
    }

    public OperationResult ValidateManifest(SkeldpackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != SkeldForgeVersion.SkeldpackFormat)
        {
            return OperationResult.Fail("SkeldpackFormatUnsupported", manifest.FormatVersion.ToString());
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 100)
        {
            return OperationResult.Fail("SkeldpackManifestInvalid", "Name or version is missing/too long.");
        }

        if (manifest.Mods.Count > 10_000)
        {
            return OperationResult.Fail("SkeldpackManifestInvalid", "The mod reference count is unreasonable.");
        }

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Name) || mod.Name.Length > 200 ||
                string.IsNullOrWhiteSpace(mod.Version) || mod.Version.Length > 100 ||
                mod.Hash.Length != 64 || !mod.Hash.All(Uri.IsHexDigit) || !hashes.Add(mod.Hash))
            {
                return OperationResult.Fail("SkeldpackManifestInvalid",
                    "Every mod needs a unique, valid SHA-256 reference, name, and version.");
            }
        }

        return OperationResult.Ok();
    }

    private static ModInfo CloneMod(ModInfo value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Version = value.Version,
        PackageHash = value.PackageHash,
        SourceArchiveName = value.SourceArchiveName,
        LibraryArchivePath = value.LibraryArchivePath,
        ImportDate = value.ImportDate,
        IsEnabled = value.IsEnabled,
        NameConfidence = value.NameConfidence,
        VersionConfidence = value.VersionConfidence,
        Compatibility = value.Compatibility,
        Dependencies = value.Dependencies.Select(x => new DependencyInfo
        {
            Name = x.Name,
            Status = x.Status,
            Evidence = x.Evidence,
            Confidence = x.Confidence
        }).ToList()
    };

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
}
