using System.Text.Json;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Owns immutable package copies and their metadata under SkeldForge's per-user mod
/// library. Package hashes are SHA-256 content identifiers, not publisher signatures.
/// </summary>
public sealed class ModLibraryService
{
    private const string MetadataFileName = "mod.json";
    private const string ArchiveFileName = "package.zip";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ModPackageAnalyzer _analyzer;
    private readonly HashService _hashService;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModLibraryService(
        string libraryRoot,
        ModPackageAnalyzer? analyzer = null,
        HashService? hashService = null,
        LoggingService? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        LibraryRoot = Path.GetFullPath(libraryRoot);
        _analyzer = analyzer ?? new ModPackageAnalyzer();
        _hashService = hashService ?? new HashService();
        _logger = logger;
    }

    public ModLibraryService(
        AppPaths paths,
        ModPackageAnalyzer? analyzer = null,
        HashService? hashService = null,
        LoggingService? logger = null)
        : this(paths?.ModLibraryDirectory ?? throw new ArgumentNullException(nameof(paths)), analyzer, hashService, logger)
    {
    }

    public string LibraryRoot { get; }

    public async Task<OperationResult<ModLibraryImportResult>> ImportAsync(
        string archivePath,
        ModPackageAnalysis? analysis = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var sourcePath = Path.GetFullPath(archivePath);
        analysis ??= await _analyzer.AnalyzeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!analysis.IsSafe)
        {
            return OperationResult<ModLibraryImportResult>.Fail(
                "ModPackageUnsafe",
                analysis.RejectionReason ?? "Package analysis did not pass security validation.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(LibraryRoot);
            var duplicate = await FindByHashCoreAsync(analysis.Sha256, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await LogInfoAsync($"Reused duplicate mod-library package hash {analysis.Sha256}.")
                    .ConfigureAwait(false);
                return OperationResult<ModLibraryImportResult>.Ok(new ModLibraryImportResult
                {
                    Mod = duplicate,
                    IsDuplicate = true
                });
            }

            var modId = Guid.NewGuid().ToString("N");
            var finalDirectory = GetContainedLibraryDirectory(modId);
            var stagingRoot = Path.Combine(LibraryRoot, ".staging");
            Directory.CreateDirectory(stagingRoot);
            stagingDirectory = Path.Combine(stagingRoot, modId);
            Directory.CreateDirectory(stagingDirectory);

            var stagedArchive = Path.Combine(stagingDirectory, ArchiveFileName);
            await CopyFileAsync(sourcePath, stagedArchive, cancellationToken).ConfigureAwait(false);
            var copiedHash = await _hashService.ComputeFileSha256Async(stagedArchive, cancellationToken).ConfigureAwait(false);
            if (!copiedHash.Equals(analysis.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<ModLibraryImportResult>.Fail(
                    "ModPackageChangedDuringImport",
                    "The archive content changed between analysis and the library copy.");
            }

            var mod = new ModInfo
            {
                Id = modId,
                Name = analysis.DetectedName,
                Version = analysis.DetectedVersion,
                PackageHash = copiedHash,
                SourceArchiveName = Path.GetFileName(sourcePath),
                LibraryArchivePath = Path.Combine(finalDirectory, ArchiveFileName),
                ImportDate = DateTimeOffset.UtcNow,
                IsEnabled = true,
                NameConfidence = analysis.NameConfidence,
                VersionConfidence = analysis.VersionConfidence,
                Compatibility = ModCompatibilityStatus.Unknown,
                Dependencies = analysis.DetectedDependencies.Select(CloneDependency).ToList()
            };
            await WriteJsonAsync(Path.Combine(stagingDirectory, MetadataFileName), mod, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, finalDirectory);
            stagingDirectory = null;
            await LogInfoAsync($"Imported mod package '{mod.Name}' ({mod.PackageHash}) into the library.")
                .ConfigureAwait(false);
            return OperationResult<ModLibraryImportResult>.Ok(new ModLibraryImportResult
            {
                Mod = mod,
                IsDuplicate = false
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            await LogErrorAsync("Mod-library import was denied.", ex).ConfigureAwait(false);
            return OperationResult<ModLibraryImportResult>.Fail("ModLibraryAccessDenied", ex.Message);
        }
        catch (IOException ex)
        {
            await LogErrorAsync("Mod-library import failed.", ex).ConfigureAwait(false);
            return OperationResult<ModLibraryImportResult>.Fail("ModLibraryImportFailed", ex.Message);
        }
        catch (JsonException ex)
        {
            await LogErrorAsync("Mod-library metadata serialization failed.", ex).ConfigureAwait(false);
            return OperationResult<ModLibraryImportResult>.Fail("ModLibraryMetadataInvalid", ex.Message);
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                TryDeleteDirectory(stagingDirectory);
            }

            _gate.Release();
        }
    }

    public Task<OperationResult<ModLibraryImportResult>> ImportPackageAsync(
        string archivePath,
        ModPackageAnalysis? analysis = null,
        CancellationToken cancellationToken = default) => ImportAsync(archivePath, analysis, cancellationToken);

    public async Task<IReadOnlyList<ModInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModInfo?> FindByHashAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (!IsSha256(sha256))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindByHashCoreAsync(sha256, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModInfo?> GetByIdAsync(string modId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(modId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadMetadataAsync(GetContainedLibraryDirectory(modId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> UpdateMetadataAsync(ModInfo mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!IsSafeId(mod.Id))
        {
            return OperationResult.Fail("ModLibraryIdInvalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetContainedLibraryDirectory(mod.Id);
            if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, ArchiveFileName)))
            {
                return OperationResult.Fail("ModLibraryEntryMissing");
            }

            var libraryCopy = CloneMod(mod);
            libraryCopy.LibraryArchivePath = Path.Combine(directory, ArchiveFileName);
            libraryCopy.InstalledAt = null;
            libraryCopy.InstalledFiles = [];
            await WriteJsonAtomicallyAsync(
                    Path.Combine(directory, MetadataFileName),
                    libraryCopy,
                    cancellationToken)
                .ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Fail("ModLibraryMetadataWriteFailed", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> RemoveAsync(string modId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(modId))
        {
            return OperationResult.Fail("ModLibraryIdInvalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetContainedLibraryDirectory(modId);
            if (!Directory.Exists(directory))
            {
                return OperationResult.Fail("ModLibraryEntryMissing");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, true);
            return OperationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("ModLibraryRemoveFailed", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModInfo?> FindByHashCoreAsync(string sha256, CancellationToken cancellationToken)
    {
        if (!IsSha256(sha256))
        {
            return null;
        }

        var mods = await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        return mods.FirstOrDefault(x => x.PackageHash.Equals(sha256, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ModInfo>> ReadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(LibraryRoot))
        {
            return [];
        }

        var result = new List<ModInfo>();
        foreach (var directory in Directory.EnumerateDirectories(LibraryRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var mod = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
            if (mod is not null)
            {
                result.Add(mod);
            }
        }

        return result.OrderByDescending(x => x.ImportDate).ToArray();
    }

    private static async Task<ModInfo?> ReadMetadataAsync(string directory, CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var archivePath = Path.Combine(directory, ArchiveFileName);
        if (!File.Exists(metadataPath) || !File.Exists(archivePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var mod = await JsonSerializer.DeserializeAsync<ModInfo>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (mod is null || !IsSafeId(mod.Id) || !IsSha256(mod.PackageHash))
            {
                return null;
            }

            mod.LibraryArchivePath = archivePath;
            mod.InstalledAt = null;
            mod.InstalledFiles = [];
            return mod;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetContainedLibraryDirectory(string id)
    {
        if (!IsSafeId(id))
        {
            throw new ArgumentException("Library identifier is invalid.", nameof(id));
        }

        var target = Path.GetFullPath(Path.Combine(LibraryRoot, id));
        var prefix = Path.EndsInDirectorySeparator(LibraryRoot)
            ? LibraryRoot
            : LibraryRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Library path escaped its configured root.");
        }

        return target;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteJsonAsync(temporary, value, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsSafeId(string value) =>
        value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static DependencyInfo CloneDependency(DependencyInfo value) => new()
    {
        Name = value.Name,
        Status = value.Status,
        Evidence = value.Evidence,
        Confidence = value.Confidence
    };

    private static ModInfo CloneMod(ModInfo value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Version = value.Version,
        PackageHash = value.PackageHash,
        SourceArchiveName = value.SourceArchiveName,
        LibraryArchivePath = value.LibraryArchivePath,
        ImportDate = value.ImportDate,
        InstalledAt = value.InstalledAt,
        IsEnabled = value.IsEnabled,
        NameConfidence = value.NameConfidence,
        VersionConfidence = value.VersionConfidence,
        Compatibility = value.Compatibility,
        Dependencies = value.Dependencies.Select(CloneDependency).ToList(),
        InstalledFiles = value.InstalledFiles.Select(x => new InstalledFileRecord
        {
            RelativePath = x.RelativePath,
            Sha256 = x.Sha256,
            Size = x.Size,
            IsShared = x.IsShared,
            IsCurrentlyApplied = x.IsCurrentlyApplied,
            PreserveOnRemove = x.PreserveOnRemove
        }).ToList()
    };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // The caller receives the primary import failure. A later library scan ignores .staging.
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
