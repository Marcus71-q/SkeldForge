using SkeldForge.Services;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class ZipServiceSecurityTests
{
    [Fact]
    public async Task RejectsTraversalAndWritesNothingOutsideDestination()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = temp.Combine("malicious.zip");
        await TestArchive.CreateAsync(archivePath, ("../../outside.txt", "owned"));
        var destination = temp.Combine("instance", "Game");
        var escaped = temp.Combine("outside.txt");
        var service = new ZipService();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExtractSafeAsync(archivePath, destination));

        Assert.False(File.Exists(escaped));
        Assert.False(Directory.Exists(destination) && Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData("/rooted.txt")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("BepInEx/plugins/file.dll:payload")]
    [InlineData("BepInEx/../evil.dll")]
    public async Task RejectsUnsafeWindowsEntryNames(string entryName)
    {
        using var temp = new TemporaryDirectory();
        var archivePath = temp.Combine("unsafe.zip");
        await TestArchive.CreateAsync(archivePath, (entryName, "x"));

        Assert.Throws<InvalidDataException>(() => new ZipService().ValidateArchive(archivePath));
    }

    [Fact]
    public async Task ExtractsSafeBepInExPackageInsideDestination()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = temp.Combine("safe.zip");
        await TestArchive.CreateAsync(
            archivePath,
            ("BepInEx/plugins/Example.dll", "plugin"),
            ("BepInEx/config/Example.cfg", "enabled=true"));
        var destination = temp.Combine("Game");

        await new ZipService().ExtractSafeAsync(archivePath, destination);

        Assert.Equal("plugin", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "BepInEx", "plugins", "Example.dll")));
        Assert.Equal("enabled=true", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "BepInEx", "config", "Example.cfg")));
    }
}
