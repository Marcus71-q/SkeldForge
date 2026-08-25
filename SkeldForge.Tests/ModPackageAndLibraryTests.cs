using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class ModPackageAndLibraryTests
{
    [Fact]
    public async Task AnalyzerReportsStructureAndManifestVersionWithoutInstalling()
    {
        using var temp = new TemporaryDirectory();
        var package = temp.Combine("TOHE-package.zip");
        await TestArchive.CreateAsync(
            package,
            ("manifest.json", "{\"name\":\"TOHE\",\"version\":\"2.4.2 Beta 15\"}"),
            ("BepInEx/plugins/TOHE.dll", "not a managed assembly, but a plugin-shaped fixture"),
            ("BepInEx/config/tohe.cfg", "Enabled=true"),
            ("doorstop_config.ini", "targetAssembly=BepInEx/core/BepInEx.Preloader.dll"),
            ("winhttp.dll", "loader"));

        var analysis = await new ModPackageAnalyzer().AnalyzeAsync(package);

        Assert.True(analysis.IsSafe, analysis.RejectionReason);
        Assert.Equal("TOHE", analysis.DetectedName);
        Assert.Equal("2.4.2 Beta 15", analysis.DetectedVersion);
        Assert.Equal(PackageConfidence.High, analysis.VersionConfidence);
        Assert.True(analysis.HasBepInExStructure);
        Assert.True(analysis.HasPluginDlls);
        Assert.True(analysis.HasConfigurationFiles);
        Assert.True(analysis.HasLoaderComponents);
        Assert.Equal(5, analysis.FileCount);
        Assert.Equal(64, analysis.Sha256.Length);
    }

    [Fact]
    public async Task IdenticalArchiveHashReusesExistingLibraryEntry()
    {
        using var temp = new TemporaryDirectory();
        var package = temp.Combine("Example-1.2.3.zip");
        await TestArchive.CreateAsync(package, ("BepInEx/plugins/Example.dll", "fixture"));
        var analyzer = new ModPackageAnalyzer();
        var library = new ModLibraryService(temp.Combine("Library"), analyzer);

        var first = await library.ImportAsync(package);
        var second = await library.ImportAsync(package);

        Assert.True(first.Success, first.TechnicalMessage);
        Assert.True(second.Success, second.TechnicalMessage);
        Assert.False(Assert.IsType<ModLibraryImportResult>(first.Value).IsDuplicate);
        Assert.True(Assert.IsType<ModLibraryImportResult>(second.Value).IsDuplicate);
        Assert.Equal(first.Value.Mod.Id, second.Value.Mod.Id);
        Assert.Single(await library.GetAllAsync());
    }
}
