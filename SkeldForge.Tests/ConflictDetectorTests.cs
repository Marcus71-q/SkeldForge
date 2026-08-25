using SkeldForge.Core;
using SkeldForge.Models;

namespace SkeldForge.Tests;

public sealed class ConflictDetectorTests
{
    [Fact]
    public void DetectsTwoPackagesProvidingSameRelativeFileWithDifferentContent()
    {
        var existing = CreateMod("Mod A", "a", new string('a', 64));
        var incoming = CreateMod("Mod B", "b", new string('b', 64));
        var detector = new ConflictDetector();

        var conflicts = detector.Detect([existing], incoming);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("BepInEx/plugins/Example.dll", conflict.RelativePath);
        Assert.False(conflict.HashesMatch);
        Assert.True(detector.HasBlockingConflicts(conflicts));
    }

    [Fact]
    public void SamePathAndHashIsTrackedAsShareableRatherThanBlocking()
    {
        var hash = new string('c', 64);
        var existing = CreateMod("Loader A", "a", hash);
        var incoming = CreateMod("Loader B", "b", hash);
        var detector = new ConflictDetector();

        var conflicts = detector.Detect([existing], incoming);

        Assert.True(Assert.Single(conflicts).HashesMatch);
        Assert.False(detector.HasBlockingConflicts(conflicts));
    }

    private static ModInfo CreateMod(string name, string id, string hash) => new()
    {
        Id = id,
        Name = name,
        IsEnabled = true,
        InstalledFiles =
        [
            new InstalledFileRecord
            {
                RelativePath = "BepInEx/plugins/Example.dll",
                Sha256 = hash,
                IsCurrentlyApplied = true
            }
        ]
    };
}
