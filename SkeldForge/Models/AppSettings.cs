namespace SkeldForge.Models;

public sealed class AppSettings
{
    public int ConfigVersion { get; set; } = SkeldForgeVersion.ConfigSchema;
    public string Language { get; set; } = string.Empty;
    public string AmongUsPath { get; set; } = string.Empty;
    public string DataRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public string LastLaunchedModpackId { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public bool FirstLaunchComplete { get; set; }
}
