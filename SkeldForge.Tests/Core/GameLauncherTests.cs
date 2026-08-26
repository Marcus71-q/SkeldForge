using SkeldForge.Core;
using SkeldForge.Models;

namespace SkeldForge.Tests.Core;

public sealed class GameLauncherTests
{
    [Fact]
    public void MissingDetectedDependency_IsAdvisoryInsteadOfLaunchBlocking()
    {
        var instance = new ModpackInfo
        {
            Mods =
            [
                new ModInfo
                {
                    Name = "HydraMenu",
                    IsEnabled = true,
                    Dependencies =
                    [
                        new DependencyInfo
                        {
                            Name = "BepInEx",
                            Status = DependencyStatus.Missing
                        }
                    ]
                }
            ]
        };

        var warnings = GameLauncher.GetDependencyWarnings(instance);

        Assert.Single(warnings);
        Assert.Contains("HydraMenu", warnings[0]);
        Assert.Contains("BepInEx", warnings[0]);
        Assert.Contains("Launch was allowed", warnings[0]);
    }

    [Fact]
    public void ModdedStartInfo_UsesDirectSteamAwareLaunch()
    {
        var info = GameLauncher.CreateModdedStartInfo(
            @"C:\Instances\Test\Game\Among Us.exe",
            @"C:\Instances\Test\Game");

        Assert.False(info.UseShellExecute);
        Assert.Equal("945360", info.Environment["SteamAppId"]);
        Assert.Equal("945360", info.Environment["SteamGameId"]);
        Assert.Equal(@"C:\Instances\Test\Game", info.WorkingDirectory);
    }

    [Fact]
    public async Task EnsureSteamAppIdAsync_CreatesAndRepairsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SkeldForgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "steam_appid.txt");
            await File.WriteAllTextAsync(path, "wrong");

            await GameLauncher.EnsureSteamAppIdAsync(directory);

            Assert.Equal("945360", (await File.ReadAllTextAsync(path)).Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DetectKnownGameVersionMismatch_BlocksToheBeta15OnNewerGame()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SkeldForgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Among Us_Data"));
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "Among Us_Data", "globalgamemanagers"),
                "binary\02026.8.18\0more"u8.ToArray());
            var instance = new ModpackInfo
            {
                Mods =
                [
                    new ModInfo
                    {
                        Name = "TOHE",
                        Version = "2026.514.242.15000",
                        IsEnabled = true
                    }
                ]
            };

            var mismatch = GameLauncher.DetectKnownGameVersionMismatch(instance, directory);

            Assert.Contains("2025.11.18", mismatch);
            Assert.Contains("2026.8.18", mismatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
