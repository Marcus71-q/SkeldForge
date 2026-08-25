using System.Security.Cryptography;
using System.IO;

namespace SkeldForge.Services;

public sealed class HashService
{
    public async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public string ComputeSha256(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    public Task<string> ComputeStreamSha256Async(
        Stream stream,
        CancellationToken cancellationToken = default)
        => ComputeSha256Async(stream, cancellationToken);
}
