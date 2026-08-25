namespace SkeldForge.Models;

public enum GameValidationStatus { Valid, DirectoryMissing, ExecutableMissing, DataDirectoryMissing, CoreFilesMissing, AccessDenied, Invalid }
public enum ModCompatibilityStatus { Compatible, Unknown, PossibleMismatch, KnownIncompatible }
public enum DependencyStatus { Installed, Missing, Unknown, NotRequired }
public enum CleanupClassification { KnownModComponent, KnownModPlugin, ProtectedGameComponent, Unknown }
public enum BackupKind { VanillaCleanup, Instance }
public enum RestoreEntryStatus { Restored, Conflict, Skipped, Error }
public enum PackageConfidence { High, Medium, Low, Unknown }
public enum ModpackSort { Name, RecentlyUsed, DateCreated, Version }
public enum ModSort { Name, ImportDate, Version, Status }

public static class SkeldForgeVersion
{
    public const string Current = "1.0.10";
    public const int ConfigSchema = 1;
    public const int ModpackSchema = 1;
    public const int SkeldpackFormat = 1;
}
