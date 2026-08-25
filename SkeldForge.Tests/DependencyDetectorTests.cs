using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class DependencyDetectorTests
{
    [Fact]
    public async Task ReportsOnlyDependenciesWithConcretePackageEvidence()
    {
        using var temp = new TemporaryDirectory();
        var package = temp.Combine("dependency-fixture.zip");
        await TestArchive.CreateAsync(
            package,
            ("BepInEx/plugins/Example.dll", "plugin"),
            ("BepInEx/plugins/Reactor.dll", "provider by canonical filename"),
            ("BepInEx/plugins/MiraAPI.dll", "provider by canonical filename"));
        var analysis = await new ModPackageAnalyzer().AnalyzeAsync(package);

        var dependencies = new DependencyDetector().DetectFromArchive(package, analysis);

        Assert.Contains(dependencies, x =>
            x.Name == "BepInEx" && x.Status == DependencyStatus.Unknown && x.Confidence == PackageConfidence.High);
        Assert.Contains(dependencies, x =>
            x.Name == "Reactor" && x.Status == DependencyStatus.Installed && x.Confidence == PackageConfidence.High);
        Assert.Contains(dependencies, x =>
            x.Name == "MiraAPI" && x.Status == DependencyStatus.Installed && x.Confidence == PackageConfidence.High);
        Assert.DoesNotContain(dependencies, x => x.Name is not ("BepInEx" or "Reactor" or "MiraAPI"));
    }

    [Fact]
    public void ReplacesStaleMissingStatusWhenBepInExCoreNowExists()
    {
        using var temp = new TemporaryDirectory();
        var core = temp.Combine("BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.Core.dll"), "provider");
        var mod = new ModInfo
        {
            Id = "translate",
            IsEnabled = true,
            InstalledFiles = [new InstalledFileRecord { RelativePath = "BepInEx/plugins/SkeldTranslate.dll", IsCurrentlyApplied = true }],
            Dependencies = [new DependencyInfo { Name = "BepInEx", Status = DependencyStatus.Missing }]
        };
        var modpack = new ModpackInfo { GameDirectory = temp.Path, Mods = [mod] };

        var evaluated = new DependencyDetector().Evaluate(mod, modpack, temp.Path);

        Assert.Equal(DependencyStatus.Installed, Assert.Single(evaluated).Status);
    }
}
