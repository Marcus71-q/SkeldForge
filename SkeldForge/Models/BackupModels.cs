namespace SkeldForge.Models;

public sealed class BackupSourceItem
{
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string Classification { get; set; } = string.Empty;
}

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BackupKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedWithSkeldForge { get; set; } = SkeldForgeVersion.Current;
    public List<BackupEntry> Files { get; set; } = [];
}

public sealed class BackupEntry
{
    public string OriginalPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Classification { get; set; } = string.Empty;
}

public sealed class RestoreEntryResult
{
    public string RelativePath { get; set; } = string.Empty;
    public RestoreEntryStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class RestoreReport
{
    public string BackupId { get; set; } = string.Empty;
    public List<RestoreEntryResult> Entries { get; set; } = [];
    public int RestoredCount => Entries.Count(x => x.Status == RestoreEntryStatus.Restored);
    public int ConflictCount => Entries.Count(x => x.Status == RestoreEntryStatus.Conflict);
    public int ErrorCount => Entries.Count(x => x.Status == RestoreEntryStatus.Error);
}
