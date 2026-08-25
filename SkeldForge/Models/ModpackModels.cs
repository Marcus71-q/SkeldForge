namespace SkeldForge.Models;

public sealed class ModpackInfo
{
    public int SchemaVersion { get; set; } = SkeldForgeVersion.ModpackSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string InstanceDirectory { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public string SourceGamePath { get; set; } = string.Empty;
    public string SourceGameVersion { get; set; } = "Unknown";
    public string SourceGameBuildId { get; set; } = string.Empty;
    public string SourceGameFingerprint { get; set; } = string.Empty;
    public string CreatedWithSkeldForge { get; set; } = SkeldForgeVersion.Current;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public List<ModInfo> Mods { get; set; } = [];
}

public sealed class SkeldpackManifest
{
    public int FormatVersion { get; set; } = SkeldForgeVersion.SkeldpackFormat;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ExportedBy { get; set; } = $"SkeldForge {SkeldForgeVersion.Current}";
    public List<SkeldpackModReference> Mods { get; set; } = [];
}

public sealed class SkeldpackModReference
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "Unknown";
    public string Hash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class SkeldpackImportPreview
{
    public required SkeldpackManifest Manifest { get; init; }
    public List<ModInfo> FoundMods { get; init; } = [];
    public List<SkeldpackModReference> MissingMods { get; init; } = [];
}
