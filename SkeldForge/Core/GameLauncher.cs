using System.ComponentModel;
using System.Diagnostics;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>Provides deliberately separate vanilla and isolated launch paths.</summary>
public sealed class GameLauncher
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    private readonly AppPaths _paths;
    private readonly GameDetector _gameDetector;
    private readonly LoggingService? _logger;
    private readonly HashService _hashService;
    private readonly GameInstanceManager? _instanceManager;
    private readonly DependencyDetector _dependencyDetector;

    public GameLauncher(
        AppPaths paths,
        GameDetector? gameDetector = null,
        LoggingService? logger = null,
        HashService? hashService = null,
        GameInstanceManager? instanceManager = null,
        DependencyDetector? dependencyDetector = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _gameDetector = gameDetector ?? new GameDetector();
        _logger = logger;
        _hashService = hashService ?? new HashService();
        _instanceManager = instanceManager;
        _dependencyDetector = dependencyDetector ?? new DependencyDetector();
    }

    public async Task<OperationResult<GameLaunchResult>> LaunchVanillaAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var validation = await _gameDetector.ValidateAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            await LogWarningAsync($"Vanilla launch rejected: {validation.Status}.").ConfigureAwait(false);
            return OperationResult<GameLaunchResult>.Fail(
                validation.MessageKey,
                $"Missing: {string.Join(", ", validation.MissingItems)}");
        }

        if (IsInside(_paths.InstancesDirectory, validation.Path))
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.VanillaPathIsInstance",
                "LaunchVanillaAsync was given a managed instance path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(validation.Path, GameDetector.ExecutableName);

        if (LooksLikeSteamInstallation(validation.Path))
        {
            try
            {
                using var steamProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{SteamDetector.AmongUsAppId}",
                    UseShellExecute = true
                });

                var steamResult = new GameLaunchResult
                {
                    IsVanilla = true,
                    UsedSteamProtocol = true,
                    ExecutablePath = executable,
                    ProcessId = TryGetProcessId(steamProcess),
                    LaunchedAt = DateTimeOffset.UtcNow
                };
                await LogInfoAsync("Launched the validated vanilla installation through Steam.").ConfigureAwait(false);
                return OperationResult<GameLaunchResult>.Ok(steamResult);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                await LogWarningAsync("Steam protocol launch failed; attempting the validated vanilla executable.", exception)
                    .ConfigureAwait(false);
            }
        }

        return await StartExecutableAsync(executable, validation.Path, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<GameLaunchResult>> LaunchModdedAsync(
        ModpackInfo instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string instanceDirectory;
        string gameDirectory;
        try
        {
            instanceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.InstanceDirectory));
            gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.GameDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult<GameLaunchResult>.Fail("Launch.InvalidInstancePath", exception.Message);
        }

        if (!Directory.Exists(_paths.InstancesDirectory) || IsReparsePoint(_paths.InstancesDirectory))
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.UnsafeInstancesRoot",
                "The managed Instances root is missing or is a reparse point.");
        }

        if (!IsDirectChild(_paths.InstancesDirectory, instanceDirectory) ||
            !gameDirectory.Equals(Path.Combine(instanceDirectory, GameInstanceManager.GameFolderName), StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.InstanceOutsideDataRoot",
                "The instance is outside SkeldForge's managed Instances directory.");
        }


        if (!Directory.Exists(instanceDirectory) || !Directory.Exists(gameDirectory) ||
            IsReparsePoint(instanceDirectory) || IsReparsePoint(gameDirectory))
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.UnsafeInstancePath",
                "Managed instance and Game directories must be real directories, not reparse points.");
        }

        if (!string.IsNullOrWhiteSpace(instance.SourceGamePath))
        {
            try
            {
                if (gameDirectory.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.SourceGamePath)),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult<GameLaunchResult>.Fail("Launch.InstanceMatchesVanilla");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return OperationResult<GameLaunchResult>.Fail("Launch.InvalidSourcePath", exception.Message);
            }
        }

        var validation = await _gameDetector.ValidateAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.InstanceCorrupted",
                $"Missing: {string.Join(", ", validation.MissingItems)}");
        }

        var incompatibleVersion = DetectKnownGameVersionMismatch(instance, gameDirectory);
        if (incompatibleVersion is not null)
        {
            await LogWarningAsync(incompatibleVersion).ConfigureAwait(false);
            return OperationResult<GameLaunchResult>.Fail("Launch.IncompatibleGameVersion", incompatibleVersion);
        }

        var ownership = new Dictionary<string, (string ModName, string Hash)>(StringComparer.OrdinalIgnoreCase);
        var knownIncompatible = instance.Mods.FirstOrDefault(mod =>
            mod.IsEnabled && mod.Compatibility == ModCompatibilityStatus.KnownIncompatible);
        if (knownIncompatible is not null)
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.KnownIncompatibleMod",
                $"Enabled mod '{knownIncompatible.Name}' is marked as known incompatible.");
        }

        // Dependency status is cached in modpack metadata for display, but providers can be
        // installed or removed later. Always validate the current instance before launch.
        foreach (var mod in instance.Mods.Where(mod => mod.IsEnabled))
        {
            mod.Dependencies = _dependencyDetector.Evaluate(mod, instance, gameDirectory).ToList();
        }

        // Dependency detection is intentionally evidence-based and can only provide a
        // best-effort result.  A loader/provider may be supplied by an untracked file,
        // generated on first launch, or use a layout/version that SkeldForge does not
        // know yet.  Treating that heuristic as a hard launch gate made every loose DLL
        // mod unusable in a clean instance.  Keep the status for diagnostics, but let
        // BepInEx/the plug-in report the authoritative runtime result.
        foreach (var warning in GetDependencyWarnings(instance))
        {
            await LogWarningAsync(warning).ConfigureAwait(false);
        }

        var incorrectlyAppliedDisabledMod = instance.Mods
            .Where(item => !item.IsEnabled)
            .FirstOrDefault(item => item.InstalledFiles.Any(file => file.IsCurrentlyApplied));
        if (incorrectlyAppliedDisabledMod is not null)
        {
            return OperationResult<GameLaunchResult>.Fail(
                "Launch.DisabledModStillApplied",
                $"Disabled mod '{incorrectlyAppliedDisabledMod.Name}' still has applied files.");
        }

        foreach (var mod in instance.Mods.Where(item => item.IsEnabled))
        {
            foreach (var file in mod.InstalledFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.IsCurrentlyApplied)
                {
                    return OperationResult<GameLaunchResult>.Fail(
                        "Launch.ModNotApplied",
                        $"Enabled mod '{mod.Name}' has a disabled file '{file.RelativePath}'.");
                }

                string installedPath;
                try
                {
                    installedPath = GetContainedPath(gameDirectory, file.RelativePath);
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
                {
                    return OperationResult<GameLaunchResult>.Fail("Launch.UnsafeTrackedPath", exception.Message);
                }

                if (!File.Exists(installedPath))
                {
                    return OperationResult<GameLaunchResult>.Fail(
                        "Launch.ModFileMissing",
                        $"Enabled mod file is missing: '{file.RelativePath}'.");
                }

                if (ownership.TryGetValue(file.RelativePath, out var existing) &&
                    (string.IsNullOrWhiteSpace(existing.Hash) ||
                     !existing.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    return OperationResult<GameLaunchResult>.Fail(
                        "Launch.UnresolvedConflict",
                        $"Mods '{existing.ModName}' and '{mod.Name}' both own '{file.RelativePath}'.");
                }

                ownership[file.RelativePath] = (mod.Name, file.Sha256);

                if (Path.GetExtension(installedPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(file.Sha256))
                {
                    var actualHash = await _hashService
                        .ComputeFileSha256Async(installedPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return OperationResult<GameLaunchResult>.Fail(
                            "Launch.ModFileChanged",
                            $"Tracked binary changed: '{file.RelativePath}'.");
                    }
                }
            }
        }

        var executable = Path.Combine(gameDirectory, GameDetector.ExecutableName);
        var launch = await StartExecutableAsync(executable, gameDirectory, false, cancellationToken).ConfigureAwait(false);
        if (!launch.Success)
        {
            return launch;
        }

        instance.LastLaunchedAt = launch.Value?.LaunchedAt ?? DateTimeOffset.UtcNow;
        if (_instanceManager is not null)
        {
            var save = await _instanceManager.SaveAsync(instance, CancellationToken.None).ConfigureAwait(false);
            if (!save.Success)
            {
                await LogWarningAsync($"The modpack launched, but its last-launched timestamp could not be saved: {save.TechnicalMessage}")
                    .ConfigureAwait(false);
            }
        }

        return launch;
    }

    public Task<OperationResult<GameLaunchResult>> LaunchInstanceAsync(
        ModpackInfo instance,
        CancellationToken cancellationToken = default)
        => LaunchModdedAsync(instance, cancellationToken);

    internal static IReadOnlyList<string> GetDependencyWarnings(ModpackInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Mods
            .Where(mod => mod.IsEnabled)
            .SelectMany(mod => mod.Dependencies
                .Where(dependency => dependency.Status == DependencyStatus.Missing)
                .Select(dependency =>
                    $"Mod '{mod.Name}' may be missing '{dependency.Name}'. Launch was allowed because dependency detection is advisory."))
            .ToArray();
    }

    private async Task<OperationResult<GameLaunchResult>> StartExecutableAsync(
        string executable,
        string workingDirectory,
        bool vanilla,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(workingDirectory) || !File.Exists(executable))
        {
            return OperationResult<GameLaunchResult>.Fail("Launch.ExecutableMissing");
        }

        await LaunchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runningProcess = FindRunningAmongUsProcess();
            if (runningProcess is not null)
            {
                await LogWarningAsync($"Launch rejected because Among Us is already running (PID {runningProcess.Value.ProcessId}, '{runningProcess.Value.Path ?? "unknown path"}').")
                    .ConfigureAwait(false);
                return OperationResult<GameLaunchResult>.Fail(
                    "Launch.AlreadyRunning",
                    $"PID {runningProcess.Value.ProcessId}: {runningProcess.Value.Path ?? "Among Us.exe"}");
            }

            ProcessStartInfo startInfo;
            if (vanilla)
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
            }
            else
            {
                await EnsureSteamAppIdAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
                startInfo = CreateModdedStartInfo(executable, workingDirectory);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return OperationResult<GameLaunchResult>.Fail(
                    "Launch.ProcessStartFailed",
                    "Windows did not return a process for the launch request.");
            }

            var result = new GameLaunchResult
            {
                IsVanilla = vanilla,
                UsedSteamProtocol = false,
                ExecutablePath = executable,
                ProcessId = TryGetProcessId(process),
                LaunchedAt = DateTimeOffset.UtcNow
            };
            await LogInfoAsync(vanilla
                ? "Launched the validated vanilla Among Us executable."
                : "Launched a validated isolated modpack executable.").ConfigureAwait(false);
            return OperationResult<GameLaunchResult>.Ok(result);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await LogErrorAsync(vanilla ? "Vanilla launch failed." : "Modded launch failed.", exception)
                .ConfigureAwait(false);
            return OperationResult<GameLaunchResult>.Fail("Launch.ProcessStartFailed", exception.Message);
        }
        finally
        {
            LaunchGate.Release();
        }
    }

    internal static ProcessStartInfo CreateModdedStartInfo(string executable, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        startInfo.Environment["SteamAppId"] = SteamDetector.AmongUsAppId;
        startInfo.Environment["SteamGameId"] = SteamDetector.AmongUsAppId;
        return startInfo;
    }

    internal static async Task EnsureSteamAppIdAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var appIdPath = Path.Combine(workingDirectory, "steam_appid.txt");
        var current = File.Exists(appIdPath)
            ? (await File.ReadAllTextAsync(appIdPath, cancellationToken).ConfigureAwait(false)).Trim()
            : string.Empty;
        if (!current.Equals(SteamDetector.AmongUsAppId, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(appIdPath, SteamDetector.AmongUsAppId + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static (int ProcessId, string? Path)? FindRunningAmongUsProcess()
    {
        foreach (var process in Process.GetProcessesByName("Among Us"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        return (process.Id, process.MainModule?.FileName);
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            return (process.Id, null);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        return null;
    }

    internal static string? DetectKnownGameVersionMismatch(ModpackInfo instance, string gameDirectory)
    {
        var tohe = instance.Mods.FirstOrDefault(mod =>
            mod.IsEnabled &&
            (mod.Name.Equals("TOHE", StringComparison.OrdinalIgnoreCase) ||
             mod.InstalledFiles.Any(file => file.RelativePath.Equals(
                 "BepInEx/plugins/TOHE.dll", StringComparison.OrdinalIgnoreCase))));
        if (tohe is null ||
            !(tohe.Version.StartsWith("2026.514.242", StringComparison.OrdinalIgnoreCase) ||
              tohe.SourceArchiveName.Contains("2.4.2Beta15", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var detected = new VersionManager().DetectGameProductVersion(gameDirectory);
        if (!Version.TryParse(detected, out var gameVersion) || gameVersion <= new Version(2025, 11, 18))
        {
            return null;
        }

        return $"TOHE 2.4.2 Beta 15 supports Among Us 2025.11.18, but this instance contains Among Us {detected}.";
    }

    private static bool LooksLikeSteamInstallation(string gameDirectory)
    {
        var directory = new DirectoryInfo(gameDirectory);
        var common = directory.Parent;
        var steamApps = common?.Parent;
        return common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true &&
               steamApps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetContainedPath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A tracked mod file has an invalid relative path.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInside(root, candidate) || candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Tracked file escapes the instance: '{relativePath}'.");
        }

        return candidate;
    }

    private static bool IsDirectChild(string rootDirectory, string candidateDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDirectory));
        return Directory.GetParent(candidate)?.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsInside(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryGetProcessId(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private Task LogInfoAsync(string message) => LogSafelyAsync(LogLevel.Information, message, null);
    private Task LogWarningAsync(string message, Exception? exception = null) => LogSafelyAsync(LogLevel.Warning, message, exception);
    private Task LogErrorAsync(string message, Exception exception) => LogSafelyAsync(LogLevel.Error, message, exception);

    private async Task LogSafelyAsync(LogLevel level, string message, Exception? exception)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.LogAsync(level, message, exception).ConfigureAwait(false);
        }
        catch (Exception loggingException) when (loggingException is not OperationCanceledException)
        {
        }
    }
}
