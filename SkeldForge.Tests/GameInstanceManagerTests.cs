using SkeldForge.Core;
using SkeldForge.Services;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class GameInstanceManagerTests
{
    [Fact]
    public async Task CreatesIsolatedCopyAndLeavesSourceUnchanged()
    {
        using var temp = new TemporaryDirectory();
        var source = new TemporaryDirectory();
        try
        {
            GameDetectorTests.CreateGameShape(source);
			Directory.CreateDirectory(source.Combine("BepInEx", "core"));
			Directory.CreateDirectory(source.Combine("BepInEx", "plugins"));
			Directory.CreateDirectory(source.Combine("BepInEx", "config"));
			await File.WriteAllTextAsync(source.Combine("BepInEx", "core", "BepInEx.Core.dll"), "loader core");
			await File.WriteAllTextAsync(source.Combine("BepInEx", "plugins", "OldHydra.dll"), "source mod contamination");
			await File.WriteAllTextAsync(source.Combine("BepInEx", "config", "oldhydra.cfg"), "source mod config");
            var sourceSnapshot = await SnapshotAsync(source.Path);
            var paths = new AppPaths(temp.Combine("Data"));
            var manager = new GameInstanceManager(paths);
            var progressEvents = new List<long>();

            var result = await manager.CreateAsync(
                "TOHE Test",
                source.Path,
                progress: new InlineProgress<SkeldForge.Models.OperationProgress>(p => progressEvents.Add(p.Completed)));

            Assert.True(result.Success, result.TechnicalMessage);
            var modpack = Assert.IsType<SkeldForge.Models.ModpackInfo>(result.Value);
            Assert.NotEqual(System.IO.Path.GetFullPath(source.Path), modpack.GameDirectory);
            Assert.True(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "Among Us.exe")));
			Assert.True(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "BepInEx", "core", "BepInEx.Core.dll")));
			Assert.False(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "BepInEx", "plugins", "OldHydra.dll")));
			Assert.False(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "BepInEx", "config", "oldhydra.cfg")));
            Assert.Equal(sourceSnapshot, await SnapshotAsync(source.Path));
            Assert.NotEmpty(progressEvents);
            Assert.True(new GameDetector().Validate(modpack.GameDirectory).IsValid);

            var packagePath = temp.Combine("SampleMod.zip");
            await TestArchive.CreateAsync(
                packagePath,
                ("BepInEx/plugins/SampleMod.dll", "synthetic plugin payload"),
                ("BepInEx/config/SampleMod.cfg", "Enabled=true"));
            var analyzer = new ModPackageAnalyzer();
            var modManager = new ModManager(
                analyzer,
                new ModLibraryService(paths.ModLibraryDirectory, analyzer),
                new ZipService(),
                new ConflictDetector(),
                new DependencyDetector(),
                metadataWriter: async (value, token) =>
                {
                    var saved = await manager.SaveAsync(value, token);
                    if (!saved.Success)
                    {
                        throw new IOException(saved.TechnicalMessage);
                    }
                });

            var installed = await modManager.InstallFromArchiveAsync(modpack, packagePath);

            Assert.True(installed.Success, installed.TechnicalMessage);
            Assert.True(File.Exists(System.IO.Path.Combine(
                modpack.GameDirectory, "BepInEx", "plugins", "SampleMod.dll")));
            var installedMod = Assert.Single(modpack.Mods);

            var disabled = await modManager.DisableAsync(modpack, installedMod.Id);
            Assert.True(disabled.Success, disabled.TechnicalMessage);
            Assert.False(File.Exists(System.IO.Path.Combine(
                modpack.GameDirectory, "BepInEx", "plugins", "SampleMod.dll")));
            Assert.False(modpack.Mods.Single().IsEnabled);

            var enabled = await modManager.EnableAsync(modpack, installedMod.Id);
            Assert.True(enabled.Success, enabled.TechnicalMessage);
            Assert.Equal("synthetic plugin payload", await File.ReadAllTextAsync(System.IO.Path.Combine(
                modpack.GameDirectory, "BepInEx", "plugins", "SampleMod.dll")));
            Assert.True(modpack.Mods.Single().IsEnabled);

            var removed = await modManager.UninstallAsync(modpack, installedMod.Id);
            Assert.True(removed.Success, removed.TechnicalMessage);
            Assert.Empty(modpack.Mods);
            Assert.False(File.Exists(System.IO.Path.Combine(
                modpack.GameDirectory, "BepInEx", "plugins", "SampleMod.dll")));

            var preservedDoorstop = "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Unity.IL2CPP.dll\n\n[Il2Cpp]\ncoreclr_path = dotnet\\coreclr.dll\ncorlib_dir = dotnet\n\n[Debug]\ndebug_start_server = true\n";
            await File.WriteAllTextAsync(System.IO.Path.Combine(modpack.GameDirectory, "doorstop_config.ini"), preservedDoorstop);
            await File.WriteAllTextAsync(System.IO.Path.Combine(modpack.GameDirectory, "changelog.txt"), "vanilla changelog");
            var loaderPackage = temp.Combine("BetterAmongUs.zip");
            await TestArchive.CreateAsync(
                loaderPackage,
                ("BetterAmongUs/changelog.txt", "mod release notes"),
                ("BetterAmongUs/doorstop_config.ini", "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Unity.IL2CPP.dll\n[Il2Cpp]\ncoreclr_path=dotnet\\coreclr.dll\ncorlib_dir=dotnet"),
                ("BetterAmongUs/BepInEx/plugins/BetterAmongUs.dll", "better among us payload"));

            var loaderInstalled = await modManager.InstallFromArchiveAsync(modpack, loaderPackage);

            Assert.True(loaderInstalled.Success, loaderInstalled.TechnicalMessage);
            Assert.Equal(preservedDoorstop, await File.ReadAllTextAsync(System.IO.Path.Combine(modpack.GameDirectory, "doorstop_config.ini")));
            Assert.Equal("vanilla changelog", await File.ReadAllTextAsync(System.IO.Path.Combine(modpack.GameDirectory, "changelog.txt")));
            Assert.True(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "BepInEx", "plugins", "BetterAmongUs.dll")));
            var loaderMod = Assert.Single(modpack.Mods);
            Assert.DoesNotContain(loaderMod.InstalledFiles, x => x.RelativePath.Equals("changelog.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(loaderMod.InstalledFiles.Single(x => x.RelativePath.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)).PreserveOnRemove);

            var loaderRemoved = await modManager.UninstallAsync(modpack, loaderMod.Id);
            Assert.True(loaderRemoved.Success, loaderRemoved.TechnicalMessage);
            Assert.True(File.Exists(System.IO.Path.Combine(modpack.GameDirectory, "doorstop_config.ini")));
            Assert.Equal(sourceSnapshot, await SnapshotAsync(source.Path));
        }
        finally
        {
            source.Dispose();
        }
    }

    private static async Task<string> SnapshotAsync(string root)
    {
        var hasher = new HashService();
        var rows = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add($"{System.IO.Path.GetRelativePath(root, file)}:{await hasher.ComputeFileSha256Async(file)}");
        }

        return string.Join("\n", rows);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
