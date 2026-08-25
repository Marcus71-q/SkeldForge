namespace SkeldForge.Tests.Support;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SkeldForge.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
        => parts.Aggregate(Path, System.IO.Path.Combine);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
        catch (IOException)
        {
            // A failing assertion remains more useful than cleanup noise.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
