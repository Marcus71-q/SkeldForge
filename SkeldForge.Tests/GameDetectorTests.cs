using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.Tests.Support;

namespace SkeldForge.Tests;

public sealed class GameDetectorTests
{
    [Fact]
    public void RequiresExecutableDataDirectoryAndBothUnityCoreFiles()
    {
        using var temp = new TemporaryDirectory();
        var detector = new GameDetector();

        File.WriteAllBytes(temp.Combine("Among Us.exe"), []);
        Directory.CreateDirectory(temp.Combine("Among Us_Data"));
        File.WriteAllBytes(temp.Combine("GameAssembly.dll"), []);

        var result = detector.Validate(temp.Path);

        Assert.False(result.IsValid);
        Assert.Equal(GameValidationStatus.CoreFilesMissing, result.Status);
        Assert.Contains("UnityPlayer.dll", result.MissingItems);
    }

    [Fact]
    public void AcceptsSyntheticDirectoryWithExpectedGameShape()
    {
        using var temp = new TemporaryDirectory();
        CreateGameShape(temp);

        var result = new GameDetector().Validate(temp.Path);

        Assert.True(result.IsValid);
        Assert.Equal(GameValidationStatus.Valid, result.Status);
        Assert.Equal(System.IO.Path.GetFullPath(temp.Path), result.Path);
    }

    internal static void CreateGameShape(TemporaryDirectory temp)
    {
        File.WriteAllText(temp.Combine("Among Us.exe"), "synthetic executable");
        File.WriteAllText(temp.Combine("GameAssembly.dll"), "synthetic game assembly");
        File.WriteAllText(temp.Combine("UnityPlayer.dll"), "synthetic unity player");
        Directory.CreateDirectory(temp.Combine("Among Us_Data"));
        File.WriteAllText(temp.Combine("Among Us_Data", "globalgamemanagers"), "synthetic data");
    }
}
