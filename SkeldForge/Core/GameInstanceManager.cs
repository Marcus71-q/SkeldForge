using System.Text.Json;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Owns isolated game instances. Every mutating operation is staged outside the
/// published instance directory and committed with same-volume directory moves.
/// </summary>
public sealed class GameInstanceManager
{
    public const string MetadataFileName = "modpack.json";
    public const string GameFolderName = "Game";

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly AppPaths _paths;
    private readonly FileSystemService _fileSystem;
    private readonly GameDetector _gameDetector;
    private readonly VersionManager _versionManager;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public GameInstanceManager(
        AppPaths paths,
        FileSystemService? fileSystem = null,
        GameDetector? gameDetector = null,
        VersionManager? versionManager = null,
        LoggingService? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? new FileSystemService();
        _versionManager = versionManager ?? new VersionManager();
        _gameDetector = gameDetector ?? new GameDetector(_versionManager);
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModpackInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task<IReadOnlyList<ModpackInfo>> LoadAsync(CancellationToken cancellationToken = default)
        => GetAllAsync(cancellationToken);

    public async Task<ModpackInfo?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindByIdCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<OperationResult<ModpackInfo>> CreateAsync(
        string name,
        string sourceGamePath,
        string modpackVersion = "1.0",
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            var normalizedName = NormalizeName(name, out var nameError);
            if (nameError is not null)
            {
                return OperationResult<ModpackInfo>.Fail(nameError);
            }

            var sourceResult = await _gameDetector
                .DetectInstallationAsync(sourceGamePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!sourceResult.Success || sourceResult.Value is null)
            {
                return OperationResult<ModpackInfo>.Fail(sourceResult.ErrorKey, sourceResult.TechnicalMessage);
            }

            var source = sourceResult.Value;
            if (_fileSystem.IsPathInside(_paths.InstancesDirectory, source.Path))
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.SourceMustBeVanilla",
                    "An existing SkeldForge instance cannot be used as the vanilla source.");
            }

            if (StorageWritesInsideSource(source.Path))
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.StorageOverlapsVanilla",
                    "SkeldForge's data, backup, or configuration root cannot be inside the vanilla game directory.");
            }

            _paths.EnsureDataDirectories();
            if (await NameExistsCoreAsync(normalizedName, null, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
            }

            var destination = GetInstanceDirectory(normalizedName);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
            }

            var sourceSize = await _fileSystem.GetDirectorySizeAsync(source.Path, cancellationToken).ConfigureAwait(false);
            var diskSpace = await _fileSystem.CheckDiskSpaceAsync(destination, sourceSize, cancellationToken).ConfigureAwait(false);
            if (!diskSpace.HasEnoughSpace)
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.NotEnoughDiskSpace",
                    $"Required {diskSpace.RequiredBytes} bytes; available {diskSpace.AvailableBytes} bytes.");
            }

            stagingDirectory = GetStagingDirectory(normalizedName, "creating");
            var stagedGame = Path.Combine(stagingDirectory, GameFolderName);
            Directory.CreateDirectory(stagingDirectory);
            await _fileSystem.CopyDirectoryAsync(
                source.Path, stagedGame, false, progress, cancellationToken).ConfigureAwait(false);

            var stagedValidation = _gameDetector.Validate(stagedGame);
            if (!stagedValidation.IsValid)
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.CopyValidationFailed",
                    $"The staged copy is missing: {string.Join(", ", stagedValidation.MissingItems)}");
            }

            var sourceAfterCopyFingerprint = await _versionManager
                .ComputeGameFingerprintAsync(source.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceAfterCopyFingerprint.Equals(source.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.SourceChangedDuringCopy",
                    "The vanilla source changed while it was being copied.");
            }

            var sourceContentFingerprint = await _versionManager
                .ComputeGameContentFingerprintAsync(source.Path, cancellationToken)
                .ConfigureAwait(false);
            var copiedContentFingerprint = await _versionManager
                .ComputeGameContentFingerprintAsync(stagedGame, cancellationToken)
                .ConfigureAwait(false);
            if (!copiedContentFingerprint.Equals(sourceContentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.CopyValidationFailed",
                    "The staged copy did not match the vanilla source.");
            }

			// The selected Steam folder may itself have been modded. An isolated
			// instance must inherit the working BepInEx runtime, but never untracked
			// plug-ins/configuration from the source; otherwise installing the same
			// mod through SkeldForge is falsely reported as a file conflict.
			RemoveSourceModPayload(stagedGame);

            var now = DateTimeOffset.UtcNow;
            var modpack = new ModpackInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedName,
                Version = NormalizeVersion(modpackVersion),
                InstanceDirectory = destination,
                GameDirectory = Path.Combine(destination, GameFolderName),
                SourceGamePath = source.Path,
                SourceGameVersion = source.DisplayVersion,
                SourceGameBuildId = source.BuildId,
                SourceGameFingerprint = source.Fingerprint,
                CreatedWithSkeldForge = SkeldForgeVersion.Current,
                CreatedAt = now,
                UpdatedAt = now,
                Mods = []
            };

            await _fileSystem.AtomicWriteJsonAsync(
                Path.Combine(stagingDirectory, MetadataFileName),
                modpack,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destination);
            stagingDirectory = null;
            progress?.Report(new OperationProgress("InstanceReady", sourceSize, sourceSize));
            await LogInfoAsync($"Created isolated modpack instance '{modpack.Name}' ({modpack.Id}).").ConfigureAwait(false);
            return OperationResult<ModpackInfo>.Ok(modpack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            await LogErrorAsync("Modpack instance creation failed.", exception).ConfigureAwait(false);
            return MapFailure<ModpackInfo>(exception, "Instance.CreateFailed");
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                _fileSystem.TryDeleteDirectory(stagingDirectory);
            }

            _mutationGate.Release();
        }
    }

    public Task<OperationResult<ModpackInfo>> CreateInstanceAsync(
        string name,
        string sourceGamePath,
        string modpackVersion = "1.0",
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => CreateAsync(name, sourceGamePath, modpackVersion, progress, cancellationToken);

    public async Task<OperationResult<ModpackInfo>> DuplicateAsync(
        string sourceId,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            var source = await FindByIdCoreAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NotFound");
            }

            var normalizedName = NormalizeName(newName, out var nameError);
            if (nameError is not null)
            {
                return OperationResult<ModpackInfo>.Fail(nameError);
            }

            if (await NameExistsCoreAsync(normalizedName, null, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
            }

            if (!_gameDetector.Validate(source.GameDirectory).IsValid)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.Corrupted");
            }

            var destination = GetInstanceDirectory(normalizedName);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
            }

            var size = await _fileSystem.GetDirectorySizeAsync(source.InstanceDirectory, cancellationToken).ConfigureAwait(false);
            var diskSpace = await _fileSystem.CheckDiskSpaceAsync(destination, size, cancellationToken).ConfigureAwait(false);
            if (!diskSpace.HasEnoughSpace)
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.NotEnoughDiskSpace",
                    $"Required {diskSpace.RequiredBytes} bytes; available {diskSpace.AvailableBytes} bytes.");
            }

            stagingDirectory = GetStagingDirectory(normalizedName, "duplicating");
            await _fileSystem.CopyDirectoryAsync(
                source.InstanceDirectory, stagingDirectory, false, progress, cancellationToken).ConfigureAwait(false);

            source.Id = Guid.NewGuid().ToString("N");
            source.Name = normalizedName;
            source.InstanceDirectory = destination;
            source.GameDirectory = Path.Combine(destination, GameFolderName);
            source.CreatedAt = DateTimeOffset.UtcNow;
            source.UpdatedAt = source.CreatedAt;
            source.LastLaunchedAt = null;

            await _fileSystem.AtomicWriteJsonAsync(
                Path.Combine(stagingDirectory, MetadataFileName),
                source,
                cancellationToken).ConfigureAwait(false);

            var stagedValidation = _gameDetector.Validate(Path.Combine(stagingDirectory, GameFolderName));
            if (!stagedValidation.IsValid)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.CopyValidationFailed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destination);
            stagingDirectory = null;
            await LogInfoAsync($"Duplicated modpack instance as '{source.Name}' ({source.Id}).").ConfigureAwait(false);
            return OperationResult<ModpackInfo>.Ok(source);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            await LogErrorAsync("Modpack duplication failed.", exception).ConfigureAwait(false);
            return MapFailure<ModpackInfo>(exception, "Instance.DuplicateFailed");
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                _fileSystem.TryDeleteDirectory(stagingDirectory);
            }

            _mutationGate.Release();
        }
    }

    public Task<OperationResult<ModpackInfo>> DuplicateInstanceAsync(
        string sourceId,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => DuplicateAsync(sourceId, newName, progress, cancellationToken);

    public async Task<OperationResult<ModpackInfo>> RenameAsync(
        string id,
        string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modpack = await FindByIdCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if (modpack is null)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NotFound");
            }

            var normalizedName = NormalizeName(newName, out var nameError);
            if (nameError is not null)
            {
                return OperationResult<ModpackInfo>.Fail(nameError);
            }

            if (await NameExistsCoreAsync(normalizedName, id, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
            }

            EnsureManagedInstancePath(modpack.InstanceDirectory);
            var oldDirectory = modpack.InstanceDirectory;
            var newDirectory = GetInstanceDirectory(normalizedName);
            var oldName = modpack.Name;
            var oldGameDirectory = modpack.GameDirectory;
            string? intermediate = null;
            var moved = false;

            try
            {
                if (!oldDirectory.Equals(newDirectory, StringComparison.Ordinal))
                {
                    if (Directory.Exists(newDirectory) &&
                        !oldDirectory.Equals(newDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return OperationResult<ModpackInfo>.Fail("Instance.NameExists");
                    }

                    intermediate = GetStagingDirectory(normalizedName, "renaming");
                    Directory.Move(oldDirectory, intermediate);
                    Directory.Move(intermediate, newDirectory);
                    intermediate = null;
                    moved = true;
                }

                modpack.Name = normalizedName;
                modpack.InstanceDirectory = newDirectory;
                modpack.GameDirectory = Path.Combine(newDirectory, GameFolderName);
                modpack.UpdatedAt = DateTimeOffset.UtcNow;
                await _fileSystem.AtomicWriteJsonAsync(
                    Path.Combine(newDirectory, MetadataFileName), modpack, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                modpack.Name = oldName;
                modpack.InstanceDirectory = oldDirectory;
                modpack.GameDirectory = oldGameDirectory;
                if (intermediate is not null && Directory.Exists(intermediate) && !Directory.Exists(oldDirectory))
                {
                    Directory.Move(intermediate, oldDirectory);
                }
                else if (moved && Directory.Exists(newDirectory) && !Directory.Exists(oldDirectory))
                {
                    Directory.Move(newDirectory, oldDirectory);
                }

                throw;
            }

            await LogInfoAsync($"Renamed modpack instance '{oldName}' to '{modpack.Name}' ({modpack.Id}).").ConfigureAwait(false);
            return OperationResult<ModpackInfo>.Ok(modpack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            await LogErrorAsync("Modpack rename failed.", exception).ConfigureAwait(false);
            return MapFailure<ModpackInfo>(exception, "Instance.RenameFailed");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task<OperationResult<ModpackInfo>> RenameInstanceAsync(
        string id,
        string newName,
        CancellationToken cancellationToken = default)
        => RenameAsync(id, newName, cancellationToken);

    public async Task<OperationResult> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? quarantine = null;
        string? originalDirectory = null;
        var deletionStarted = false;
        try
        {
            var modpack = await FindByIdCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if (modpack is null)
            {
                return OperationResult.Fail("Instance.NotFound");
            }

            EnsureManagedInstancePath(modpack.InstanceDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            originalDirectory = modpack.InstanceDirectory;
            quarantine = GetStagingDirectory(modpack.Name, "deleting");
            Directory.Move(originalDirectory, quarantine);

            // The destructive boundary has been crossed. Finish deleting rather than
            // allowing cancellation to leave a partially removed visible instance.
            deletionStarted = true;
            await _fileSystem.DeleteDirectoryAsync(quarantine, CancellationToken.None).ConfigureAwait(false);
            quarantine = null;
            await LogInfoAsync($"Deleted modpack instance '{modpack.Name}' ({modpack.Id}); the vanilla source was untouched.")
                .ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            if (!deletionStarted && quarantine is not null && originalDirectory is not null &&
                Directory.Exists(quarantine) && !Directory.Exists(originalDirectory))
            {
                try
                {
                    Directory.Move(quarantine, originalDirectory);
                    quarantine = null;
                }
                catch (Exception rollbackException) when (IsExpectedFileSystemException(rollbackException))
                {
                    await LogErrorAsync("Instance deletion rollback also failed.", rollbackException).ConfigureAwait(false);
                }
            }

            else if (deletionStarted && quarantine is not null && Directory.Exists(quarantine))
            {
                await LogErrorAsync(
                    $"Instance deletion stopped after the destructive boundary; remaining files stay quarantined at '{quarantine}'.",
                    exception).ConfigureAwait(false);
            }

            await LogErrorAsync("Modpack deletion failed.", exception).ConfigureAwait(false);
            return MapFailure(exception, "Instance.DeleteFailed");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task<OperationResult> DeleteInstanceAsync(string id, CancellationToken cancellationToken = default)
        => DeleteAsync(id, cancellationToken);

    /// <summary>
    /// Refreshes the vanilla base in a staging directory and reapplies files owned
    /// by enabled mods. The live instance is swapped only after full validation.
    /// </summary>
    public async Task<OperationResult<ModpackInfo>> UpdateAsync(
        string id,
        string sourceGamePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        string? rollbackDirectory = null;
        string? liveDirectory = null;
        try
        {
            var modpack = await FindByIdCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if (modpack is null)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NotFound");
            }

            EnsureManagedInstancePath(modpack.InstanceDirectory);
            liveDirectory = modpack.InstanceDirectory;
            if (!_gameDetector.Validate(modpack.GameDirectory).IsValid)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.Corrupted");
            }

            var sourceResult = await _gameDetector
                .DetectInstallationAsync(sourceGamePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!sourceResult.Success || sourceResult.Value is null)
            {
                return OperationResult<ModpackInfo>.Fail(sourceResult.ErrorKey, sourceResult.TechnicalMessage);
            }

            var source = sourceResult.Value;
            if (_fileSystem.IsPathInside(_paths.InstancesDirectory, source.Path))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.SourceMustBeVanilla");
            }

            if (StorageWritesInsideSource(source.Path))
            {
                return OperationResult<ModpackInfo>.Fail("Instance.StorageOverlapsVanilla");
            }

            var enabledButUnapplied = modpack.Mods
                .Where(mod => mod.IsEnabled)
                .FirstOrDefault(mod => mod.InstalledFiles.Any(file => !file.IsCurrentlyApplied));
            if (enabledButUnapplied is not null)
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.ModStateInvalid",
                    $"Enabled mod '{enabledButUnapplied.Name}' has files that are not applied.");
            }

            var ownedFiles = CollectEnabledOwnedFiles(modpack, out var conflict);
            if (conflict is not null)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.ModConflict", conflict);
            }

            long ownedFileBytes = 0;
            foreach (var owned in ownedFiles)
            {
                var oldFile = _fileSystem.GetSafeDestinationPath(modpack.GameDirectory, owned.RelativePath);
                if (!File.Exists(oldFile))
                {
                    return OperationResult<ModpackInfo>.Fail(
                        "Instance.Corrupted",
                        $"An enabled mod file is missing: '{owned.RelativePath}'.");
                }

                ownedFileBytes = checked(ownedFileBytes + new FileInfo(oldFile).Length);
            }

            var sourceSize = await _fileSystem.GetDirectorySizeAsync(source.Path, cancellationToken).ConfigureAwait(false);
            var diskSpace = await _fileSystem
                .CheckDiskSpaceAsync(modpack.InstanceDirectory, checked(sourceSize + ownedFileBytes), cancellationToken)
                .ConfigureAwait(false);
            if (!diskSpace.HasEnoughSpace)
            {
                return OperationResult<ModpackInfo>.Fail(
                    "Instance.NotEnoughDiskSpace",
                    $"Required {diskSpace.RequiredBytes} bytes; available {diskSpace.AvailableBytes} bytes.");
            }

            stagingDirectory = GetStagingDirectory(modpack.Name, "updating");
            var stagedGame = Path.Combine(stagingDirectory, GameFolderName);
            Directory.CreateDirectory(stagingDirectory);
            await _fileSystem.CopyDirectoryAsync(
                source.Path, stagedGame, false, progress, cancellationToken).ConfigureAwait(false);

			RemoveSourceModPayload(stagedGame);

            long reapplied = 0;
            foreach (var owned in ownedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldFile = _fileSystem.GetSafeDestinationPath(modpack.GameDirectory, owned.RelativePath);
                if (!File.Exists(oldFile))
                {
                    return OperationResult<ModpackInfo>.Fail(
                        "Instance.Corrupted",
                        $"An enabled mod file is missing: '{owned.RelativePath}'.");
                }

                var stagedFile = _fileSystem.GetSafeDestinationPath(stagedGame, owned.RelativePath);
                await _fileSystem.CopyFileAsync(oldFile, stagedFile, true, cancellationToken).ConfigureAwait(false);
                reapplied++;
                progress?.Report(new OperationProgress(
                    "ReapplyingMods", reapplied, ownedFiles.Count, owned.RelativePath));
            }

            var stagedValidation = _gameDetector.Validate(stagedGame);
            if (!stagedValidation.IsValid)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.CopyValidationFailed");
            }

            Directory.CreateDirectory(_paths.InstanceMetadataBackupsDirectory);
            var metadataBackup = Path.Combine(
                _paths.InstanceMetadataBackupsDirectory,
                $"{modpack.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            await _fileSystem.AtomicWriteJsonAsync(metadataBackup, modpack, cancellationToken).ConfigureAwait(false);

            modpack.SourceGamePath = source.Path;
            modpack.SourceGameVersion = source.DisplayVersion;
            modpack.SourceGameBuildId = source.BuildId;
            modpack.SourceGameFingerprint = source.Fingerprint;
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var disabledMod in modpack.Mods.Where(mod => !mod.IsEnabled))
            {
                foreach (var file in disabledMod.InstalledFiles)
                {
                    file.IsCurrentlyApplied = false;
                }
            }

            await _fileSystem.AtomicWriteJsonAsync(
                Path.Combine(stagingDirectory, MetadataFileName), modpack, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            rollbackDirectory = GetStagingDirectory(modpack.Name, "rollback");
            Directory.Move(modpack.InstanceDirectory, rollbackDirectory);
            try
            {
                Directory.Move(stagingDirectory, modpack.InstanceDirectory);
                stagingDirectory = null;
            }
            catch
            {
                Directory.Move(rollbackDirectory, modpack.InstanceDirectory);
                rollbackDirectory = null;
                throw;
            }

            _fileSystem.TryDeleteDirectory(rollbackDirectory);
            if (Directory.Exists(rollbackDirectory))
            {
                await LogWarningAsync(
                    $"Updated instance successfully, but the temporary rollback directory remains at '{rollbackDirectory}'.")
                    .ConfigureAwait(false);
            }

            rollbackDirectory = null;
            await LogInfoAsync(
                $"Updated base game for '{modpack.Name}' ({modpack.Id}) and reapplied {ownedFiles.Count} tracked files.")
                .ConfigureAwait(false);
            return OperationResult<ModpackInfo>.Ok(modpack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            if (rollbackDirectory is not null && Directory.Exists(rollbackDirectory))
            {
                try
                {
                    if (liveDirectory is not null && !Directory.Exists(liveDirectory))
                    {
                        Directory.Move(rollbackDirectory, liveDirectory);
                        rollbackDirectory = null;
                    }
                }
                catch (Exception rollbackException) when (IsExpectedFileSystemException(rollbackException))
                {
                    await LogErrorAsync("Instance update rollback failed.", rollbackException).ConfigureAwait(false);
                }
            }

            await LogErrorAsync("Modpack base update failed.", exception).ConfigureAwait(false);
            return MapFailure<ModpackInfo>(exception, "Instance.UpdateFailed");
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                _fileSystem.TryDeleteDirectory(stagingDirectory);
            }

            _mutationGate.Release();
        }
    }

    public Task<OperationResult<ModpackInfo>> UpdateInstanceAsync(
        string id,
        string sourceGamePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => UpdateAsync(id, sourceGamePath, progress, cancellationToken);

    /// <summary>Persists mod metadata while retaining the manager-owned instance paths.</summary>
    public async Task<OperationResult<ModpackInfo>> SaveAsync(
        ModpackInfo modpack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindByIdCoreAsync(modpack.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return OperationResult<ModpackInfo>.Fail("Instance.NotFound");
            }

            modpack.Name = existing.Name;
            modpack.InstanceDirectory = existing.InstanceDirectory;
            modpack.GameDirectory = existing.GameDirectory;
            modpack.SourceGamePath = existing.SourceGamePath;
            modpack.SourceGameVersion = existing.SourceGameVersion;
            modpack.SourceGameBuildId = existing.SourceGameBuildId;
            modpack.SourceGameFingerprint = existing.SourceGameFingerprint;
            modpack.SchemaVersion = SkeldForgeVersion.ModpackSchema;
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            await _fileSystem.AtomicWriteJsonAsync(
                Path.Combine(existing.InstanceDirectory, MetadataFileName), modpack, cancellationToken).ConfigureAwait(false);
            return OperationResult<ModpackInfo>.Ok(modpack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            await LogErrorAsync("Modpack metadata save failed.", exception).ConfigureAwait(false);
            return MapFailure<ModpackInfo>(exception, "Instance.SaveFailed");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task<OperationResult<ModpackInfo>> SaveModpackAsync(
        ModpackInfo modpack,
        CancellationToken cancellationToken = default)
        => SaveAsync(modpack, cancellationToken);

    private async Task<IReadOnlyList<ModpackInfo>> LoadAllCoreAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDataDirectories();
        EnsureInstancesRootSafe();
        var results = new List<ModpackInfo>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.InstancesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    await LogWarningAsync($"Skipped reparse-point instance directory '{name}'.").ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await LogWarningAsync($"Skipped inaccessible instance directory '{name}'.", exception).ConfigureAwait(false);
                continue;
            }

            var metadataPath = Path.Combine(directory, MetadataFileName);
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    metadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    32 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var modpack = await JsonSerializer.DeserializeAsync<ModpackInfo>(
                    stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                if (modpack is null || string.IsNullOrWhiteSpace(modpack.Id) ||
                    modpack.SchemaVersion is <= 0 or > SkeldForgeVersion.ModpackSchema)
                {
                    await LogWarningAsync($"Skipped invalid instance metadata in '{name}'.").ConfigureAwait(false);
                    continue;
                }

                // Never trust persisted paths as deletion or launch authorities.
                modpack.InstanceDirectory = Path.GetFullPath(directory);
                modpack.GameDirectory = Path.Combine(modpack.InstanceDirectory, GameFolderName);
                results.Add(modpack);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                await LogWarningAsync($"Skipped unreadable instance metadata in '{name}'.", exception).ConfigureAwait(false);
            }
        }

        return results
            .OrderByDescending(item => item.LastLaunchedAt ?? item.CreatedAt)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<ModpackInfo?> FindByIdCoreAsync(string id, CancellationToken cancellationToken)
        => (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> NameExistsCoreAsync(
        string name,
        string? excludingId,
        CancellationToken cancellationToken)
        => (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).Any(item =>
            item.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) &&
            !item.Id.Equals(excludingId, StringComparison.OrdinalIgnoreCase));

    private string GetInstanceDirectory(string normalizedName)
        => Path.Combine(_paths.InstancesDirectory, normalizedName);

    private string GetStagingDirectory(string name, string operation)
    {
        var safePart = new string(name.Where(character =>
            !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        if (string.IsNullOrWhiteSpace(safePart))
        {
            safePart = "instance";
        }

        return Path.Combine(_paths.InstancesDirectory, $".{safePart}.{operation}-{Guid.NewGuid():N}");
    }

    private void EnsureManagedInstancePath(string instanceDirectory)
    {
        EnsureInstancesRootSafe();
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        var parent = Directory.GetParent(fullPath)?.FullName;
        if (parent is null ||
            !Path.TrimEndingDirectorySeparator(parent).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.InstancesDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The instance path is outside SkeldForge's managed Instances directory.");
        }


        if (Directory.Exists(fullPath) &&
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse-point instance directories are not managed by SkeldForge.");
        }
    }

    private void EnsureInstancesRootSafe()
    {
        var root = Path.GetFullPath(_paths.InstancesDirectory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The managed Instances root is missing or is a reparse point.");
        }
    }

    private bool StorageWritesInsideSource(string sourceGamePath)
        => _fileSystem.IsPathInside(sourceGamePath, _paths.DataRoot) ||
           _fileSystem.IsPathInside(sourceGamePath, _paths.BackupsDirectory) ||
           _fileSystem.IsPathInside(sourceGamePath, _paths.BootstrapRoot);

	private static void RemoveSourceModPayload(string stagedGame)
	{
		var bepinEx = Path.Combine(stagedGame, "BepInEx");
		if (!Directory.Exists(bepinEx)) return;

		// Keep BepInEx/core, dotnet, winhttp.dll and doorstop_config.ini so the
		// loader remains usable. These directories contain mod-owned state and
		// must begin empty for every independently managed modpack.
		string[] payloadDirectories = ["plugins", "config", "patchers"];
		foreach (var name in payloadDirectories)
		{
			var path = Path.Combine(bepinEx, name);
			if (!Directory.Exists(path)) continue;
			Directory.Delete(path, true);
			Directory.CreateDirectory(path);
		}
	}

    private static string NormalizeName(string? name, out string? errorKey)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            errorKey = "Instance.NameRequired";
            return string.Empty;
        }

        if (normalized.Length > 80 || normalized is "." or ".." ||
            normalized.StartsWith(".", StringComparison.Ordinal) ||
            normalized.EndsWith(' ') || normalized.EndsWith('.') ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            ReservedWindowsNames.Contains(normalized.Split('.')[0]))
        {
            errorKey = "Instance.InvalidName";
            return string.Empty;
        }

        errorKey = null;
        return normalized;
    }

    private static string NormalizeVersion(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 50 ? normalized : "1.0";
    }

    private List<OwnedFile> CollectEnabledOwnedFiles(ModpackInfo modpack, out string? conflict)
    {
        var files = new Dictionary<string, OwnedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in modpack.Mods.Where(item => item.IsEnabled))
        {
            foreach (var record in mod.InstalledFiles.Where(file => file.IsCurrentlyApplied))
            {
                try
                {
                    _ = _fileSystem.GetSafeDestinationPath(modpack.GameDirectory, record.RelativePath);
                }
                catch (InvalidDataException)
                {
                    conflict = $"Mod '{mod.Name}' has an unsafe tracked path: '{record.RelativePath}'.";
                    return [];
                }

                if (files.TryGetValue(record.RelativePath, out var existing))
                {
                    var sameHash = !string.IsNullOrWhiteSpace(existing.Sha256) &&
                        existing.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase);
                    if (!sameHash)
                    {
                        conflict = $"Enabled mods '{existing.ModName}' and '{mod.Name}' both own '{record.RelativePath}'.";
                        return [];
                    }
                }
                else
                {
                    files[record.RelativePath] = new OwnedFile(record.RelativePath, record.Sha256, mod.Name);
                }
            }
        }

        conflict = null;
        return files.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsExpectedFileSystemException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;

    private static OperationResult<T> MapFailure<T>(Exception exception, string fallbackKey)
        => exception is InsufficientDiskSpaceException disk
            ? OperationResult<T>.Fail(
                "Instance.NotEnoughDiskSpace",
                $"Required {disk.RequiredBytes} bytes; available {disk.AvailableBytes} bytes.")
            : OperationResult<T>.Fail(fallbackKey, exception.Message);

    private static OperationResult MapFailure(Exception exception, string fallbackKey)
        => exception is InsufficientDiskSpaceException disk
            ? OperationResult.Fail(
                "Instance.NotEnoughDiskSpace",
                $"Required {disk.RequiredBytes} bytes; available {disk.AvailableBytes} bytes.")
            : OperationResult.Fail(fallbackKey, exception.Message);

    private async Task LogInfoAsync(string message)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.InfoAsync(message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private Task LogWarningAsync(string message, Exception? exception = null)
        => LogSafelyAsync(LogLevel.Warning, message, exception);

    private Task LogErrorAsync(string message, Exception exception)
        => LogSafelyAsync(LogLevel.Error, message, exception);

    private async Task LogSafelyAsync(LogLevel level, string message, Exception? exception)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.LogAsync(level, message, exception).ConfigureAwait(false);
        }
        catch (Exception loggingException) when (loggingException is not OperationCanceledException)
        {
        }
    }

    private sealed record OwnedFile(string RelativePath, string Sha256, string ModName);
}
