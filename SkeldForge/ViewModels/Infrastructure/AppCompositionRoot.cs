using System.Diagnostics;
using System.IO;
using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.ViewModels.Infrastructure;

/// <summary>
/// Single UI composition boundary. It wires durable services once and keeps view models
/// free of filesystem construction details while preserving the services' transactional results.
/// </summary>
public sealed class AppCompositionRoot : IDisposable
{
    private readonly HashService _hash = new();
    private readonly FileSystemService _files = new();
    private readonly VersionManager _versions;
    private readonly GameDetector _gameDetector;
    private readonly SteamDetector _steamDetector;
    private readonly ZipService _zip = new();
    private readonly DependencyDetector _dependencies;
    private readonly ModPackageAnalyzer _analyzer;
    private readonly ConflictDetector _conflicts = new();
    private readonly SkeldpackService _skeldpacks;
    private readonly SettingsService _settings;
    private readonly GameInstanceManager _instances;
    private readonly GameLauncher _launcher;

    private ModLibraryService? _library;
    private ModManager? _mods;
    private BackupManager? _backups;
    private VanillaCleaner? _cleaner;
    private string _configuredLibraryRoot = string.Empty;
    private string _configuredBackupRoot = string.Empty;

    public AppCompositionRoot()
    {
        Paths = new AppPaths();
        Logger = new LoggingService(Paths);
        Dialogs = new DialogService();
        _settings = new SettingsService(Paths, Logger);
        _versions = new VersionManager(_hash);
        _gameDetector = new GameDetector(_versions);
        _steamDetector = new SteamDetector(_gameDetector, Logger);
        _dependencies = new DependencyDetector();
        _analyzer = new ModPackageAnalyzer(_zip, _dependencies);
        _instances = new GameInstanceManager(Paths, _files, _gameDetector, _versions, Logger);
        _launcher = new GameLauncher(Paths, _gameDetector, Logger, _hash, _instances);
        _skeldpacks = new SkeldpackService(_zip, Logger);
    }

    public AppPaths Paths { get; }
    public LoggingService Logger { get; }
    public IDialogService Dialogs { get; }

    public async Task<AppSettings> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        ConfigureDataServices(settings);
        await Logger.LogStartupAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        ConfigureDataServices(_settings.Current);
    }

    public async Task<AppSettings> ResetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.ResetAsync(cancellationToken).ConfigureAwait(false);
        ConfigureDataServices(settings);
        return settings;
    }

    public async Task<GameInstallation?> DetectGameAsync(CancellationToken cancellationToken = default)
    {
        var detection = await _steamDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        return detection.AmongUsInstallations.FirstOrDefault();
    }

    public Task<GameInstallation?> InspectGameAsync(string path, CancellationToken cancellationToken = default) =>
        _gameDetector.InspectAsync(path, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<ModpackInfo>> GetModpacksAsync(CancellationToken cancellationToken = default) =>
        _instances.GetAllAsync(cancellationToken);

    public Task<ModpackInfo?> GetModpackAsync(string id, CancellationToken cancellationToken = default) =>
        _instances.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<ModInfo>> GetLibraryModsAsync(CancellationToken cancellationToken = default) =>
        Library.GetAllAsync(cancellationToken);

    public Task<OperationResult<ModpackInfo>> CreateModpackAsync(
        string name, string sourcePath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _instances.CreateAsync(name, sourcePath, "1.0", progress, cancellationToken);

    public Task<OperationResult<ModpackInfo>> DuplicateModpackAsync(
        string id, string name, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _instances.DuplicateAsync(id, name, progress, cancellationToken);

    public Task<OperationResult<ModpackInfo>> RenameModpackAsync(
        string id, string name, CancellationToken cancellationToken = default) =>
        _instances.RenameAsync(id, name, cancellationToken);

    public Task<OperationResult> DeleteModpackAsync(string id, CancellationToken cancellationToken = default) =>
        _instances.DeleteAsync(id, cancellationToken);

    public Task<OperationResult<ModpackInfo>> SaveModpackAsync(ModpackInfo modpack, CancellationToken cancellationToken = default) =>
        _instances.SaveAsync(modpack, cancellationToken);

    public GameUpdateComparison CompareGameVersion(GameInstallation currentGame, ModpackInfo modpack) =>
        _versions.Compare(currentGame, modpack);

    public async Task<OperationResult<BackupManifest>> CreateInstanceBackupAsync(
        ModpackInfo modpack,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        var metadataPath = Path.Combine(modpack.InstanceDirectory, GameInstanceManager.MetadataFileName);
        var items = new List<BackupSourceItem>
        {
            new()
            {
                FullPath = modpack.GameDirectory,
                RelativePath = GameInstanceManager.GameFolderName,
                IsDirectory = true,
                Classification = "InstanceGameFile"
            }
        };
        if (File.Exists(metadataPath))
        {
            items.Add(new BackupSourceItem
            {
                FullPath = metadataPath,
                RelativePath = GameInstanceManager.MetadataFileName,
                Classification = "InstanceMetadata"
            });
        }

        return await Backups.CreateBackupAsync(
            modpack.InstanceDirectory,
            items,
            BackupKind.Instance,
            $"{modpack.Name} Instance Backup",
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ModpackInfo>> UpdateModpackBaseAsync(
        ModpackInfo modpack,
        string sourceGamePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        var backup = await CreateInstanceBackupAsync(modpack, progress, cancellationToken).ConfigureAwait(false);
        if (!backup.Success)
        {
            return OperationResult<ModpackInfo>.Fail(backup.ErrorKey, backup.TechnicalMessage);
        }

        return await _instances.UpdateAsync(
            modpack.Id, sourceGamePath, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> LaunchVanillaAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _launcher.LaunchVanillaAsync(path, cancellationToken).ConfigureAwait(false);
        return ToUntyped(result);
    }

    public async Task<OperationResult> LaunchModpackAsync(ModpackInfo modpack, CancellationToken cancellationToken = default)
    {
        var result = await _launcher.LaunchModdedAsync(modpack, cancellationToken).ConfigureAwait(false);
        return ToUntyped(result);
    }

    public async Task<ModPackageAnalysis> AnalyzePackageAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _analyzer.AnalyzeAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Logger.WarningAsync($"Package analysis rejected '{Path.GetFileName(archivePath)}'.", ex, cancellationToken)
                .ConfigureAwait(false);
            return new ModPackageAnalysis
            {
                ArchivePath = archivePath,
                ArchiveName = Path.GetFileName(archivePath),
                DetectedName = Path.GetFileNameWithoutExtension(archivePath),
                IsSafe = false,
                RejectionReason = ex.Message
            };
        }
    }

    public Task<OperationResult<ModLibraryImportResult>> ImportLibraryPackageAsync(
        string path, ModPackageAnalysis analysis, CancellationToken cancellationToken = default) =>
        Library.ImportAsync(path, analysis, cancellationToken);

    public async Task<OperationResult<InstalledModUiResult>> InstallModFromArchiveAsync(
        ModpackInfo modpack,
        string archivePath,
        ModPackageAnalysis analysis,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var import = await Library.ImportAsync(archivePath, analysis, cancellationToken).ConfigureAwait(false);
        if (!import.Success || import.Value is null)
            return OperationResult<InstalledModUiResult>.Fail(import.ErrorKey, import.TechnicalMessage);

        var install = await Mods.InstallAsync(modpack, import.Value.Mod, progress, cancellationToken).ConfigureAwait(false);
        if (!install.Success || install.Value?.InstalledMod is null)
            return OperationResult<InstalledModUiResult>.Fail(install.ErrorKey, install.TechnicalMessage);

        var refreshed = await _instances.GetByIdAsync(modpack.Id, cancellationToken).ConfigureAwait(false) ?? modpack;
        return OperationResult<InstalledModUiResult>.Ok(new InstalledModUiResult(refreshed, install.Value.InstalledMod));
    }

    public async Task<OperationResult<ModpackInfo>> SetModEnabledAsync(
        ModpackInfo modpack,
        string modId,
        bool enabled,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Mods.SetEnabledAsync(modpack, modId, enabled, progress, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return OperationResult<ModpackInfo>.Fail(result.ErrorKey, result.TechnicalMessage);
        var refreshed = await _instances.GetByIdAsync(modpack.Id, cancellationToken).ConfigureAwait(false) ?? modpack;
        return OperationResult<ModpackInfo>.Ok(refreshed);
    }

    public async Task<OperationResult<ModpackInfo>> RemoveModAsync(
        ModpackInfo modpack,
        string modId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Mods.UninstallAsync(modpack, modId, progress, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return OperationResult<ModpackInfo>.Fail(result.ErrorKey, result.TechnicalMessage);
        var refreshed = await _instances.GetByIdAsync(modpack.Id, cancellationToken).ConfigureAwait(false) ?? modpack;
        return OperationResult<ModpackInfo>.Ok(refreshed);
    }

    public async Task<OperationResult> ExportSkeldpackAsync(
        ModpackInfo modpack,
        string destination,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationProgress("SkeldpackExport", 0, 1, Path.GetFileName(destination)));
        var result = await _skeldpacks.ExportAsync(modpack, destination, cancellationToken).ConfigureAwait(false);
        progress?.Report(new OperationProgress("SkeldpackExport", 1, 1, Path.GetFileName(destination)));
        return ToUntyped(result);
    }

    public Task<OperationResult<SkeldpackImportPreview>> PreviewSkeldpackAsync(
        string path, CancellationToken cancellationToken = default) =>
        _skeldpacks.PreviewImportAsync(path, Library, cancellationToken);

    public async Task<OperationResult<ModpackInfo>> ImportSkeldpackAsync(
        string path,
        string sourceGamePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var previewResult = await _skeldpacks.PreviewImportAsync(path, Library, cancellationToken).ConfigureAwait(false);
        if (!previewResult.Success || previewResult.Value is null)
            return OperationResult<ModpackInfo>.Fail(previewResult.ErrorKey, previewResult.TechnicalMessage);

        var preview = previewResult.Value;
        var create = await _instances.CreateAsync(
            preview.Manifest.Name, sourceGamePath, preview.Manifest.Version, progress, cancellationToken).ConfigureAwait(false);
        if (!create.Success || create.Value is null)
            return create;

        var modpack = create.Value;
        foreach (var libraryMod in preview.FoundMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var desiredEnabled = libraryMod.IsEnabled;
            var install = await Mods.InstallAsync(modpack, libraryMod, progress, cancellationToken).ConfigureAwait(false);
            if (!install.Success || install.Value?.InstalledMod is null)
            {
                await _instances.DeleteAsync(modpack.Id, CancellationToken.None).ConfigureAwait(false);
                return OperationResult<ModpackInfo>.Fail(install.ErrorKey, install.TechnicalMessage);
            }
            if (!desiredEnabled)
            {
                var disable = await Mods.SetEnabledAsync(modpack, libraryMod.Id, false, progress, cancellationToken).ConfigureAwait(false);
                if (!disable.Success)
                {
                    await _instances.DeleteAsync(modpack.Id, CancellationToken.None).ConfigureAwait(false);
                    return OperationResult<ModpackInfo>.Fail(disable.ErrorKey, disable.TechnicalMessage);
                }
            }
        }

        return OperationResult<ModpackInfo>.Ok(
            await _instances.GetByIdAsync(modpack.Id, cancellationToken).ConfigureAwait(false) ?? modpack);
    }

    public Task<OperationResult<CleanupScanResult>> ScanVanillaAsync(
        string gamePath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Cleaner.ScanAsync(gamePath, progress, cancellationToken);

    public Task<OperationResult<CleanupReport>> CleanVanillaAsync(
        CleanupScanResult scan, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Cleaner.CleanAsync(scan, progress, cancellationToken);

    public Task<IReadOnlyList<BackupManifest>> GetBackupsAsync(CancellationToken cancellationToken = default) =>
        Backups.GetBackupsAsync(cancellationToken);

    public async Task<BackupManifest?> GetBackupAsync(string id, CancellationToken cancellationToken = default) =>
        (await Backups.GetBackupsAsync(cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public Task<OperationResult<RestoreReport>> RestoreBackupAsync(
        BackupManifest manifest,
        IProgress<OperationProgress>? progress = null,
        bool overwriteConflicts = false,
        CancellationToken cancellationToken = default) =>
        Backups.RestoreAsync(manifest, overwriteConflicts, progress, cancellationToken);

    public Task<OperationResult> DeleteBackupAsync(BackupManifest manifest, CancellationToken cancellationToken = default) =>
        Backups.DeleteBackupAsync(manifest, cancellationToken);

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Warning($"Could not open folder '{path}'.", ex);
        }
    }

    public string ResolveProgressLocalizationKey(string stageKey) => stageKey switch
    {
        "CopyingGameFiles" or "InstanceReady" => "Progress.Copying",
        "ZipExtract" or "ModApply" or "ModEnable" or "ModDisable" or "ModUninstall" => "Progress.Installing",
        "BackupCreate" => "Progress.BackingUp",
        "BackupRestore" => "Progress.Restoring",
        "CleanupScan" => "Cleanup.Scanning",
        "CleanupRemove" => "Progress.Cleaning",
        "SkeldpackExport" => "Progress.Exporting",
        _ => stageKey.StartsWith("Progress.", StringComparison.Ordinal) || stageKey.StartsWith("Cleanup.", StringComparison.Ordinal)
            ? stageKey
            : "Common.Working"
    };

    public string ResolveErrorLocalizationKey(string errorKey)
    {
        if (string.IsNullOrWhiteSpace(errorKey)) return "Error.Generic";
        if (errorKey.Equals("Launch.AlreadyRunning", StringComparison.OrdinalIgnoreCase)) return "Error.GameAlreadyRunning";
        if (errorKey.Equals("Launch.IncompatibleGameVersion", StringComparison.OrdinalIgnoreCase)) return "Error.IncompatibleGameVersion";
        if (errorKey.Contains("DiskSpace", StringComparison.OrdinalIgnoreCase)) return "Error.DiskSpace";
        if (errorKey.Contains("Unsafe", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("PackageUnsafe", StringComparison.OrdinalIgnoreCase)) return "Error.ZipUnsafe";
        if (errorKey.Contains("Access", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("Permission", StringComparison.OrdinalIgnoreCase)) return "Error.Permission";
        if (errorKey.Contains("Backup", StringComparison.OrdinalIgnoreCase) && !errorKey.Contains("Restore", StringComparison.OrdinalIgnoreCase)) return "Error.BackupFailed";
        if (errorKey.Contains("Restore", StringComparison.OrdinalIgnoreCase)) return "Error.RestoreConflict";
        if (errorKey.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("FilesChanged", StringComparison.OrdinalIgnoreCase)) return "Error.ModConflict";
        if (errorKey.Contains("Launch", StringComparison.OrdinalIgnoreCase)) return "Error.Launch";
        if (errorKey.Contains("GamePath", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("SourceMustBeVanilla", StringComparison.OrdinalIgnoreCase)) return "Error.InvalidGamePath";
        if (errorKey.Contains("Executable", StringComparison.OrdinalIgnoreCase)) return "Error.ExecutableMissing";
        if (errorKey.Contains("Instance", StringComparison.OrdinalIgnoreCase)) return "Error.InstanceCorrupt";
        if (errorKey.Contains("Skeldpack", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("Json", StringComparison.OrdinalIgnoreCase)) return "Error.InvalidJson";
        if (errorKey.Contains("Zip", StringComparison.OrdinalIgnoreCase) || errorKey.Contains("Package", StringComparison.OrdinalIgnoreCase)) return "Error.ZipCorrupt";
        if (errorKey.Contains("File", StringComparison.OrdinalIgnoreCase)) return "Error.FileLocked";
        return "Error.Generic";
    }

    public void Dispose() => Logger.Dispose();

    private ModLibraryService Library => _library ?? throw new InvalidOperationException("Application services have not been initialized.");
    private ModManager Mods => _mods ?? throw new InvalidOperationException("Application services have not been initialized.");
    private BackupManager Backups => _backups ?? throw new InvalidOperationException("Application services have not been initialized.");
    private VanillaCleaner Cleaner => _cleaner ?? throw new InvalidOperationException("Application services have not been initialized.");

    private void ConfigureDataServices(AppSettings settings)
    {
        Paths.ConfigureDataRoot(settings.DataRoot);
        Paths.EnsureDataDirectories();
        var libraryRoot = Paths.ModLibraryDirectory;
        var backupRoot = string.IsNullOrWhiteSpace(settings.BackupRoot) ? Paths.BackupsDirectory : Path.GetFullPath(settings.BackupRoot);
        if (_library is not null &&
            libraryRoot.Equals(_configuredLibraryRoot, StringComparison.OrdinalIgnoreCase) &&
            backupRoot.Equals(_configuredBackupRoot, StringComparison.OrdinalIgnoreCase))
            return;

        _configuredLibraryRoot = libraryRoot;
        _configuredBackupRoot = backupRoot;
        _library = new ModLibraryService(libraryRoot, _analyzer);
        _backups = new BackupManager(backupRoot, _hash, _files, Logger);
        _cleaner = new VanillaCleaner(_backups, _hash, Logger);
        _mods = new ModManager(
            _analyzer, _library, _zip, _conflicts, _dependencies, _hash, _files, Logger,
            async (modpack, token) =>
            {
                var save = await _instances.SaveAsync(modpack, token).ConfigureAwait(false);
                if (!save.Success)
                    throw new IOException($"Could not persist modpack metadata: {save.ErrorKey} {save.TechnicalMessage}");
            });
    }

    private static OperationResult ToUntyped<T>(OperationResult<T> result) => new()
    {
        Success = result.Success,
        ErrorKey = result.ErrorKey,
        TechnicalMessage = result.TechnicalMessage
    };
}

public sealed record InstalledModUiResult(ModpackInfo Modpack, ModInfo Mod);
