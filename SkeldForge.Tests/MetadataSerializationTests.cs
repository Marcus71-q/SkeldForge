using System.Text.Json;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Tests;

public sealed class MetadataSerializationTests
{
    [Fact]
    public void ModpackRoundTripsVersionedOwnershipMetadata()
    {
        var original = new ModpackInfo
        {
            Name = "TOHE Test",
            Version = "1.3",
            SourceGameVersion = "2026.8.1",
            SourceGameFingerprint = "abc123",
            Mods =
            [
                new ModInfo
                {
                    Name = "Example",
                    Version = "2.4.2 Beta 15",
                    PackageHash = "def456",
                    InstalledFiles =
                    [
                        new InstalledFileRecord
                        {
                            RelativePath = "BepInEx/plugins/Example.dll",
                            Sha256 = "feedbeef",
                            Size = 42
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var restored = JsonSerializer.Deserialize<ModpackInfo>(json, JsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(SkeldForgeVersion.ModpackSchema, restored.SchemaVersion);
        Assert.Equal("TOHE Test", restored.Name);
        Assert.Equal("BepInEx/plugins/Example.dll", restored.Mods.Single().InstalledFiles.Single().RelativePath);
    }

    [Fact]
    public void BackupManifestRoundTripsHashesAndClassifications()
    {
        var original = new BackupManifest
        {
            Kind = BackupKind.VanillaCleanup,
            SourceRoot = @"C:\Games\Among Us",
            Files =
            [
                new BackupEntry
                {
                    OriginalPath = @"C:\Games\Among Us\winhttp.dll",
                    RelativePath = "winhttp.dll",
                    BackupPath = @"C:\Backup\files\winhttp.dll",
                    Sha256 = "012345",
                    Classification = CleanupClassification.KnownModComponent.ToString()
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var restored = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(BackupKind.VanillaCleanup, restored.Kind);
        Assert.Equal("012345", restored.Files.Single().Sha256);
    }
}
