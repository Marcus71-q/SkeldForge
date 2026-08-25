using SkeldForge.Models;
using System.Text.RegularExpressions;

namespace SkeldForge.Core;

public sealed class GameDetector
{
    public const string ExecutableName = "Among Us.exe";
    public const string DataDirectoryName = "Among Us_Data";

    private static readonly string[] CoreFiles = ["GameAssembly.dll", "UnityPlayer.dll"];
    private readonly VersionManager _versionManager;

    public GameDetector(VersionManager? versionManager = null)
    {
        _versionManager = versionManager ?? new VersionManager();
    }

    public GameValidationResult Validate(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return Invalid(GameValidationStatus.DirectoryMissing, directoryPath ?? string.Empty, "GamePath.DirectoryMissing");
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(directoryPath.Trim())));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(GameValidationStatus.Invalid, directoryPath, "GamePath.Invalid");
        }

        if (!Directory.Exists(root))
        {
            return Invalid(GameValidationStatus.DirectoryMissing, root, "GamePath.DirectoryMissing");
        }

        try
        {
            // Forces an access check instead of treating inaccessible directories as valid.
            _ = Directory.EnumerateFileSystemEntries(root).Take(1).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(GameValidationStatus.AccessDenied, root, "GamePath.AccessDenied");
        }
        catch (IOException)
        {
            return Invalid(GameValidationStatus.Invalid, root, "GamePath.Invalid");
        }

        var missing = new List<string>();
        if (!File.Exists(Path.Combine(root, ExecutableName)))
        {
            missing.Add(ExecutableName);
        }

        if (!Directory.Exists(Path.Combine(root, DataDirectoryName)))
        {
            missing.Add(DataDirectoryName + Path.DirectorySeparatorChar);
        }

        missing.AddRange(CoreFiles.Where(file => !File.Exists(Path.Combine(root, file))));

        if (missing.Count == 0)
        {
            return new GameValidationResult
            {
                Status = GameValidationStatus.Valid,
                Path = root,
                MissingItems = [],
                MessageKey = "GamePath.Valid"
            };
        }

        var status = missing.Contains(ExecutableName, StringComparer.OrdinalIgnoreCase)
            ? GameValidationStatus.ExecutableMissing
            : missing.Any(item => item.StartsWith(DataDirectoryName, StringComparison.OrdinalIgnoreCase))
                ? GameValidationStatus.DataDirectoryMissing
                : GameValidationStatus.CoreFilesMissing;

        return new GameValidationResult
        {
            Status = status,
            Path = root,
            MissingItems = missing,
            MessageKey = "GamePath.NotAmongUs"
        };
    }

    public Task<GameValidationResult> ValidateAsync(
        string? directoryPath,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Validate(directoryPath);
        }, cancellationToken);

    public async Task<OperationResult<GameInstallation>> DetectInstallationAsync(
        string directoryPath,
        string? steamRoot = null,
        string? steamLibrary = null,
        string? buildId = null,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(directoryPath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return OperationResult<GameInstallation>.Fail(
                validation.MessageKey,
                $"Missing or invalid game items: {string.Join(", ", validation.MissingItems)}");
        }

        try
        {
            var steamMetadata = await ReadSteamMetadataAsync(
                validation.Path,
                steamRoot,
                steamLibrary,
                buildId,
                cancellationToken).ConfigureAwait(false);
            var fingerprint = await _versionManager
                .ComputeGameFingerprintAsync(validation.Path, cancellationToken)
                .ConfigureAwait(false);

            return OperationResult<GameInstallation>.Ok(new GameInstallation
            {
                Path = validation.Path,
                ExecutablePath = Path.Combine(validation.Path, ExecutableName),
                SteamRoot = steamMetadata.SteamRoot,
                SteamLibrary = steamMetadata.SteamLibrary,
                BuildId = steamMetadata.BuildId,
                DisplayVersion = _versionManager.DetectDisplayVersion(validation.Path, steamMetadata.BuildId),
                Fingerprint = fingerprint,
                DetectedAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult<GameInstallation>.Fail("GamePath.ReadFailed", exception.Message);
        }
    }

    public async Task<GameInstallation?> InspectAsync(
        string directoryPath,
        string? steamRoot = null,
        string? steamLibrary = null,
        string? buildId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DetectInstallationAsync(
            directoryPath, steamRoot, steamLibrary, buildId, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Value : null;
    }

    private static GameValidationResult Invalid(GameValidationStatus status, string path, string messageKey)
        => new()
        {
            Status = status,
            Path = path,
            MissingItems = [],
            MessageKey = messageKey
        };

    private static string NormalizeOptionalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static async Task<SteamMetadata> ReadSteamMetadataAsync(
        string gamePath,
        string? steamRoot,
        string? steamLibrary,
        string? buildId,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeOptionalDirectory(steamRoot);
        var normalizedLibrary = NormalizeOptionalDirectory(steamLibrary);
        var normalizedBuildId = buildId?.Trim() ?? string.Empty;

        var gameDirectory = new DirectoryInfo(gamePath);
        var commonDirectory = gameDirectory.Parent;
        var steamAppsDirectory = commonDirectory?.Parent;
        if (commonDirectory?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true ||
            steamAppsDirectory?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new SteamMetadata(normalizedRoot, normalizedLibrary, normalizedBuildId);
        }

        var inferredLibrary = steamAppsDirectory.Parent?.FullName ?? string.Empty;
        if (normalizedLibrary.Length == 0)
        {
            normalizedLibrary = NormalizeOptionalDirectory(inferredLibrary);
        }

        if (normalizedRoot.Length == 0 && File.Exists(Path.Combine(inferredLibrary, "steam.exe")))
        {
            normalizedRoot = NormalizeOptionalDirectory(inferredLibrary);
        }

        if (normalizedBuildId.Length == 0)
        {
            var manifest = Path.Combine(steamAppsDirectory.FullName, $"appmanifest_{SteamDetector.AmongUsAppId}.acf");
            if (File.Exists(manifest))
            {
                var contents = await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false);
                var match = Regex.Match(
                    contents,
                    "\"buildid\"\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    normalizedBuildId = match.Groups[1].Value.Trim();
                }
            }
        }

        return new SteamMetadata(normalizedRoot, normalizedLibrary, normalizedBuildId);
    }

    private sealed record SteamMetadata(string SteamRoot, string SteamLibrary, string BuildId);
}
