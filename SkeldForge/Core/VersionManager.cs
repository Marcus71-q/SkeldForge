using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

public sealed partial class VersionManager
{
    private static readonly string[] FingerprintFiles =
    [
        "Among Us.exe",
        "GameAssembly.dll",
        "UnityPlayer.dll",
        Path.Combine("Among Us_Data", "globalgamemanagers"),
        Path.Combine("Among Us_Data", "resources.assets"),
        Path.Combine("Among Us_Data", "sharedassets0.assets"),
        Path.Combine("Among Us_Data", "level0"),
        Path.Combine("Among Us_Data", "boot.config"),
        Path.Combine("Among Us_Data", "app.info"),
        Path.Combine("Among Us_Data", "il2cpp_data", "Metadata", "global-metadata.dat")
    ];

    private readonly HashService _hashService;

    public VersionManager(HashService? hashService = null)
    {
        _hashService = hashService ?? new HashService();
    }

    public string ApplicationVersion => SkeldForgeVersion.Current;

    public string DetectDisplayVersion(string gameDirectory, string? steamBuildId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var gameVersion = DetectGameProductVersion(gameDirectory);
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            return gameVersion;
        }

        var executable = Path.Combine(Path.GetFullPath(gameDirectory), "Among Us.exe");
        if (File.Exists(executable))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(executable);
                var productVersion = CleanVersion(info.ProductVersion);
                if (!string.IsNullOrWhiteSpace(productVersion))
                {
                    return productVersion;
                }

                var fileVersion = CleanVersion(info.FileVersion);
                if (!string.IsNullOrWhiteSpace(fileVersion))
                {
                    return fileVersion;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Steam's build id remains a useful non-invented fallback.
            }
        }

        return string.IsNullOrWhiteSpace(steamBuildId) ? "Unknown" : $"Steam build {steamBuildId.Trim()}";
    }

    public string DetectGameProductVersion(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var managersPath = Path.Combine(Path.GetFullPath(gameDirectory), "Among Us_Data", "globalgamemanagers");
        if (!File.Exists(managersPath))
        {
            return string.Empty;
        }

        try
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(managersPath));
            var version = GameVersionRegex().Matches(text)
                .Select(match => new
                {
                    Text = match.Value,
                    Value = new Version(
                        int.Parse(match.Groups[1].Value),
                        int.Parse(match.Groups[2].Value),
                        int.Parse(match.Groups[3].Value),
                        match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0)
                })
                .Where(item => item.Value.Major >= 2020 && item.Value.Minor is >= 1 and <= 12 && item.Value.Build is >= 1 and <= 31)
                .OrderByDescending(item => item.Value)
                .FirstOrDefault();
            return version?.Text ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        {
            return string.Empty;
        }
    }

    public Task<string> GetGameVersionAsync(
        string gameDirectory,
        string? steamBuildId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DetectDisplayVersion(gameDirectory, steamBuildId));
    }

    /// <summary>
    /// Hashes the executable and important Unity data files. Relative names and
    /// lengths are included, so add/remove/replace changes cannot collapse into
    /// the same source fingerprint.
    /// </summary>
    public async Task<string> ComputeGameFingerprintAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => await ComputeFingerprintCoreAsync(gameDirectory, true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Computes a copy-verification fingerprint that deliberately omits timestamps,
    /// which can lose precision when instances are stored on a different file system.
    /// </summary>
    public Task<string> ComputeGameContentFingerprintAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => ComputeFingerprintCoreAsync(gameDirectory, false, cancellationToken);

    private async Task<string> ComputeFingerprintCoreAsync(
        string gameDirectory,
        bool includeTimestamps,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Game directory not found: '{root}'.");
        }

        var contentHashPaths = FingerprintFiles
            .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<FingerprintEntry>();
        var files = EnumerateFilesSafely(root, cancellationToken)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase);
        foreach (var fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"A reparse-point file cannot be fingerprinted safely: '{fullPath}'.");
            }

            var relativePath = Path.GetRelativePath(root, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(fullPath);
            var hash = contentHashPaths.Contains(relativePath)
                ? await _hashService.ComputeFileSha256Async(fullPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            entries.Add(new FingerprintEntry(
                relativePath,
                info.Length,
                includeTimestamps ? info.LastWriteTimeUtc.Ticks : 0,
                hash));
        }

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var canonical = JsonSerializer.Serialize(entries, JsonDefaults.Options);
        return _hashService.ComputeSha256(Encoding.UTF8.GetBytes(canonical));
    }

    public Task<string> ComputeFingerprintAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => ComputeGameFingerprintAsync(gameDirectory, cancellationToken);

    public GameUpdateComparison Compare(GameInstallation current, ModpackInfo instance)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(instance);

        var fingerprintChanged = !string.IsNullOrWhiteSpace(current.Fingerprint) &&
            !string.IsNullOrWhiteSpace(instance.SourceGameFingerprint) &&
            !current.Fingerprint.Equals(instance.SourceGameFingerprint, StringComparison.OrdinalIgnoreCase);
        var buildChanged = !string.IsNullOrWhiteSpace(current.BuildId) &&
            !string.IsNullOrWhiteSpace(instance.SourceGameBuildId) &&
            !current.BuildId.Equals(instance.SourceGameBuildId, StringComparison.OrdinalIgnoreCase);

        return new GameUpdateComparison
        {
            HasChanged = fingerprintChanged || buildChanged,
            CurrentVersion = current.DisplayVersion,
            InstanceVersion = instance.SourceGameVersion,
            CurrentFingerprint = current.Fingerprint,
            InstanceFingerprint = instance.SourceGameFingerprint
        };
    }

    public async Task<GameUpdateComparison> CompareAsync(
        string currentGameDirectory,
        ModpackInfo instance,
        string? currentBuildId = null,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = await ComputeGameFingerprintAsync(currentGameDirectory, cancellationToken).ConfigureAwait(false);
        var installation = new GameInstallation
        {
            Path = Path.GetFullPath(currentGameDirectory),
            ExecutablePath = Path.Combine(Path.GetFullPath(currentGameDirectory), "Among Us.exe"),
            BuildId = currentBuildId ?? string.Empty,
            DisplayVersion = DetectDisplayVersion(currentGameDirectory, currentBuildId),
            Fingerprint = fingerprint
        };

        return Compare(installation, instance);
    }

    private static string CleanVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static IReadOnlyList<string> EnumerateFilesSafely(
        string root,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"A reparse-point directory cannot be fingerprinted safely: '{current}'.");
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"A reparse-point directory cannot be fingerprinted safely: '{child}'.");
                }

                pending.Push(child);
            }

            results.AddRange(Directory.EnumerateFiles(current));
        }

        return results;
    }

    private sealed record FingerprintEntry(string Path, long Length, long LastWriteUtcTicks, string Sha256);

    [GeneratedRegex(@"(?<!\d)(20\d{2})\.(\d{1,2})\.(\d{1,2})(?:\.(\d+))?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionRegex();
}
