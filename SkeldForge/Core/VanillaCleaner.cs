using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Conservatively identifies positively evidenced mod-loader content. Unknown files and
/// directories are always left untouched. Cleanup requires a prior scan result and a
/// complete verified backup before the first deletion.
/// </summary>
public sealed class VanillaCleaner
{
    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Among Us.exe",
        "GameAssembly.dll",
        "UnityPlayer.dll",
        "UnityCrashHandler64.exe",
        "baselib.dll",
        "steam_api64.dll",
        "steam_appid.txt"
    };

    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Among Us_Data",
        "MonoBleedingEdge"
    };

    private readonly IBackupManager _backupManager;
    private readonly HashService _hashService;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VanillaCleaner(
        IBackupManager backupManager,
        HashService? hashService = null,
        LoggingService? logger = null)
    {
        _backupManager = backupManager ?? throw new ArgumentNullException(nameof(backupManager));
        _hashService = hashService ?? new HashService();
        _logger = logger;
    }

    public async Task<OperationResult<CleanupScanResult>> ScanAsync(
        string gamePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
            if (!Directory.Exists(root))
            {
                return OperationResult<CleanupScanResult>.Fail("CleanupGamePathMissing", root);
            }

            RejectReparsePoint(root);
            var topLevel = Directory.EnumerateFileSystemEntries(root).ToArray();
            var result = new CleanupScanResult { GamePath = root, ScannedAt = DateTimeOffset.UtcNow };
            var bepinexPath = Path.Combine(root, "BepInEx");
            var bepinexDetected = Directory.Exists(bepinexPath) && IsRecognizedBepInExDirectory(bepinexPath);
            var doorstopPath = Path.Combine(root, "doorstop_config.ini");
            var doorstopDetected = File.Exists(doorstopPath) && IsBepInExDoorstopConfig(doorstopPath);
            var loaderBundleDetected = bepinexDetected || doorstopDetected;

            for (var index = 0; index < topLevel.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = topLevel[index];
                var name = Path.GetFileName(path);
                var isDirectory = Directory.Exists(path);

                if (isDirectory && ProtectedDirectories.Contains(name) || !isDirectory && ProtectedFiles.Contains(name))
                {
                    result.Findings.Add(CreateFinding(root, path,
                        CleanupClassification.ProtectedGameComponent, false,
                        "Known Among Us or Unity runtime component."));
                }
                else if (isDirectory && name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && bepinexDetected)
                {
                    AddKnownTree(result, root, path, "Recognized BepInEx directory structure.", cancellationToken);
                }
                else if (isDirectory && name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                         loaderBundleDetected && IsBundledDotnetRuntime(path))
                {
                    AddKnownTree(result, root, path,
                        "Bundled .NET runtime paired with recognized BepInEx/doorstop evidence.", cancellationToken);
                }
                else if (!isDirectory && name.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) &&
                         doorstopDetected)
                {
                    result.Findings.Add(CreateFinding(root, path,
                        CleanupClassification.KnownModComponent, true,
                        "Doorstop configuration explicitly references BepInEx."));
                }
                else if (!isDirectory && loaderBundleDetected &&
                         (name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Findings.Add(CreateFinding(root, path,
                        CleanupClassification.KnownModComponent, true,
                        "Known doorstop loader file paired with BepInEx evidence."));
                }
                else
                {
                    result.Findings.Add(CreateFinding(root, path, CleanupClassification.Unknown, false,
                        "No positive SkeldForge mod-component signature matched."));
                }

                progress?.Report(new OperationProgress("CleanupScan", index + 1, topLevel.Length, name));
            }

            await LogInfoAsync($"Cleanup scan completed for '{root}': {result.KnownCount} known, {result.UnknownCount} unknown.")
                .ConfigureAwait(false);
            return OperationResult<CleanupScanResult>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await LogErrorAsync("Vanilla cleanup scan failed.", ex).ConfigureAwait(false);
            return OperationResult<CleanupScanResult>.Fail("CleanupScanFailed", ex.Message);
        }
    }

    public async Task<OperationResult<CleanupReport>> CleanAsync(
        CleanupScanResult approvedScan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedScan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BackupManifest? backup = null;
        var deletionStarted = false;
        var report = new CleanupReport();
        try
        {
            var approvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedScan.GamePath));
            var approvedPaths = approvedScan.Findings
                .Where(x => x.SelectedForRemoval && IsKnown(x.Classification))
                .Select(x => NormalizeRelative(x.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (approvedPaths.Count == 0)
            {
                return FailWithValue("CleanupNothingSelected", string.Empty, report);
            }

            var currentResult = await ScanAsync(approvedRoot, null, cancellationToken).ConfigureAwait(false);
            if (!currentResult.Success || currentResult.Value is null)
            {
                return FailWithValue(currentResult.ErrorKey, currentResult.TechnicalMessage, report);
            }

            var current = currentResult.Value;
            var plannedFiles = current.Findings
                .Where(x => !x.IsDirectory && IsKnown(x.Classification) &&
                            approvedPaths.Contains(NormalizeRelative(x.RelativePath)))
                .ToArray();
            var plannedDirectories = current.Findings
                .Where(x => x.IsDirectory && IsKnown(x.Classification) &&
                            approvedPaths.Contains(NormalizeRelative(x.RelativePath)))
                .OrderByDescending(x => x.RelativePath.Length)
                .ToArray();

            report.Unknown.AddRange(current.Findings
                .Where(x => x.Classification == CleanupClassification.Unknown)
                .Select(x => x.RelativePath));
            if (plannedFiles.Length == 0)
            {
                report.Skipped.AddRange(plannedDirectories.Select(x => x.RelativePath));
                return FailWithValue("CleanupNothingSelected", "No positively identified files were selected.", report);
            }

            var backupItems = plannedFiles.Select(x => new BackupSourceItem
            {
                FullPath = x.FullPath,
                RelativePath = x.RelativePath,
                Classification = x.Classification.ToString()
            });
            var backupResult = await _backupManager.CreateBackupAsync(
                    approvedRoot,
                    backupItems,
                    BackupKind.VanillaCleanup,
                    $"Vanilla Cleanup {DateTime.Now:g}",
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!backupResult.Success || backupResult.Value is null)
            {
                report.Errors.Add(backupResult.TechnicalMessage);
                await LogErrorAsync("Cleanup stopped because the required backup failed.",
                    new IOException(backupResult.TechnicalMessage)).ConfigureAwait(false);
                return FailWithValue("CleanupBackupFailed", backupResult.TechnicalMessage, report);
            }

            backup = backupResult.Value;
            report.BackupId = backup.Id;
            report.BackupCreated = true;
            report.BackedUp.AddRange(backup.Files.Select(x => x.RelativePath));
            var backupByPath = backup.Files.ToDictionary(x => NormalizeRelative(x.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < plannedFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finding = plannedFiles[index];
                var relative = NormalizeRelative(finding.RelativePath);
                if (!backupByPath.TryGetValue(relative, out var entry))
                {
                    report.Errors.Add($"Backup manifest omitted '{relative}'.");
                    continue;
                }

                var target = ResolveContainedPath(approvedRoot, relative);
                if (!File.Exists(target))
                {
                    report.Skipped.Add(relative);
                    continue;
                }

                var currentHash = await _hashService.ComputeFileSha256Async(target, cancellationToken)
                    .ConfigureAwait(false);
                if (!currentHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    report.Errors.Add($"'{relative}' changed after backup and was not removed.");
                    continue;
                }

                deletionStarted = true;
                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
                report.Removed.Add(relative);
                progress?.Report(new OperationProgress("CleanupRemove", index + 1, plannedFiles.Length, relative));
            }

            foreach (var directory in plannedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveContainedPath(approvedRoot, directory.RelativePath);
                if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                {
                    Directory.Delete(target);
                    report.Removed.Add(NormalizeRelative(directory.RelativePath) + "/");
                }
                else if (Directory.Exists(target))
                {
                    report.Skipped.Add(directory.RelativePath);
                }
            }

            var finalResult = await ScanAsync(approvedRoot, null, cancellationToken).ConfigureAwait(false);
            report.VerificationCompleted = finalResult.Success;
            report.FinalScan = finalResult.Value;
            if (!finalResult.Success)
            {
                report.Errors.Add(finalResult.TechnicalMessage);
            }

            await LogInfoAsync($"Vanilla cleanup completed with {report.Removed.Count} removed files and {report.Errors.Count} errors.")
                .ConfigureAwait(false);
            return report.Errors.Count == 0
                ? OperationResult<CleanupReport>.Ok(report)
                : FailWithValue("CleanupPartialFailure", string.Join("; ", report.Errors), report);
        }
        catch (OperationCanceledException)
        {
            if (deletionStarted && backup is not null)
            {
                await TryRestoreAfterFailureAsync(backup).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report.Errors.Add(ex.Message);
            if (deletionStarted && backup is not null)
            {
                await TryRestoreAfterFailureAsync(backup).ConfigureAwait(false);
            }

            await LogErrorAsync("Vanilla cleanup failed.", ex).ConfigureAwait(false);
            return FailWithValue("CleanupFailed", ex.Message, report);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TryRestoreAfterFailureAsync(BackupManifest backup)
    {
        try
        {
            await _backupManager.RestoreAsync(backup, false, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogErrorAsync("Automatic cleanup rollback from backup failed.", ex).ConfigureAwait(false);
        }
    }

    private static bool IsRecognizedBepInExDirectory(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "core")) ||
                   Directory.Exists(Path.Combine(path, "plugins")) ||
                   Directory.Exists(Path.Combine(path, "patchers")) ||
                   Directory.EnumerateFiles(path, "BepInEx*.dll", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBepInExDoorstopConfig(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024 * 1024)
            {
                return false;
            }

            var text = File.ReadAllText(path);
            return text.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                   (text.Contains("targetAssembly", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("dllSearchPathOverride", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBundledDotnetRuntime(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "shared")) ||
                   Directory.Exists(Path.Combine(path, "host", "fxr")) ||
                   File.Exists(Path.Combine(path, "dotnet.exe"));
        }
        catch
        {
            return false;
        }
    }

    private static void AddKnownTree(
        CleanupScanResult result,
        string root,
        string treeRoot,
        string evidence,
        CancellationToken cancellationToken)
    {
        result.Findings.Add(CreateFinding(root, treeRoot,
            CleanupClassification.KnownModComponent, true, evidence));

        var pending = new Stack<string>();
        pending.Push(treeRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                {
                    result.Findings.Add(CreateFinding(root, directory, CleanupClassification.Unknown, false,
                        "Reparse-point directory was not followed."));
                    continue;
                }

                result.Findings.Add(CreateFinding(root, directory,
                    CleanupClassification.KnownModComponent, true, evidence));
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                var classification = IsPluginFile(treeRoot, file)
                    ? CleanupClassification.KnownModPlugin
                    : CleanupClassification.KnownModComponent;
                result.Findings.Add(CreateFinding(root, file, classification, true, evidence));
            }
        }
    }

    private static bool IsPluginFile(string treeRoot, string file)
    {
        if (!Path.GetFileName(treeRoot).Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(treeRoot, file).Replace('\\', '/');
        return relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) &&
               file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static CleanupFinding CreateFinding(
        string root,
        string path,
        CleanupClassification classification,
        bool selected,
        string evidence) => new()
    {
        FullPath = Path.GetFullPath(path),
        RelativePath = NormalizeRelative(Path.GetRelativePath(root, path)),
        Classification = classification,
        IsDirectory = Directory.Exists(path),
        SelectedForRemoval = selected,
        Evidence = evidence
    };

    private static bool IsKnown(CleanupClassification classification) =>
        classification is CleanupClassification.KnownModComponent or CleanupClassification.KnownModPlugin;

    private static string NormalizeRelative(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or "..") ||
            Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new InvalidDataException("Cleanup item contains an unsafe relative path.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(
            canonicalRoot, NormalizeRelative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cleanup path escaped the game directory.");
        }

        return target;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Vanilla cleanup does not follow reparse points.");
        }
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logger is not null)
        {
            try { await _logger.InfoAsync(message).ConfigureAwait(false); } catch { }
        }
    }

    private async Task LogErrorAsync(string message, Exception exception)
    {
        if (_logger is not null)
        {
            try { await _logger.ErrorAsync(message, exception).ConfigureAwait(false); } catch { }
        }
    }

    private static OperationResult<T> FailWithValue<T>(string key, string message, T value) => new()
    {
        Success = false,
        ErrorKey = key,
        TechnicalMessage = message,
        Value = value
    };
}
