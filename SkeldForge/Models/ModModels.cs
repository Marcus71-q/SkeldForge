namespace SkeldForge.Models;

public sealed class InstalledFileRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsShared { get; set; }
    public bool IsCurrentlyApplied { get; set; } = true;
    /// <summary>
    /// True when identical unowned content existed before this mod was installed.
    /// Disable and uninstall must leave that physical file in place.
    /// </summary>
    public bool PreserveOnRemove { get; set; }
}

public sealed class DependencyInfo
{
    public string Name { get; set; } = string.Empty;
    public DependencyStatus Status { get; set; } = DependencyStatus.Unknown;
    public string Evidence { get; set; } = string.Empty;
    public PackageConfidence Confidence { get; set; } = PackageConfidence.Unknown;
}

public sealed class ModInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "Unknown";
    public string PackageHash { get; set; } = string.Empty;
    public string SourceArchiveName { get; set; } = string.Empty;
    public string LibraryArchivePath { get; set; } = string.Empty;
    public DateTimeOffset ImportDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? InstalledAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    public PackageConfidence NameConfidence { get; set; } = PackageConfidence.Unknown;
    public PackageConfidence VersionConfidence { get; set; } = PackageConfidence.Unknown;
    public ModCompatibilityStatus Compatibility { get; set; } = ModCompatibilityStatus.Unknown;
    public List<InstalledFileRecord> InstalledFiles { get; set; } = [];
    public List<DependencyInfo> Dependencies { get; set; } = [];
}

public sealed class PackageEntryInfo
{
    public string RelativePath { get; set; } = string.Empty;
    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public bool IsDirectory { get; set; }
}

public sealed class ModPackageAnalysis
{
    public string ArchivePath { get; set; } = string.Empty;
    public string ArchiveName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string DetectedName { get; set; } = string.Empty;
    public string DetectedVersion { get; set; } = "Unknown";
    /// <summary>
    /// Optional archive wrapper directory removed when mapping entries into a game
    /// instance. The value always uses forward slashes and ends in a slash.
    /// </summary>
    public string ContentRootPrefix { get; set; } = string.Empty;
    public PackageConfidence NameConfidence { get; set; }
    public PackageConfidence VersionConfidence { get; set; }
    public bool IsSafe { get; set; }
    public bool HasBepInExStructure { get; set; }
    public bool HasPluginDlls { get; set; }
    public bool HasConfigurationFiles { get; set; }
    public bool HasLoaderComponents { get; set; }
    public string? RejectionReason { get; set; }
    public List<string> PluginFiles { get; set; } = [];
    public List<PackageEntryInfo> Entries { get; set; } = [];
    public List<DependencyInfo> DetectedDependencies { get; set; } = [];
    public int FileCount => Entries.Count(x => !x.IsDirectory);
    public long UncompressedSize => Entries.Sum(x => x.UncompressedSize);
}

public sealed class ConflictInfo
{
    public string RelativePath { get; set; } = string.Empty;
    public string ExistingModId { get; set; } = string.Empty;
    public string ExistingModName { get; set; } = string.Empty;
    public string IncomingModId { get; set; } = string.Empty;
    public string IncomingModName { get; set; } = string.Empty;
    public bool HashesMatch { get; set; }
    public string ExistingHash { get; set; } = string.Empty;
    public string IncomingHash { get; set; } = string.Empty;
}

public sealed class ModLibraryImportResult
{
    public required ModInfo Mod { get; init; }
    public bool IsDuplicate { get; init; }
}

public sealed class ModInstallResult
{
    public ModPackageAnalysis? Analysis { get; init; }
    public ModInfo? InstalledMod { get; init; }
    public bool UsedExistingLibraryEntry { get; init; }
    public List<ConflictInfo> Conflicts { get; init; } = [];
}

public sealed class ModOperationReport
{
    public string ModId { get; init; } = string.Empty;
    public List<string> Applied { get; init; } = [];
    public List<string> Removed { get; init; } = [];
    public List<string> Shared { get; init; } = [];
    public List<string> Skipped { get; init; } = [];
    public List<ConflictInfo> Conflicts { get; init; } = [];
}
