using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Services;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class GameInstanceUpdateTests
{
    [Fact]
    public async Task UserControlledUpdateHasRecoverableBackupAndReappliesTrackedMods()
    {
        using var temp = new TemporaryDirectory();
        using var source = new TemporaryDirectory();
        GameDetectorTests.CreateGameShape(source);
        var paths = new AppPaths(temp.Combine("Data"));
        var instances = new GameInstanceManager(paths);
        var created = await instances.CreateAsync("Update Test", source.Path);
        var modpack = Assert.IsType<ModpackInfo>(created.Value);

        var package = temp.Combine("TrackedMod.zip");
        await TestArchive.CreateAsync(package, ("BepInEx/plugins/TrackedMod.dll", "tracked plugin"));
        var analyzer = new ModPackageAnalyzer();
        var mods = new ModManager(
            analyzer,
            new ModLibraryService(paths.ModLibraryDirectory, analyzer),
            new ZipService(),
            new ConflictDetector(),
            new DependencyDetector(),
            metadataWriter: async (value, token) =>
            {
                var save = await instances.SaveAsync(value, token);
                if (!save.Success) throw new IOException(save.TechnicalMessage);
            });
        var installed = await mods.InstallFromArchiveAsync(modpack, package);
        Assert.True(installed.Success, installed.TechnicalMessage);

        var backups = new BackupManager(temp.Combine("Backups"));
        var backupResult = await backups.CreateBackupAsync(
            modpack.InstanceDirectory,
            [
                new BackupSourceItem
                {
                    FullPath = modpack.GameDirectory,
                    RelativePath = GameInstanceManager.GameFolderName,
                    IsDirectory = true,
                    Classification = "InstanceGameFile"
                },
                new BackupSourceItem
                {
                    FullPath = System.IO.Path.Combine(modpack.InstanceDirectory, GameInstanceManager.MetadataFileName),
                    RelativePath = GameInstanceManager.MetadataFileName,
                    Classification = "InstanceMetadata"
                }
            ],
            BackupKind.Instance,
            "Update Test Instance Backup");
        Assert.True(backupResult.Success, backupResult.TechnicalMessage);

        await File.WriteAllTextAsync(source.Combine("Among Us_Data", "globalgamemanagers"), "new Steam build");
        await File.WriteAllTextAsync(source.Combine("Among Us_Data", "new-build.marker"), "new official file");
        var sourceSnapshot = await SnapshotAsync(source.Path);
        var current = Assert.IsType<GameInstallation>((await new GameDetector().DetectInstallationAsync(source.Path)).Value);
        Assert.True(new VersionManager().Compare(current, modpack).HasChanged);

        var updated = await instances.UpdateAsync(modpack.Id, source.Path);

        Assert.True(updated.Success, updated.TechnicalMessage);
        var refreshed = Assert.IsType<ModpackInfo>(updated.Value);
        Assert.Equal("new Steam build", await File.ReadAllTextAsync(
            System.IO.Path.Combine(refreshed.GameDirectory, "Among Us_Data", "globalgamemanagers")));
        Assert.True(File.Exists(System.IO.Path.Combine(refreshed.GameDirectory, "Among Us_Data", "new-build.marker")));
        Assert.Equal("tracked plugin", await File.ReadAllTextAsync(
            System.IO.Path.Combine(refreshed.GameDirectory, "BepInEx", "plugins", "TrackedMod.dll")));
        Assert.False(File.Exists(source.Combine("BepInEx", "plugins", "TrackedMod.dll")));
        Assert.Equal(sourceSnapshot, await SnapshotAsync(source.Path));
        Assert.True(File.Exists(System.IO.Path.Combine(
            Assert.IsType<BackupManifest>(backupResult.Value).BackupDirectory,
            "files", "Game", "Among Us_Data", "globalgamemanagers")));
    }

    private static async Task<string> SnapshotAsync(string root)
    {
        var hasher = new HashService();
        var entries = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add($"{System.IO.Path.GetRelativePath(root, file)}:{await hasher.ComputeFileSha256Async(file)}");
        }

        return string.Join("\n", entries);
    }
}
