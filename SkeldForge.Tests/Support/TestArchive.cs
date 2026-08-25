using System.IO.Compression;
using System.Text;

namespace SkeldForge.Tests.Support;

internal static class TestArchive
{
    public static async Task CreateAsync(
        string path,
        params (string Name, string Content)[] entries)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            await using var output = entry.Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes(item.Content));
        }
    }
}
