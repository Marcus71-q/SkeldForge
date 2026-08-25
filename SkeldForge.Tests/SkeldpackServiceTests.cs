using System.IO.Compression;
using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class SkeldpackServiceTests
{
    [Fact]
    public async Task ExportContainsOnlyMetadataManifestAndRoundTrips()
    {
        using var temp = new TemporaryDirectory();
        var output = temp.Combine("TOHE-Pack.skeldpack");
        var pack = new ModpackInfo
        {
            Name = "TOHE Pack",
            Version = "1.3",
            Mods =
            [
                new ModInfo
                {
                    Name = "TOHE",
                    Version = "2.4.2 Beta 15",
                    PackageHash = new string('a', 64),
                    IsEnabled = true
                }
            ]
        };
        var service = new SkeldpackService();

        var exported = await service.ExportAsync(pack, output);

        Assert.True(exported.Success, exported.TechnicalMessage);
        using (var archive = ZipFile.OpenRead(output))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("manifest.json", entry.FullName);
            Assert.DoesNotContain(archive.Entries, x =>
                x.FullName.Contains("Among Us", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }

        var imported = await service.ReadManifestAsync(output);
        Assert.True(imported.Success, imported.TechnicalMessage);
        Assert.Equal("TOHE Pack", imported.Value?.Name);
        Assert.Equal("1.3", imported.Value?.Version);
        Assert.Equal(new string('a', 64), imported.Value?.Mods.Single().Hash);
    }

    [Fact]
    public void RejectsUnsupportedFormatVersion()
    {
        var manifest = new SkeldpackManifest
        {
            FormatVersion = SkeldForgeVersion.SkeldpackFormat + 1,
            Name = "Future Pack",
            Version = "1.0"
        };

        var result = new SkeldpackService().ValidateManifest(manifest);

        Assert.False(result.Success);
        Assert.Equal("SkeldpackFormatUnsupported", result.ErrorKey);
    }
}
