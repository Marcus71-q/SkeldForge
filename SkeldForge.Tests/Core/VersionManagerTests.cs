using SkeldForge.Core;

namespace SkeldForge.Tests.Core;

public sealed class VersionManagerTests
{
    [Fact]
    public void DetectDisplayVersion_PrefersAmongUsProductVersionFromUnityData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SkeldForgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Among Us_Data"));
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "Among Us_Data", "globalgamemanagers"),
                "header\01.0\02026.8.18\0footer"u8.ToArray());

            Assert.Equal("2026.8.18", new VersionManager().DetectDisplayVersion(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
