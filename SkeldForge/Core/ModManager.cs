using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Applies library packages only to isolated instance directories. Installation is staged,
/// validated, conflict checked, applied, and then committed to modpack metadata. Physical
/// changes are rolled back if the metadata commit fails.
/// </summary>
public sealed class ModManager
{
    private readonly ModPackageAnalyzer _analyzer;
    private readonly ModLibraryService _library;
    private readonly ZipService _zipService;
    private readonly ConflictDetector _conflictDetector;
    private readonly DependencyDetector _dependencyDetector;
    private readonly HashService _hashService;
    private readonly FileSystemService _fileSystem;
    private readonly LoggingService? _logger;
    private readonly Func<ModpackInfo, CancellationToken, Task>? _metadataWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModManager(
        ModPackageAnalyzer analyzer,
        ModLibraryService library,
        ZipService zipService,
        ConflictDetector conflictDetector,
        DependencyDetector dependencyDetector,
        HashService? hashService = null,
        FileSystemService? fileSystem = null,
        LoggingService? logger = null,
        Func<ModpackInfo, CancellationToken, Task>? metadataWriter = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _zipService = zipService ?? throw new ArgumentNullException(nameof(zipService));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _dependencyDetector = dependencyDetector ?? throw new ArgumentNullException(nameof(dependencyDetector));
        _hashService = hashService ?? new HashService();
        _fileSystem = fileSystem ?? new FileSystemService();
        _logger = logger;
        _metadataWriter = metadataWriter;
    }

    public async Task<OperationResult<ModInstallResult>> InstallFromArchiveAsync(
        ModpackInfo modpack,
        string archivePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        var analysis = await _analyzer.AnalyzeAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (!analysis.IsSafe)
        {
            await LogWarningAsync($"Rejected unsafe mod archive '{Path.GetFileName(archivePath)}': {analysis.RejectionReason}")
                .ConfigureAwait(false);
            return FailWithValue("ModPackageUnsafe", analysis.RejectionReason ?? string.Empty, new ModInstallResult
            {
                Analysis = analysis
            });
        }

        var imported = await _library.ImportAsync(archivePath, analysis, cancellationToken).ConfigureAwait(false);
        if (!imported.Success || imported.Value is null)
        {
            return FailWithValue(imported.ErrorKey, imported.TechnicalMessage, new ModInstallResult
            {
                Analysis = analysis
            });
        }

        var installed = await InstallAsync(modpack, imported.Value.Mod, progress, cancellationToken)
            .ConfigureAwait(false);
        if (installed.Value is not null)
        {
            return new OperationResult<ModInstallResult>
            {
                Success = installed.Success,
                ErrorKey = installed.ErrorKey,
                TechnicalMessage = installed.TechnicalMessage,
                Value = new ModInstallResult
                {
                    Analysis = analysis,
                    InstalledMod = installed.Value.InstalledMod,
                    UsedExistingLibraryEntry = imported.Value.IsDuplicate,
                    Conflicts = installed.Value.Conflicts
                }
            };
        }

        return installed;
    }

    public async Task<OperationResult<ModInstallResult>> InstallAsync(
        ModpackInfo modpack,
        ModInfo libraryMod,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentNullException.ThrowIfNull(libraryMod);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stageDirectory = null;
        var createdTargets = new List<string>();
        var createdDirectories = new List<string>();
        var previousMods = CloneMods(modpack.Mods);
        var previousUpdatedAt = modpack.UpdatedAt;

        try
        {
            var validation = ValidateInstance(modpack);
            if (!validation.Success)
            {
                return OperationResult<ModInstallResult>.Fail(validation.ErrorKey, validation.TechnicalMessage);
            }

            if (modpack.Mods.Any(x =>
                    x.Id.Equals(libraryMod.Id, StringComparison.OrdinalIgnoreCase) ||
                    x.PackageHash.Equals(libraryMod.PackageHash, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult<ModInstallResult>.Fail("ModAlreadyInstalled");
            }

            if (!File.Exists(libraryMod.LibraryArchivePath))
            {
                return OperationResult<ModInstallResult>.Fail("ModLibraryArchiveMissing", libraryMod.LibraryArchivePath);
            }

            var analysis = await _analyzer.AnalyzeAsync(libraryMod.LibraryArchivePath, cancellationToken)
                .ConfigureAwait(false);
            if (!analysis.IsSafe || !analysis.Sha256.Equals(libraryMod.PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                return FailWithValue("ModLibraryPackageInvalid", analysis.RejectionReason ?? "Hash mismatch.",
                    new ModInstallResult { Analysis = analysis });
            }

            stageDirectory = CreateTransactionDirectory(modpack, "staging");
            await _zipService.ExtractSafeAsync(
                    libraryMod.LibraryArchivePath,
                    stageDirectory,
                    analysis.ContentRootPrefix,
                    false,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            var installedMod = CloneForInstallation(libraryMod, analysis);
            installedMod.InstalledFiles = await BuildFileRecordsAsync(stageDirectory, cancellationToken)
                .ConfigureAwait(false);
            RemovePreExistingInstanceInfrastructure(modpack, installedMod);
            if (installedMod.InstalledFiles.Count == 0)
            {
                return FailWithValue("ModPackageHasNoFiles", string.Empty,
                    new ModInstallResult { Analysis = analysis });
            }

            var conflicts = _conflictDetector.Detect(modpack, installedMod).ToList();
            await AddPhysicalStateConflictsAsync(modpack, installedMod, stageDirectory, conflicts, cancellationToken)
                .ConfigureAwait(false);
            if (_conflictDetector.HasBlockingConflicts(conflicts))
            {
                await LogWarningAsync($"Blocked conflicting mod install '{installedMod.Name}'.").ConfigureAwait(false);
                return FailWithValue("ModConflict", "One or more target files contain different content.",
                    new ModInstallResult
                    {
                        Analysis = analysis,
                        InstalledMod = installedMod,
                        Conflicts = DistinctConflicts(conflicts)
                    });
            }

            MarkSharedOwnership(modpack, installedMod, conflicts);
            long applied = 0;
            foreach (var record in installedMod.InstalledFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveGameTarget(modpack, record.RelativePath);
                if (File.Exists(target))
                {
                    record.IsCurrentlyApplied = true;
                    progress?.Report(new OperationProgress(
                        "ModApply", ++applied, installedMod.InstalledFiles.Count, record.RelativePath));
                    continue;
                }

                var source = ZipService.ResolveContainedPath(stageDirectory, record.RelativePath);
                await CopyNewFileAtomicallyAsync(source, target, createdDirectories, cancellationToken)
                    .ConfigureAwait(false);
                createdTargets.Add(target);
                record.IsCurrentlyApplied = true;
                progress?.Report(new OperationProgress(
                    "ModApply", ++applied, installedMod.InstalledFiles.Count, record.RelativePath));
            }

            installedMod.InstalledAt = DateTimeOffset.UtcNow;
            installedMod.IsEnabled = true;
            modpack.Mods.Add(installedMod);
            RefreshDependencies(modpack);
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistMetadataAsync(modpack, cancellationToken).ConfigureAwait(false);
            await LogInfoAsync($"Installed mod '{installedMod.Name}' into isolated instance '{modpack.Name}'.")
                .ConfigureAwait(false);
            return OperationResult<ModInstallResult>.Ok(new ModInstallResult
            {
                Analysis = analysis,
                InstalledMod = installedMod,
                Conflicts = DistinctConflicts(conflicts)
            });
        }
        catch (OperationCanceledException)
        {
            RollBackCreatedFiles(createdTargets, createdDirectories);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            RollBackCreatedFiles(createdTargets, createdDirectories);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            await LogErrorAsync("Mod installation failed and was rolled back.", ex).ConfigureAwait(false);
            return OperationResult<ModInstallResult>.Fail("ModInstallFailed", ex.Message);
        }
        finally
        {
            if (stageDirectory is not null)
            {
                _fileSystem.TryDeleteDirectory(stageDirectory);
            }

            _gate.Release();
        }
    }

    public async Task<OperationResult<ModOperationReport>> SetEnabledAsync(
        ModpackInfo modpack,
        string modId,
        bool enabled,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = ValidateInstance(modpack);
            if (!validation.Success)
            {
                return OperationResult<ModOperationReport>.Fail(validation.ErrorKey, validation.TechnicalMessage);
            }

            var mod = modpack.Mods.FirstOrDefault(x => x.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            if (mod is null)
            {
                return OperationResult<ModOperationReport>.Fail("ModNotInstalled");
            }

            if (mod.IsEnabled == enabled)
            {
                return OperationResult<ModOperationReport>.Ok(new ModOperationReport
                {
                    ModId = mod.Id,
                    Skipped = mod.InstalledFiles.Select(x => x.RelativePath).ToList()
                });
            }

            return enabled
                ? await EnableCoreAsync(modpack, mod, progress, cancellationToken).ConfigureAwait(false)
                : await DisableCoreAsync(modpack, mod, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<OperationResult<ModOperationReport>> EnableAsync(
        ModpackInfo modpack,
        string modId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(modpack, modId, true, progress, cancellationToken);

    public Task<OperationResult<ModOperationReport>> DisableAsync(
        ModpackInfo modpack,
        string modId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(modpack, modId, false, progress, cancellationToken);

    public async Task<OperationResult<ModOperationReport>> UninstallAsync(
        ModpackInfo modpack,
        string modId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var previousMods = CloneMods(modpack.Mods);
        var previousUpdatedAt = modpack.UpdatedAt;
        var movedFiles = new List<(string Original, string Holding)>();
        string? holdingRoot = null;
        string? movedDisabledDirectory = null;
        try
        {
            var validation = ValidateInstance(modpack);
            if (!validation.Success)
            {
                return OperationResult<ModOperationReport>.Fail(validation.ErrorKey, validation.TechnicalMessage);
            }

            var mod = modpack.Mods.FirstOrDefault(x => x.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            if (mod is null)
            {
                return OperationResult<ModOperationReport>.Fail("ModNotInstalled");
            }

            var report = new ModOperationReport { ModId = mod.Id };
            var conflicts = await ValidateRemovalTargetsAsync(modpack, mod, cancellationToken).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                report.Conflicts.AddRange(conflicts);
                return FailWithValue("ModFilesChanged", "Owned files changed outside SkeldForge.", report);
            }

            holdingRoot = CreateTransactionDirectory(modpack, "uninstall");
            var otherEnabledPaths = GetEnabledOwnershipPaths(modpack, mod.Id);
            var completed = 0;
            foreach (var record in mod.InstalledFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ConflictDetector.Normalize(record.RelativePath);
                var target = ResolveGameTarget(modpack, path);
                if (record.PreserveOnRemove || otherEnabledPaths.Contains(path))
                {
                    report.Shared.Add(path);
                }
                else if (File.Exists(target))
                {
                    var holding = ZipService.ResolveContainedPath(holdingRoot, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(holding)!);
                    File.Move(target, holding);
                    movedFiles.Add((target, holding));
                    report.Removed.Add(path);
                }
                else
                {
                    report.Skipped.Add(path);
                }

                progress?.Report(new OperationProgress("ModUninstall", ++completed, mod.InstalledFiles.Count, path));
            }

            var disabledRoot = GetDisabledRoot(modpack, mod.Id);
            if (Directory.Exists(disabledRoot))
            {
                movedDisabledDirectory = Path.Combine(holdingRoot, "disabled-content");
                Directory.Move(disabledRoot, movedDisabledDirectory);
            }

            modpack.Mods.Remove(mod);
            RefreshSharedFlags(modpack);
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistMetadataAsync(modpack, cancellationToken).ConfigureAwait(false);
            _fileSystem.TryDeleteDirectory(holdingRoot);
            holdingRoot = null;
            RemoveEmptyGameDirectories(modpack, report.Removed);
            await LogInfoAsync($"Uninstalled mod '{mod.Name}' from isolated instance '{modpack.Name}'.")
                .ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            RestoreMovedFiles(movedFiles);
            RestoreDisabledDirectory(movedDisabledDirectory, modpack, modId);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            RestoreMovedFiles(movedFiles);
            RestoreDisabledDirectory(movedDisabledDirectory, modpack, modId);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            await LogErrorAsync("Mod uninstall failed and was rolled back.", ex).ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Fail("ModUninstallFailed", ex.Message);
        }
        finally
        {
            if (holdingRoot is not null && movedFiles.Count == 0)
            {
                _fileSystem.TryDeleteDirectory(holdingRoot);
            }

            _gate.Release();
        }
    }

    public Task<OperationResult<ModOperationReport>> RemoveAsync(
        ModpackInfo modpack,
        string modId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        UninstallAsync(modpack, modId, progress, cancellationToken);

    public IReadOnlyList<ConflictInfo> GetConflicts(ModpackInfo modpack)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        var conflicts = new List<ConflictInfo>();
        foreach (var incoming in modpack.Mods.Where(x => x.IsEnabled))
        {
            conflicts.AddRange(_conflictDetector.Detect(modpack.Mods, incoming));
        }

        return DistinctConflicts(conflicts);
    }

    private async Task<OperationResult<ModOperationReport>> DisableCoreAsync(
        ModpackInfo modpack,
        ModInfo mod,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = new ModOperationReport { ModId = mod.Id };
        var previousMods = CloneMods(modpack.Mods);
        var previousUpdatedAt = modpack.UpdatedAt;
        var moved = new List<(string Original, string Disabled)>();
        try
        {
            var conflicts = await ValidateRemovalTargetsAsync(modpack, mod, cancellationToken).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                report.Conflicts.AddRange(conflicts);
                return FailWithValue("ModFilesChanged", "Owned files changed outside SkeldForge.", report);
            }

            var disabledRoot = GetDisabledRoot(modpack, mod.Id);
            var otherEnabledPaths = GetEnabledOwnershipPaths(modpack, mod.Id);
            var completed = 0;
            foreach (var record in mod.InstalledFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ConflictDetector.Normalize(record.RelativePath);
                var target = ResolveGameTarget(modpack, path);
                if (record.PreserveOnRemove || otherEnabledPaths.Contains(path))
                {
                    record.IsCurrentlyApplied = false;
                    report.Shared.Add(path);
                }
                else if (File.Exists(target))
                {
                    var disabled = ZipService.ResolveContainedPath(disabledRoot, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(disabled)!);
                    if (File.Exists(disabled))
                    {
                        throw new IOException($"Disabled-file storage already contains '{path}'.");
                    }

                    File.Move(target, disabled);
                    moved.Add((target, disabled));
                    record.IsCurrentlyApplied = false;
                    report.Removed.Add(path);
                }
                else
                {
                    record.IsCurrentlyApplied = false;
                    report.Skipped.Add(path);
                }

                progress?.Report(new OperationProgress("ModDisable", ++completed, mod.InstalledFiles.Count, path));
            }

            mod.IsEnabled = false;
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistMetadataAsync(modpack, cancellationToken).ConfigureAwait(false);
            RemoveEmptyGameDirectories(modpack, report.Removed);
            await LogInfoAsync($"Disabled mod '{mod.Name}' in isolated instance '{modpack.Name}'.")
                .ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            RestoreDisabledFiles(moved);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            RestoreDisabledFiles(moved);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            await LogErrorAsync("Disabling a mod failed and was rolled back.", ex).ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Fail("ModDisableFailed", ex.Message);
        }
    }

    private async Task<OperationResult<ModOperationReport>> EnableCoreAsync(
        ModpackInfo modpack,
        ModInfo mod,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = new ModOperationReport { ModId = mod.Id };
        var previousMods = CloneMods(modpack.Mods);
        var previousUpdatedAt = modpack.UpdatedAt;
        var created = new List<string>();
        var moved = new List<(string Target, string Disabled)>();
        var createdDirectories = new List<string>();
        string? stage = null;
        try
        {
            if (!File.Exists(mod.LibraryArchivePath))
            {
                return OperationResult<ModOperationReport>.Fail("ModLibraryArchiveMissing", mod.LibraryArchivePath);
            }

            var analysis = await _analyzer.AnalyzeAsync(mod.LibraryArchivePath, cancellationToken).ConfigureAwait(false);
            if (!analysis.IsSafe || !analysis.Sha256.Equals(mod.PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<ModOperationReport>.Fail("ModLibraryPackageInvalid",
                    analysis.RejectionReason ?? "Hash mismatch.");
            }

            stage = CreateTransactionDirectory(modpack, "staging");
            await _zipService.ExtractSafeAsync(
                    mod.LibraryArchivePath, stage, analysis.ContentRootPrefix, false, progress, cancellationToken)
                .ConfigureAwait(false);
            var stagedRecords = (await BuildFileRecordsAsync(stage, cancellationToken).ConfigureAwait(false))
                .ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
            var disabledRoot = GetDisabledRoot(modpack, mod.Id);
            var conflicts = new List<ConflictInfo>();

            foreach (var record in mod.InstalledFiles)
            {
                if (!stagedRecords.TryGetValue(record.RelativePath, out var staged) ||
                    !staged.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(CreateStateConflict(mod, record, "Library package content changed", staged?.Sha256 ?? string.Empty));
                    continue;
                }

                var target = ResolveGameTarget(modpack, record.RelativePath);
                if (!File.Exists(target))
                {
                    continue;
                }

                var actualHash = await _hashService.ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false);
                if (!actualHash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(CreateStateConflict(mod, record, "Untracked instance file", actualHash));
                    continue;
                }

                var owners = GetEnabledOwners(modpack, mod.Id, record.RelativePath);
                record.IsShared = owners.Count > 0 || record.PreserveOnRemove;
                if (owners.Count == 0 && !record.PreserveOnRemove)
                {
                    record.PreserveOnRemove = true;
                }
            }

            if (conflicts.Count > 0)
            {
                report.Conflicts.AddRange(conflicts);
                return FailWithValue("ModConflict", "A target file contains different content.", report);
            }

            var completed = 0;
            foreach (var record in mod.InstalledFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveGameTarget(modpack, record.RelativePath);
                if (File.Exists(target))
                {
                    record.IsCurrentlyApplied = true;
                    report.Shared.Add(record.RelativePath);
                }
                else
                {
                    var disabled = ZipService.ResolveContainedPath(disabledRoot, record.RelativePath);
                    if (File.Exists(disabled))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(disabled, target);
                        moved.Add((target, disabled));
                    }
                    else
                    {
                        var source = ZipService.ResolveContainedPath(stage, record.RelativePath);
                        await CopyNewFileAtomicallyAsync(source, target, createdDirectories, cancellationToken)
                            .ConfigureAwait(false);
                        created.Add(target);
                    }

                    record.IsCurrentlyApplied = true;
                    report.Applied.Add(record.RelativePath);
                }

                progress?.Report(new OperationProgress("ModEnable", ++completed, mod.InstalledFiles.Count, record.RelativePath));
            }

            mod.IsEnabled = true;
            mod.Dependencies = _dependencyDetector.Evaluate(mod, modpack, modpack.GameDirectory)
                .Select(CloneDependency).ToList();
            modpack.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistMetadataAsync(modpack, cancellationToken).ConfigureAwait(false);
            _fileSystem.TryDeleteDirectory(disabledRoot);
            await LogInfoAsync($"Enabled mod '{mod.Name}' in isolated instance '{modpack.Name}'.")
                .ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            RollBackCreatedFiles(created, createdDirectories);
            RollBackEnabledMoves(moved);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            RollBackCreatedFiles(created, createdDirectories);
            RollBackEnabledMoves(moved);
            await RestoreStateAndMetadataAsync(modpack, previousMods, previousUpdatedAt).ConfigureAwait(false);
            await LogErrorAsync("Enabling a mod failed and was rolled back.", ex).ConfigureAwait(false);
            return OperationResult<ModOperationReport>.Fail("ModEnableFailed", ex.Message);
        }
        finally
        {
            if (stage is not null)
            {
                _fileSystem.TryDeleteDirectory(stage);
            }
        }
    }

    private async Task AddPhysicalStateConflictsAsync(
        ModpackInfo modpack,
        ModInfo incoming,
        string stageDirectory,
        ICollection<ConflictInfo> conflicts,
        CancellationToken cancellationToken)
    {
        foreach (var record in incoming.InstalledFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveGameTarget(modpack, record.RelativePath);
            var owners = GetEnabledOwners(modpack, incoming.Id, record.RelativePath);
            if (!File.Exists(target))
            {
                if (owners.Count > 0)
                {
                    conflicts.Add(CreateStateConflict(incoming, record, "Owned instance file is missing", string.Empty));
                }

                continue;
            }

            var actualHash = await _hashService.ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (owners.Count == 0 && IsCompatiblePreservedInfrastructureFile(
                        record.RelativePath,
                        target,
                        ZipService.ResolveContainedPath(stageDirectory, record.RelativePath)))
                {
                    record.PreserveOnRemove = true;
                    record.IsShared = true;
                    continue;
                }

                conflicts.Add(CreateStateConflict(incoming, record,
                    owners.Count > 0 ? "Owned instance file was modified" : "Untracked instance file", actualHash));
            }
            else if (owners.Count == 0)
            {
                record.PreserveOnRemove = true;
                record.IsShared = true;
                conflicts.Add(new ConflictInfo
                {
                    RelativePath = record.RelativePath,
                    ExistingModName = "Pre-existing identical instance file",
                    IncomingModId = incoming.Id,
                    IncomingModName = incoming.Name,
                    ExistingHash = actualHash,
                    IncomingHash = record.Sha256,
                    HashesMatch = true
                });
            }
        }
    }

    private async Task<List<ConflictInfo>> ValidateRemovalTargetsAsync(
        ModpackInfo modpack,
        ModInfo mod,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<ConflictInfo>();
        var otherEnabled = GetEnabledOwnershipPaths(modpack, mod.Id);
        foreach (var record in mod.InstalledFiles.Where(x => x.IsCurrentlyApplied))
        {
            if (record.PreserveOnRemove || otherEnabled.Contains(record.RelativePath))
            {
                continue;
            }

            var target = ResolveGameTarget(modpack, record.RelativePath);
            if (!File.Exists(target))
            {
                continue;
            }

            var actual = await _hashService.ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(CreateStateConflict(mod, record, "Owned instance file was modified", actual));
            }
        }

        return conflicts;
    }

    private static ConflictInfo CreateStateConflict(
        ModInfo incoming,
        InstalledFileRecord record,
        string existingName,
        string existingHash) => new()
    {
        RelativePath = record.RelativePath,
        ExistingModName = existingName,
        IncomingModId = incoming.Id,
        IncomingModName = incoming.Name,
        ExistingHash = existingHash,
        IncomingHash = record.Sha256,
        HashesMatch = existingHash.Length == 64 && existingHash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)
    };

    private static void MarkSharedOwnership(
        ModpackInfo modpack,
        ModInfo incoming,
        IEnumerable<ConflictInfo> conflicts)
    {
        foreach (var conflict in conflicts.Where(x => x.HashesMatch))
        {
            var incomingRecord = incoming.InstalledFiles.FirstOrDefault(x =>
                x.RelativePath.Equals(conflict.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (incomingRecord is not null)
            {
                incomingRecord.IsShared = true;
            }

            var existing = modpack.Mods.FirstOrDefault(x =>
                x.Id.Equals(conflict.ExistingModId, StringComparison.OrdinalIgnoreCase));
            var existingRecord = existing?.InstalledFiles.FirstOrDefault(x =>
                x.RelativePath.Equals(conflict.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (existingRecord is not null)
            {
                existingRecord.IsShared = true;
            }
        }
    }

    private IReadOnlyList<ModInfo> GetEnabledOwners(ModpackInfo modpack, string excludedModId, string path) =>
        modpack.Mods.Where(x => x.IsEnabled && !x.Id.Equals(excludedModId, StringComparison.OrdinalIgnoreCase) &&
                                x.InstalledFiles.Any(file => file.IsCurrentlyApplied &&
                                    file.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private static HashSet<string> GetEnabledOwnershipPaths(ModpackInfo modpack, string excludedModId) =>
        modpack.Mods
            .Where(x => x.IsEnabled && !x.Id.Equals(excludedModId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.InstalledFiles)
            .Where(x => x.IsCurrentlyApplied)
            .Select(x => ConflictDetector.Normalize(x.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<List<InstalledFileRecord>> BuildFileRecordsAsync(
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stageDirectory);
        var result = new List<InstalledFileRecord>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Staged package contains a reparse point.");
            }

            var relative = ConflictDetector.Normalize(Path.GetRelativePath(root, path));
            if (IsPackageDocumentation(relative))
            {
                continue;
            }

            result.Add(new InstalledFileRecord
            {
                RelativePath = relative,
                Sha256 = await _hashService.ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false),
                Size = new FileInfo(path).Length,
                IsCurrentlyApplied = true
            });
        }

        return result;
    }

    private static bool IsPackageDocumentation(string relativePath)
    {
        if (relativePath.Contains('/')) return false;
        var name = Path.GetFileNameWithoutExtension(relativePath);
        return name.Equals("changelog", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("changes", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("readme", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("license", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("licence", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemovePreExistingInstanceInfrastructure(ModpackInfo modpack, ModInfo incoming)
    {
        incoming.InstalledFiles.RemoveAll(record =>
            IsInstanceInfrastructurePath(record.RelativePath) &&
            File.Exists(ResolveGameTarget(modpack, record.RelativePath)));
    }

    private static bool IsInstanceInfrastructurePath(string relativePath)
    {
        var path = ConflictDetector.Normalize(relativePath);
        return path.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatiblePreservedInfrastructureFile(
        string relativePath,
        string existingPath,
        string incomingPath)
    {
        if (!relativePath.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var existing = ParseIniSettings(existingPath);
            var incoming = ParseIniSettings(incomingPath);
            if (incoming.Count == 0) return false;

            // Full mod bundles frequently ship a slightly different Doorstop
            // template (extra debugger/environment switches) even though both
            // files load the same BepInEx entry point.  Those optional settings
            // must not make an otherwise clean isolated instance look as if it
            // contains a conflicting mod.
            string[] loaderKeys =
            [
                "General/enabled",
                "General/target_assembly",
                "Il2Cpp/coreclr_path",
                "Il2Cpp/corlib_dir"
            ];

            return loaderKeys.All(key =>
                incoming.TryGetValue(key, out var incomingValue) &&
                existing.TryGetValue(key, out var existingValue) &&
                existingValue.Equals(incomingValue, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseIniSettings(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = $"{section}/{line[..separator].Trim()}";
            result[key] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static ModInfo CloneForInstallation(ModInfo libraryMod, ModPackageAnalysis analysis) => new()
    {
        Id = libraryMod.Id,
        Name = libraryMod.Name,
        Version = libraryMod.Version,
        PackageHash = libraryMod.PackageHash,
        SourceArchiveName = libraryMod.SourceArchiveName,
        LibraryArchivePath = libraryMod.LibraryArchivePath,
        ImportDate = libraryMod.ImportDate,
        IsEnabled = true,
        NameConfidence = libraryMod.NameConfidence,
        VersionConfidence = libraryMod.VersionConfidence,
        Compatibility = ModCompatibilityStatus.Unknown,
        Dependencies = analysis.DetectedDependencies.Select(CloneDependency).ToList()
    };

    private OperationResult ValidateInstance(ModpackInfo modpack)
    {
        if (string.IsNullOrWhiteSpace(modpack.InstanceDirectory) || string.IsNullOrWhiteSpace(modpack.GameDirectory))
        {
            return OperationResult.Fail("InstancePathMissing");
        }

        var instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modpack.InstanceDirectory));
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modpack.GameDirectory));
        var inside = game.StartsWith(instance + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside || game.Equals(instance, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("InstancePathUnsafe", "Game directory must be a child of the isolated instance directory.");
        }

        if (!string.IsNullOrWhiteSpace(modpack.SourceGamePath) &&
            game.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modpack.SourceGamePath)),
                StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("InstancePointsToSourceGame");
        }

        if (!Directory.Exists(instance) || !Directory.Exists(game))
        {
            return OperationResult.Fail("InstanceDirectoryMissing");
        }

        if (File.GetAttributes(instance).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(game).HasFlag(FileAttributes.ReparsePoint))
        {
            return OperationResult.Fail("InstancePathUnsafe", "Reparse-point instance paths are not modified.");
        }

        return OperationResult.Ok();
    }

    private string CreateTransactionDirectory(ModpackInfo modpack, string category)
    {
        var instanceRoot = Path.GetFullPath(modpack.InstanceDirectory);
        var stateRoot = Path.Combine(instanceRoot, ".skeldforge", category);
        EnsureNoReparseAncestors(instanceRoot, Path.GetDirectoryName(stateRoot) ?? instanceRoot);
        Directory.CreateDirectory(stateRoot);
        EnsureNoReparseAncestors(instanceRoot, stateRoot);
        var directory = Path.Combine(stateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetDisabledRoot(ModpackInfo modpack, string modId)
    {
        if (modId.Length is <= 0 or > 64 || !modId.All(char.IsAsciiLetterOrDigit))
        {
            throw new InvalidDataException("Mod identifier is unsafe for disabled-file storage.");
        }

        var instanceRoot = Path.GetFullPath(modpack.InstanceDirectory);
        var result = Path.Combine(instanceRoot, ".skeldforge", "disabled", modId);
        EnsureNoReparseAncestors(instanceRoot, Path.GetDirectoryName(result) ?? instanceRoot);
        return result;
    }

    private static string ResolveGameTarget(ModpackInfo modpack, string relativePath)
    {
        var gameRoot = Path.GetFullPath(modpack.GameDirectory);
        var result = ZipService.ResolveContainedPath(gameRoot, relativePath);
        EnsureNoReparseAncestors(gameRoot, Path.GetDirectoryName(result) ?? gameRoot);
        if (File.Exists(result) && File.GetAttributes(result).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Tracked mod targets cannot be reparse points.");
        }

        return result;
    }

    private static void EnsureNoReparseAncestors(string root, string directory)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!canonicalDirectory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalDirectory.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("File operation escaped the isolated instance.");
        }

        if (Directory.Exists(canonicalRoot) &&
            File.GetAttributes(canonicalRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Reparse-point instance roots are not modified.");
        }

        var current = canonicalRoot;
        foreach (var segment in Path.GetRelativePath(canonicalRoot, canonicalDirectory)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Mod file operation would traverse a reparse point.");
            }
        }
    }

    private static async Task CopyNewFileAtomicallyAsync(
        string source,
        string target,
        ICollection<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"Install target already exists: '{target}'.");
        }

        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Install target has no parent directory.");
        TrackCreatedDirectories(parent, createdDirectories);
        var temporary = target + $".skeldforge-apply-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void TrackCreatedDirectories(string directory, ICollection<string> created)
    {
        var missing = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("Directory has no existing ancestor.");
        }

        while (missing.Count > 0)
        {
            var item = missing.Pop();
            Directory.CreateDirectory(item);
            created.Add(item);
        }
    }

    private async Task PersistMetadataAsync(ModpackInfo modpack, CancellationToken cancellationToken)
    {
        if (_metadataWriter is not null)
        {
            await _metadataWriter(modpack, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = Path.Combine(Path.GetFullPath(modpack.InstanceDirectory), "modpack.json");
        await _fileSystem.AtomicWriteJsonAsync(path, modpack, cancellationToken).ConfigureAwait(false);
    }

    private static void RestoreModState(ModpackInfo modpack, List<ModInfo> previousMods, DateTimeOffset updatedAt)
    {
        modpack.Mods = previousMods;
        modpack.UpdatedAt = updatedAt;
    }

    private async Task RestoreStateAndMetadataAsync(
        ModpackInfo modpack,
        List<ModInfo> previousMods,
        DateTimeOffset updatedAt)
    {
        RestoreModState(modpack, previousMods, updatedAt);
        try
        {
            await PersistMetadataAsync(modpack, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackException)
        {
            await LogErrorAsync("Mod metadata rollback could not be persisted.", rollbackException)
                .ConfigureAwait(false);
        }
    }

    private static List<ModInfo> CloneMods(IEnumerable<ModInfo> mods) => mods.Select(CloneMod).ToList();

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

    private static DependencyInfo CloneDependency(DependencyInfo value) => new()
    {
        Name = value.Name,
        Status = value.Status,
        Evidence = value.Evidence,
        Confidence = value.Confidence
    };

    private static List<ConflictInfo> DistinctConflicts(IEnumerable<ConflictInfo> conflicts) => conflicts
        .DistinctBy(x => $"{x.RelativePath}\0{x.ExistingModId}\0{x.ExistingModName}\0{x.IncomingModId}",
            StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void RefreshSharedFlags(ModpackInfo modpack)
    {
        var counts = modpack.Mods
            .SelectMany(x => x.InstalledFiles)
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var record in modpack.Mods.SelectMany(x => x.InstalledFiles))
        {
            record.IsShared = record.PreserveOnRemove || counts[record.RelativePath] > 1;
        }
    }

    private void RefreshDependencies(ModpackInfo modpack)
    {
        foreach (var mod in modpack.Mods)
        {
            mod.Dependencies = _dependencyDetector.Evaluate(mod, modpack, modpack.GameDirectory)
                .Select(CloneDependency)
                .ToList();
        }
    }

    private static void RollBackCreatedFiles(IEnumerable<string> files, IEnumerable<string> directories)
    {
        foreach (var file in files.Reverse())
        {
            TryDeleteFile(file);
        }

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            TryDeleteEmptyDirectory(directory);
        }
    }

    private static void RestoreDisabledFiles(IEnumerable<(string Original, string Disabled)> moved)
    {
        foreach (var (original, disabled) in moved.Reverse())
        {
            try
            {
                if (File.Exists(disabled) && !File.Exists(original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(disabled, original);
                }
            }
            catch
            {
                // Leave the recoverable copy in disabled storage.
            }
        }
    }

    private static void RollBackEnabledMoves(IEnumerable<(string Target, string Disabled)> moved)
    {
        foreach (var (target, disabled) in moved.Reverse())
        {
            try
            {
                if (File.Exists(target) && !File.Exists(disabled))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(disabled)!);
                    File.Move(target, disabled);
                }
            }
            catch
            {
                // Keep whichever recoverable copy remains.
            }
        }
    }

    private static void RestoreMovedFiles(IEnumerable<(string Original, string Holding)> moved)
    {
        foreach (var (original, holding) in moved.Reverse())
        {
            try
            {
                if (File.Exists(holding) && !File.Exists(original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(holding, original);
                }
            }
            catch
            {
                // The holding transaction remains under the instance for manual recovery.
            }
        }
    }

    private static void RestoreDisabledDirectory(string? movedDirectory, ModpackInfo modpack, string modId)
    {
        if (movedDirectory is null || !Directory.Exists(movedDirectory))
        {
            return;
        }

        try
        {
            var destination = GetDisabledRoot(modpack, modId);
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(movedDirectory, destination);
            }
        }
        catch
        {
            // The holding transaction remains under the instance for manual recovery.
        }
    }

    private static void RemoveEmptyGameDirectories(ModpackInfo modpack, IEnumerable<string> relativeFiles)
    {
        var root = Path.GetFullPath(modpack.GameDirectory);
        foreach (var relative in relativeFiles)
        {
            var directory = Path.GetDirectoryName(ResolveGameTarget(modpack, relative));
            while (directory is not null && !directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    break;
                }

                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
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
        }
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logger is not null)
        {
            try { await _logger.InfoAsync(message).ConfigureAwait(false); } catch { }
        }
    }

    private async Task LogWarningAsync(string message)
    {
        if (_logger is not null)
        {
            try { await _logger.WarningAsync(message).ConfigureAwait(false); } catch { }
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
