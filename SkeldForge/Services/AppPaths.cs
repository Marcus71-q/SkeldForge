namespace SkeldForge.Services;

/// <summary>
/// Resolves every per-user SkeldForge path. The bootstrap configuration remains
/// under LocalApplicationData so a custom data root can always be discovered.
/// </summary>
public sealed class AppPaths
{
    public const string ApplicationFolderName = "SkeldForge";
    public const string SettingsFileName = "settings.json";

    private string _dataRoot;
    private string? _backupRoot;

    public AppPaths(string? dataRoot = null)
    {
        BootstrapRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName));

        _dataRoot = NormalizeRoot(dataRoot, BootstrapRoot);
    }

    public string BootstrapRoot { get; }
    public string DefaultDataRoot => BootstrapRoot;
    public string DataRoot => _dataRoot;
    public string ConfigDirectory => Path.Combine(BootstrapRoot, "Config");
    public string SettingsFilePath => Path.Combine(ConfigDirectory, SettingsFileName);
    public string InstancesDirectory => Path.Combine(DataRoot, "Instances");
    public string ModLibraryDirectory => Path.Combine(DataRoot, "ModLibrary");
    public string BackupsDirectory => _backupRoot ?? Path.Combine(DataRoot, "Backups");
    public string LogsDirectory => Path.Combine(DataRoot, "Logs");
    public string InstanceMetadataBackupsDirectory => Path.Combine(BackupsDirectory, "InstanceMetadata");

    // Readable aliases for callers that use Root/File terminology.
    public string InstancesRoot => InstancesDirectory;
    public string ModLibraryRoot => ModLibraryDirectory;
    public string BackupRoot => BackupsDirectory;
    public string LogsRoot => LogsDirectory;
    public string SettingsFile => SettingsFilePath;

    public void ConfigureDataRoot(string? dataRoot)
    {
        _dataRoot = NormalizeRoot(dataRoot, BootstrapRoot);
    }

    public void ConfigureBackupRoot(string? backupRoot)
    {
        _backupRoot = string.IsNullOrWhiteSpace(backupRoot)
            ? null
            : NormalizeRoot(backupRoot, Path.Combine(DataRoot, "Backups"));
    }

    public void EnsureBootstrapDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
    }

    public void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(ModLibraryDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string NormalizeRoot(string? candidate, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? fallback
            : Environment.ExpandEnvironmentVariables(candidate.Trim());

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
