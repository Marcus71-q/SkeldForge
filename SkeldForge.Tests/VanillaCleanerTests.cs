using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class VanillaCleanerTests
{
    [Fact]
    public async Task ScanCleanAndRestoreUseBackupWhileLeavingUnknownFilesAlone()
    {
        using var temp = new TemporaryDirectory();
        var game = new TemporaryDirectory();
        try
        {
            CreateModdedGame(game);
            var backupManager = new BackupManager(temp.Combine("Backups"));
            var cleaner = new VanillaCleaner(backupManager);

            var scanResult = await cleaner.ScanAsync(game.Path);

            Assert.True(scanResult.Success, scanResult.TechnicalMessage);
            var scan = Assert.IsType<CleanupScanResult>(scanResult.Value);
            Assert.Contains(scan.Findings, x =>
                x.RelativePath.Equals("BepInEx/plugins/TOHE.dll", StringComparison.OrdinalIgnoreCase) &&
                x.Classification == CleanupClassification.KnownModPlugin && x.SelectedForRemoval);
            Assert.Contains(scan.Findings, x =>
                x.RelativePath == "Among Us.exe" && x.Classification == CleanupClassification.ProtectedGameComponent && !x.SelectedForRemoval);
            Assert.Contains(scan.Findings, x =>
                x.RelativePath == "mystery.dll" && x.Classification == CleanupClassification.Unknown && !x.SelectedForRemoval);

            var cleanResult = await cleaner.CleanAsync(scan);

            Assert.True(cleanResult.Success, cleanResult.TechnicalMessage);
            var report = Assert.IsType<CleanupReport>(cleanResult.Value);
            Assert.True(report.BackupCreated);
            Assert.True(report.VerificationCompleted);
            Assert.Contains("BepInEx/plugins/TOHE.dll", report.Removed);
            Assert.True(File.Exists(game.Combine("mystery.dll")));
            Assert.True(File.Exists(game.Combine("Among Us.exe")));
            Assert.False(File.Exists(game.Combine("BepInEx", "plugins", "TOHE.dll")));

            var backups = await backupManager.GetBackupsAsync();
            var manifest = Assert.Single(backups);
            Assert.True(File.Exists(System.IO.Path.Combine(manifest.BackupDirectory, BackupManager.ManifestFileName)));
            Assert.Contains(manifest.Files, x => x.RelativePath == "BepInEx/plugins/TOHE.dll" && x.Sha256.Length == 64);

            var restore = await backupManager.RestoreAsync(manifest);

            Assert.True(restore.Success, restore.TechnicalMessage);
            Assert.True(File.Exists(game.Combine("BepInEx", "plugins", "TOHE.dll")));
            Assert.Equal("unknown, keep me", await File.ReadAllTextAsync(game.Combine("mystery.dll")));
        }
        finally
        {
            game.Dispose();
        }
    }

    [Fact]
    public async Task BackupFailureStopsCleanupBeforeAnyDeletion()
    {
        using var game = new TemporaryDirectory();
        CreateModdedGame(game);
        var cleaner = new VanillaCleaner(new FailingBackupManager());
        var scan = Assert.IsType<CleanupScanResult>((await cleaner.ScanAsync(game.Path)).Value);

        var result = await cleaner.CleanAsync(scan);

        Assert.False(result.Success);
        Assert.Equal("CleanupBackupFailed", result.ErrorKey);
        Assert.True(File.Exists(game.Combine("BepInEx", "plugins", "TOHE.dll")));
        Assert.True(File.Exists(game.Combine("doorstop_config.ini")));
        Assert.True(File.Exists(game.Combine("winhttp.dll")));
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteChangedFileWithoutExplicitApproval()
    {
        using var temp = new TemporaryDirectory();
        var game = new TemporaryDirectory();
        try
        {
            CreateModdedGame(game);
            var manager = new BackupManager(temp.Combine("Backups"));
            var source = new BackupSourceItem
            {
                FullPath = game.Combine("doorstop_config.ini"),
                RelativePath = "doorstop_config.ini",
                Classification = CleanupClassification.KnownModComponent.ToString()
            };
            var created = await manager.CreateBackupAsync(
                game.Path, [source], BackupKind.VanillaCleanup, "Restore conflict test");
            var manifest = Assert.IsType<BackupManifest>(created.Value);
            await File.WriteAllTextAsync(game.Combine("doorstop_config.ini"), "newer local content");

            var restore = await manager.RestoreAsync(manifest);

            Assert.False(restore.Success);
            Assert.Equal("RestoreConflicts", restore.ErrorKey);
            Assert.Equal("newer local content", await File.ReadAllTextAsync(game.Combine("doorstop_config.ini")));
            Assert.Equal(1, restore.Value?.ConflictCount);
        }
        finally
        {
            game.Dispose();
        }
    }

    private static void CreateModdedGame(TemporaryDirectory game)
    {
        GameDetectorTests.CreateGameShape(game);
        Directory.CreateDirectory(game.Combine("BepInEx", "core"));
        Directory.CreateDirectory(game.Combine("BepInEx", "plugins"));
        File.WriteAllText(game.Combine("BepInEx", "core", "BepInEx.Preloader.dll"), "loader");
        File.WriteAllText(game.Combine("BepInEx", "plugins", "TOHE.dll"), "plugin");
        File.WriteAllText(
            game.Combine("doorstop_config.ini"),
            "targetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\ndllSearchPathOverride=");
        File.WriteAllText(game.Combine("winhttp.dll"), "doorstop proxy");
        File.WriteAllText(game.Combine("mystery.dll"), "unknown, keep me");
    }

    private sealed class FailingBackupManager : IBackupManager
    {
        public Task<OperationResult<BackupManifest>> CreateBackupAsync(
            string sourceRoot,
            IEnumerable<BackupSourceItem> items,
            BackupKind kind,
            string displayName,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<BackupManifest>.Fail("SyntheticBackupFailure", "simulated disk failure"));

        public Task<OperationResult<RestoreReport>> RestoreAsync(
            BackupManifest manifest,
            bool overwriteConflicts = false,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<RestoreReport>.Fail("NotUsed"));
    }
}
