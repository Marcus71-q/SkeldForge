using System.Text.RegularExpressions;
using Microsoft.Win32;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

public sealed partial class SteamDetector
{
    public const string AmongUsAppId = "945360";

    private readonly GameDetector _gameDetector;
    private readonly LoggingService? _logger;

    public SteamDetector(GameDetector? gameDetector = null, LoggingService? logger = null)
    {
        _gameDetector = gameDetector ?? new GameDetector();
        _logger = logger;
    }

    public async Task<SteamDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var steamRoots = FindSteamRoots(warnings);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            libraries.Add(steamRoot);

            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                var contents = await File.ReadAllTextAsync(libraryFile, cancellationToken).ConfigureAwait(false);
                foreach (var parsedPath in ParseLibraryFolders(contents))
                {
                    if (Directory.Exists(parsedPath))
                    {
                        libraries.Add(parsedPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read Steam library metadata: {exception.Message}");
                await LogWarningAsync("Could not read Steam library metadata.", exception).ConfigureAwait(false);
            }
        }

        var installations = new List<GameInstallation>();
        var seenGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{AmongUsAppId}.acf");
                var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                var installDirectoryName = string.IsNullOrWhiteSpace(manifest.InstallDirectory) ||
                                           !IsSafeInstallDirectoryName(manifest.InstallDirectory)
                    ? "Among Us"
                    : manifest.InstallDirectory;
                var gamePath = Path.GetFullPath(Path.Combine(library, "steamapps", "common", installDirectoryName));

                if (!seenGamePaths.Add(gamePath))
                {
                    continue;
                }

                var steamRoot = FindOwningSteamRoot(library, steamRoots);
                var installation = await _gameDetector.InspectAsync(
                    gamePath,
                    steamRoot,
                    library,
                    manifest.BuildId,
                    cancellationToken).ConfigureAwait(false);

                if (installation is not null)
                {
                    installations.Add(installation);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"Could not inspect Steam library '{library}': {exception.Message}");
                await LogWarningAsync("Could not inspect a Steam library.", exception).ConfigureAwait(false);
            }
        }

        // The uninstall entry is useful when Steam's primary registry values are stale.
        foreach (var installLocation in FindAmongUsUninstallLocations(warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenGamePaths.Add(installLocation))
            {
                continue;
            }

            var installation = await _gameDetector
                .InspectAsync(installLocation, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (installation is not null)
            {
                installations.Add(installation);
            }
        }

        var primarySteamRoot = steamRoots.FirstOrDefault() ?? string.Empty;
        var result = new SteamDetectionResult
        {
            SteamFound = steamRoots.Count > 0,
            SteamPath = primarySteamRoot,
            LibraryPaths = libraries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            AmongUsInstallations = installations
                .OrderBy(game => game.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = warnings
        };

        await LogInfoAsync(
            $"Steam detection finished. Steam found: {result.SteamFound}; libraries: {result.LibraryPaths.Count}; Among Us installations: {result.AmongUsInstallations.Count}.")
            .ConfigureAwait(false);
        return result;
    }

    public Task<SteamDetectionResult> DetectSteamAsync(CancellationToken cancellationToken = default)
        => DetectAsync(cancellationToken);

    public static IReadOnlyList<string> ParseLibraryFolders(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in PathEntryRegex().Matches(contents))
        {
            TryAddParsedPath(paths, match.Groups[1].Value);
        }

        // Steam used numeric key/value pairs before the nested "path" format.
        foreach (Match match in LegacyLibraryEntryRegex().Matches(contents))
        {
            TryAddParsedPath(paths, match.Groups[1].Value);
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<string> FindSteamRoots(List<string> warnings)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, subKey, valueName) in SteamRegistryLocations())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey, false);
                if (key?.GetValue(valueName) is string value)
                {
                    TryAddExistingDirectory(candidates, value);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                warnings.Add($"A Steam registry location could not be read: {exception.Message}");
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            TryAddExistingDirectory(candidates, Path.Combine(programFilesX86, "Steam"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            TryAddExistingDirectory(candidates, Path.Combine(programFiles, "Steam"));
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                TryAddExistingDirectory(candidates, Path.Combine(drive.RootDirectory.FullName, "Steam"));
                TryAddExistingDirectory(candidates, Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Known Steam folders could not all be checked: {exception.Message}");
        }

        return candidates
            .Where(path => File.Exists(Path.Combine(path, "steam.exe")) || Directory.Exists(Path.Combine(path, "steamapps")))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> FindAmongUsUninstallLocations(List<string> warnings)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {AmongUsAppId}",
                        false);
                    if (key?.GetValue("InstallLocation") is string path)
                    {
                        TryAddExistingDirectory(results, path);
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    warnings.Add($"The Among Us uninstall entry could not be read: {exception.Message}");
                }
            }
        }

        return results.ToArray();
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKey, string Value)> SteamRegistryLocations()
    {
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Valve\Steam", "SteamPath");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Valve\Steam", "InstallPath");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\WOW6432Node\Valve\Steam", "InstallPath");
    }

    private static async Task<SteamManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return new SteamManifest(string.Empty, string.Empty);
        }

        var contents = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return new SteamManifest(
            ReadVdfValue(contents, "installdir"),
            ReadVdfValue(contents, "buildid"));
    }

    private static string ReadVdfValue(string contents, string key)
    {
        var match = Regex.Match(
            contents,
            $"\"{Regex.Escape(key)}\"\\s*\"((?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? DecodeVdfString(match.Groups[1].Value) : string.Empty;
    }

    private static bool IsSafeInstallDirectoryName(string value)
        => value.Length is > 0 and <= 200 &&
           value is not "." and not ".." &&
           !Path.IsPathRooted(value) &&
           value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void TryAddParsedPath(HashSet<string> paths, string encodedPath)
    {
        var decoded = DecodeVdfString(encodedPath).Trim();
        if (!Path.IsPathFullyQualified(decoded))
        {
            return;
        }

        try
        {
            paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(decoded)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    private static string DecodeVdfString(string value)
        => value.Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    private static void TryAddExistingDirectory(HashSet<string> paths, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'))));
            if (Directory.Exists(normalized))
            {
                paths.Add(normalized);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    private static string FindOwningSteamRoot(string library, IReadOnlyList<string> steamRoots)
        => steamRoots.FirstOrDefault(root => root.Equals(library, StringComparison.OrdinalIgnoreCase))
            ?? steamRoots.FirstOrDefault()
            ?? string.Empty;

    private async Task LogWarningAsync(string message, Exception exception)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.WarningAsync(message, exception).ConfigureAwait(false);
        }
        catch (Exception loggingException) when (loggingException is not OperationCanceledException)
        {
        }
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.InfoAsync(message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    [GeneratedRegex("\"path\"\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathEntryRegex();

    [GeneratedRegex("^\\s*\"\\d+\"\\s*\"((?:\\\\.|[^\"])*)\"\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLibraryEntryRegex();

    private sealed record SteamManifest(string InstallDirectory, string BuildId);
}
