using System.Text;
using SkeldForge.Services;

namespace SkeldForge.Tests;

public sealed class HashServiceTests
{
    [Fact]
    public async Task ComputesKnownSha256InLowercase()
    {
        var service = new HashService();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("SkeldForge"));

        var hash = await service.ComputeSha256Async(stream);

        Assert.Equal("491bbfb926a05d7936844f9e420f95fcda65488715e1ec8e7dd4af163d42a046", hash);
    }
}
