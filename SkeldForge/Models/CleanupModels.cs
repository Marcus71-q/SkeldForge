namespace SkeldForge.Models;

public sealed class CleanupFinding
{
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public CleanupClassification Classification { get; set; }
    public bool IsDirectory { get; set; }
    public bool SelectedForRemoval { get; set; }
    public string Evidence { get; set; } = string.Empty;
}

public sealed class CleanupScanResult
{
    public string GamePath { get; set; } = string.Empty;
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<CleanupFinding> Findings { get; set; } = [];
    public int KnownCount => Findings.Count(x => x.Classification is CleanupClassification.KnownModComponent or CleanupClassification.KnownModPlugin);
    public int UnknownCount => Findings.Count(x => x.Classification == CleanupClassification.Unknown);
    public int ProtectedCount => Findings.Count(x => x.Classification == CleanupClassification.ProtectedGameComponent);
}

public sealed class CleanupReport
{
    public string BackupId { get; set; } = string.Empty;
    public bool BackupCreated { get; set; }
    public bool VerificationCompleted { get; set; }
    public List<string> Removed { get; set; } = [];
    public List<string> BackedUp { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public List<string> Unknown { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public CleanupScanResult? FinalScan { get; set; }
    public bool Success => BackupCreated && Errors.Count == 0;
}
