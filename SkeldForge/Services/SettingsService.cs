using System.Globalization;
using System.Text;
using System.Text.Json;
using SkeldForge.Models;

namespace SkeldForge.Services;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsService(AppPaths paths, LoggingService? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
        Current = CreateDefaults();
    }

    public AppSettings Current { get; private set; }
    public AppSettings Settings => Current;
    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureBootstrapDirectories();

            if (!File.Exists(_paths.SettingsFilePath))
            {
                Current = CreateDefaults();
                ApplyAndEnsurePaths(Current);
                await SaveCoreAsync(Current, cancellationToken).ConfigureAwait(false);
                RaiseSettingsChanged();
                return Current;
            }

            try
            {
                await using var stream = new FileStream(
                    _paths.SettingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    32 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    JsonDefaults.Options,
                    cancellationToken).ConfigureAwait(false);

                if (loaded is null)
                {
                    throw new JsonException("The settings document was empty.");
                }

                try
                {
                    Current = MigrateAndNormalize(loaded);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new JsonException("The settings document contains an invalid path.", exception);
                }

                try
                {
                    ApplyAndEnsurePaths(Current);
                }
                catch (Exception exception) when (
                    (exception is IOException or UnauthorizedAccessException) &&
                    (!Current.DataRoot.Equals(_paths.DefaultDataRoot, StringComparison.OrdinalIgnoreCase) ||
                     !Current.BackupRoot.Equals(
                         Path.Combine(_paths.DefaultDataRoot, "Backups"),
                         StringComparison.OrdinalIgnoreCase)))
                {
                    await LogWarningAsync(
                        "The configured data root was unavailable; the per-user default was restored.",
                        exception).ConfigureAwait(false);
                    Current.DataRoot = _paths.DefaultDataRoot;
                    Current.BackupRoot = Path.Combine(Current.DataRoot, "Backups");
                    ApplyAndEnsurePaths(Current);
                    await SaveCoreAsync(Current, cancellationToken).ConfigureAwait(false);
                }

                RaiseSettingsChanged();
                return Current;
            }
            catch (JsonException exception)
            {
                await PreserveInvalidSettingsAsync(cancellationToken).ConfigureAwait(false);
                await LogWarningAsync("Invalid settings JSON was preserved and defaults were restored.", exception).ConfigureAwait(false);

                Current = CreateDefaults();
                ApplyAndEnsurePaths(Current);
                await SaveCoreAsync(Current, cancellationToken).ConfigureAwait(false);
                RaiseSettingsChanged();
                return Current;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
        => await SaveAsync(Current, cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = MigrateAndNormalize(settings);
            ApplyAndEnsurePaths(Current);
            await SaveCoreAsync(Current, cancellationToken).ConfigureAwait(false);
            RaiseSettingsChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resets preferences only; instances, archives, and backups are never deleted.</summary>
    public async Task<AppSettings> ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = CreateDefaults();
            ApplyAndEnsurePaths(Current);
            await SaveCoreAsync(Current, cancellationToken).ConfigureAwait(false);
            RaiseSettingsChanged();
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AppSettings CreateDefaults()
    {
        return new AppSettings
        {
            ConfigVersion = SkeldForgeVersion.ConfigSchema,
            Language = "en-US",
            DataRoot = _paths.DefaultDataRoot,
            BackupRoot = Path.Combine(_paths.DefaultDataRoot, "Backups")
        };
    }

    private AppSettings MigrateAndNormalize(AppSettings settings)
    {
        if (settings.ConfigVersion > SkeldForgeVersion.ConfigSchema)
        {
            throw new JsonException($"Unsupported settings schema version {settings.ConfigVersion}.");
        }

        // Schema 1 is the initial format. Keeping migration centralized makes
        // future upgrades deterministic without changing the persisted contract.
        settings.ConfigVersion = SkeldForgeVersion.ConfigSchema;
        string[] supportedLanguages =
        [
            "en-US", "tr-TR", "ru-RU", "es-ES", "ja-JP", "th-TH", "id-ID",
            "ko-KR", "zh-CN", "de-DE", "fr-FR", "pt-BR", "it-IT", "ar-SA", "hi-IN"
        ];
        settings.Language = supportedLanguages.FirstOrDefault(x =>
            x.Equals(settings.Language, StringComparison.OrdinalIgnoreCase)) ?? "en-US";
        settings.DataRoot = NormalizeConfiguredPath(settings.DataRoot, _paths.DefaultDataRoot);
        settings.BackupRoot = NormalizeConfiguredPath(settings.BackupRoot, Path.Combine(settings.DataRoot, "Backups"));
        settings.AmongUsPath = NormalizeOptionalPath(settings.AmongUsPath);
        if (settings.AmongUsPath.Length > 0)
        {
            if (IsSameOrChild(settings.AmongUsPath, settings.DataRoot))
            {
                settings.DataRoot = GetSafeDefaultDataRoot(settings.AmongUsPath);
            }

            if (IsSameOrChild(settings.AmongUsPath, settings.BackupRoot))
            {
                settings.BackupRoot = Path.Combine(settings.DataRoot, "Backups");
            }
        }

        settings.WindowWidth = Math.Max(settings.WindowWidth, 800);
        settings.WindowHeight = Math.Max(settings.WindowHeight, 600);
        return settings;
    }

    private void ApplyAndEnsurePaths(AppSettings settings)
    {
        _paths.ConfigureDataRoot(settings.DataRoot);
        _paths.ConfigureBackupRoot(settings.BackupRoot);
        _paths.EnsureDataDirectories();
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _paths.EnsureBootstrapDirectories();
        var temporaryPath = _paths.SettingsFilePath + $".tmp-{Guid.NewGuid():N}";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _paths.SettingsFilePath, true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task PreserveInvalidSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.SettingsFilePath))
        {
            return;
        }

        var invalidPath = Path.Combine(
            _paths.ConfigDirectory,
            $"settings.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");

        await using var source = new FileStream(
            _paths.SettingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            invalidPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeConfiguredPath(string? path, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(path) ? fallback : Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private string GetSafeDefaultDataRoot(string gamePath)
    {
        if (!IsSameOrChild(gamePath, _paths.DefaultDataRoot))
        {
            return _paths.DefaultDataRoot;
        }

        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkeldForgeData"));
    }

    private static bool IsSameOrChild(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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
            // A logging failure must not prevent recovery from invalid settings.
        }
    }

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, Current);
}
