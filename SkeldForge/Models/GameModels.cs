namespace SkeldForge.Models;

public sealed class GameValidationResult
{
    public GameValidationStatus Status { get; init; }
    public string Path { get; init; } = string.Empty;
    public IReadOnlyList<string> MissingItems { get; init; } = [];
    public string MessageKey { get; init; } = string.Empty;
    public bool IsValid => Status == GameValidationStatus.Valid;
}

public sealed class GameInstallation
{
    public string Path { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string SteamRoot { get; set; } = string.Empty;
    public string SteamLibrary { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = "Unknown";
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SteamDetectionResult
{
    public bool SteamFound { get; init; }
    public string SteamPath { get; init; } = string.Empty;
    public IReadOnlyList<string> LibraryPaths { get; init; } = [];
    public IReadOnlyList<GameInstallation> AmongUsInstallations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class GameUpdateComparison
{
    public bool HasChanged { get; init; }
    public string CurrentVersion { get; init; } = "Unknown";
    public string InstanceVersion { get; init; } = "Unknown";
    public string CurrentFingerprint { get; init; } = string.Empty;
    public string InstanceFingerprint { get; init; } = string.Empty;
}

public sealed class GameLaunchResult
{
    public bool IsVanilla { get; init; }
    public bool UsedSteamProtocol { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public DateTimeOffset LaunchedAt { get; init; } = DateTimeOffset.UtcNow;
}
