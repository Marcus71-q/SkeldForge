using System.IO.Compression;
using SkeldForge.Core;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class ModPackageInputTests
{
    [Fact]
    public async Task LooseDllIsNormalizedAndAnalyzedAsBepInExPlugin()
    {
        using var temp = new TemporaryDirectory();
        var dll = temp.Combine("LoosePlugin.dll");
        await File.WriteAllTextAsync(dll, "synthetic plugin payload");

        using var input = ModPackageInput.Prepare(dll);
        var analysis = await new ModPackageAnalyzer().AnalyzeAsync(input.ArchivePath);

        Assert.True(analysis.IsSafe, analysis.RejectionReason);
        Assert.True(analysis.HasPluginDlls);
        Assert.Contains(analysis.PluginFiles, path =>
            path.Equals("LoosePlugin.dll", StringComparison.OrdinalIgnoreCase));
        using var archive = ZipFile.OpenRead(input.ArchivePath);
        Assert.Equal("LoosePlugin.dll", Assert.Single(archive.Entries).FullName);
    }

    [Fact]
    public void ZipIsPassedThroughWithoutCreatingAnotherArchive()
    {
        using var temp = new TemporaryDirectory();
        var zip = temp.Combine("Existing.zip");
        using (ZipFile.Open(zip, ZipArchiveMode.Create)) { }

        using var input = ModPackageInput.Prepare(zip);

        Assert.Equal(Path.GetFullPath(zip), input.ArchivePath);
    }

    [Fact]
    public void UnsupportedLooseFileIsRejected()
    {
        using var temp = new TemporaryDirectory();
        var file = temp.Combine("readme.txt");
        File.WriteAllText(file, "not a mod");

        Assert.Throws<InvalidDataException>(() => ModPackageInput.Prepare(file));
    }
}
