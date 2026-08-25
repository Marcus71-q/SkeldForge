using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Core;

/// <summary>
/// Performs read-only security and structure analysis before a package is imported or
/// installed. A safe but unusual package remains inspectable instead of being rejected.
/// </summary>
public sealed partial class ModPackageAnalyzer
{
    private readonly ZipService _zipService;
    private readonly DependencyDetector _dependencyDetector;
    private readonly HashService _hashService;

    public ModPackageAnalyzer(
        ZipService? zipService = null,
        DependencyDetector? dependencyDetector = null,
        HashService? hashService = null)
    {
        _zipService = zipService ?? new ZipService();
        _dependencyDetector = dependencyDetector ?? new DependencyDetector();
        _hashService = hashService ?? new HashService();
    }

    public async Task<ModPackageAnalysis> AnalyzeAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var result = new ModPackageAnalysis
        {
            ArchivePath = fullPath,
            ArchiveName = Path.GetFileName(fullPath),
            DetectedName = Path.GetFileNameWithoutExtension(fullPath),
            NameConfidence = PackageConfidence.Low,
            VersionConfidence = PackageConfidence.Unknown
        };

        if (!File.Exists(fullPath))
        {
            result.RejectionReason = "ArchiveNotFound";
            return result;
        }

        try
        {
            result.Sha256 = await _hashService.ComputeFileSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
            var rawEntries = _zipService.ValidateArchive(fullPath);
            result.ContentRootPrefix = FindContentRootPrefix(rawEntries);
            result.Entries = MapEntries(rawEntries, result.ContentRootPrefix);

            DetectStructure(result);
            ReadManifestMetadata(fullPath, result);
            ReadAssemblyMetadata(fullPath, result);
            ApplyFilenameFallback(result);
            result.DetectedDependencies = _dependencyDetector
                .DetectFromArchive(fullPath, result)
                .Select(CloneDependency)
                .ToList();
            result.IsSafe = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            result.RejectionReason = $"ArchiveNotFound: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            result.RejectionReason = $"ArchiveAccessDenied: {ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            result.RejectionReason = $"UnsafeOrInvalidArchive: {ex.Message}";
        }
        catch (IOException ex)
        {
            result.RejectionReason = $"ArchiveReadFailed: {ex.Message}";
        }

        return result;
    }

    private static List<PackageEntryInfo> MapEntries(
        IReadOnlyList<PackageEntryInfo> entries,
        string prefix)
    {
        if (prefix.Length == 0)
        {
            return entries.Select(CloneEntry).ToList();
        }

        return entries
            .Where(x => !x.RelativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => new PackageEntryInfo
            {
                RelativePath = x.RelativePath[prefix.Length..],
                CompressedSize = x.CompressedSize,
                UncompressedSize = x.UncompressedSize,
                IsDirectory = x.IsDirectory
            })
            .Where(x => x.RelativePath.Length > 0)
            .ToList();
    }

    private static string FindContentRootPrefix(IReadOnlyList<PackageEntryInfo> entries)
    {
        var files = entries.Where(x => !x.IsDirectory).Select(x => x.RelativePath).ToArray();
        if (files.Length == 0)
        {
            return string.Empty;
        }

        var prefix = new List<string>();
        while (files.All(x => x.Contains('/')))
        {
            var firstSegments = files
                .Select(x => x.Split('/', 2)[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (firstSegments.Length != 1 || IsGameRootDirectory(firstSegments[0]))
            {
                break;
            }

            prefix.Add(firstSegments[0]);
            files = files.Select(x => x[(x.IndexOf('/') + 1)..]).ToArray();
        }

        return prefix.Count == 0 ? string.Empty : string.Join('/', prefix) + "/";
    }

    private static bool IsGameRootDirectory(string segment) =>
        segment.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("Among Us_Data", StringComparison.OrdinalIgnoreCase);

    private static void DetectStructure(ModPackageAnalysis result)
    {
        var files = result.Entries.Where(x => !x.IsDirectory).Select(x => x.RelativePath).ToArray();
        result.HasBepInExStructure = result.Entries.Any(x =>
            x.RelativePath.Equals("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
            x.RelativePath.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase));

        result.PluginFiles = files.Where(x =>
                x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                (x.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) || !x.Contains('/')))
            .ToList();
        result.HasPluginDlls = result.PluginFiles.Count > 0;
        result.HasConfigurationFiles = files.Any(x =>
            x.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase) &&
            x.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        result.HasLoaderComponents = files.Any(x =>
            x.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
            x.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadManifestMetadata(string archivePath, ModPackageAnalysis result)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var candidates = archive.Entries
            .Where(x => x.Length is > 0 and <= 1024 * 1024 &&
                        (x.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                         x.Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase) ||
                         x.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => ManifestPriority(x.Name))
            .ThenBy(x => x.FullName.Count(c => c is '/' or '\\'))
            .ToArray();

        foreach (var entry in candidates)
        {
            try
            {
                using var stream = entry.Open();
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(document.RootElement, "name", "modName", "displayName", "title");
                var version = ReadString(document.RootElement, "version", "modVersion", "packageVersion");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.DetectedName = SanitizeDisplayValue(name);
                    result.NameConfidence = PackageConfidence.High;
                }

                if (!string.IsNullOrWhiteSpace(version))
                {
                    result.DetectedVersion = SanitizeDisplayValue(version);
                    result.VersionConfidence = PackageConfidence.High;
                }

                if (result.NameConfidence == PackageConfidence.High &&
                    result.VersionConfidence == PackageConfidence.High)
                {
                    return;
                }
            }
            catch (JsonException)
            {
                // A non-SkeldForge manifest may be invalid; it is metadata, not a safety bypass.
            }
            catch (IOException)
            {
                // The central archive validation already supplied the safety result.
            }
        }
    }

    private static void ReadAssemblyMetadata(string archivePath, ModPackageAnalysis result)
    {
        if (result.NameConfidence == PackageConfidence.High &&
            result.VersionConfidence == PackageConfidence.High)
        {
            return;
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var pluginNames = result.PluginFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = archive.Entries
            .Where(x => x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => pluginNames.Contains(x.Name))
            .ThenBy(x => x.FullName.Length)
            .Take(32);

        foreach (var entry in candidates)
        {
            if (entry.Length is <= 0 or > 64L * 1024 * 1024)
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                using var image = new MemoryStream(checked((int)entry.Length));
                stream.CopyTo(image);
                image.Position = 0;
                using var pe = new PEReader(image);
                if (!pe.HasMetadata)
                {
                    continue;
                }

                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly)
                {
                    continue;
                }

                var definition = reader.GetAssemblyDefinition();
                var assemblyName = reader.GetString(definition.Name);
                if (IsFrameworkAssembly(assemblyName))
                {
                    continue;
                }

                if (result.NameConfidence != PackageConfidence.High && !string.IsNullOrWhiteSpace(assemblyName))
                {
                    result.DetectedName = assemblyName;
                    result.NameConfidence = PackageConfidence.Medium;
                }

                if (result.VersionConfidence != PackageConfidence.High && definition.Version != new Version(0, 0, 0, 0))
                {
                    result.DetectedVersion = definition.Version.ToString();
                    result.VersionConfidence = PackageConfidence.Medium;
                }

                return;
            }
            catch (BadImageFormatException)
            {
                // Native plugin.
            }
            catch (InvalidDataException)
            {
                // Corrupt managed metadata does not affect path-safety analysis.
            }
            catch (IOException)
            {
                // Keep lower-confidence metadata.
            }
        }
    }

    private static void ApplyFilenameFallback(ModPackageAnalysis result)
    {
        var filename = Path.GetFileNameWithoutExtension(result.ArchiveName).Trim();
        var match = VersionInFilenameRegex().Match(filename);
        if (result.VersionConfidence == PackageConfidence.Unknown && match.Success)
        {
            result.DetectedVersion = match.Groups["version"].Value.Replace('_', '.');
            result.VersionConfidence = PackageConfidence.Low;
        }

        if (result.NameConfidence == PackageConfidence.Low)
        {
            var name = match.Success ? filename[..match.Index] : filename;
            name = SeparatorRegex().Replace(name, " ").Trim(' ', '-', '_', '.');
            result.DetectedName = string.IsNullOrWhiteSpace(name) ? filename : name;
        }

        if (string.IsNullOrWhiteSpace(result.DetectedName))
        {
            result.DetectedName = "Unknown Package";
            result.NameConfidence = PackageConfidence.Unknown;
        }
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in propertyNames)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            foreach (var candidate in element.EnumerateObject())
            {
                if (candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Value.ValueKind == JsonValueKind.String)
                {
                    return candidate.Value.GetString();
                }
            }
        }

        return null;
    }

    private static int ManifestPriority(string name) => name.ToLowerInvariant() switch
    {
        "manifest.json" => 0,
        "mod.json" => 1,
        _ => 2
    };

    private static string SanitizeDisplayValue(string value)
    {
        var sanitized = new string(value.Where(x => !char.IsControl(x)).ToArray()).Trim();
        return sanitized.Length <= 200 ? sanitized : sanitized[..200];
    }

    private static bool IsFrameworkAssembly(string name) =>
        name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("BepInEx.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Reactor", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Reactor.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MiraAPI", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MiraAPI.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);

    private static PackageEntryInfo CloneEntry(PackageEntryInfo value) => new()
    {
        RelativePath = value.RelativePath,
        CompressedSize = value.CompressedSize,
        UncompressedSize = value.UncompressedSize,
        IsDirectory = value.IsDirectory
    };

    private static DependencyInfo CloneDependency(DependencyInfo value) => new()
    {
        Name = value.Name,
        Status = value.Status,
        Evidence = value.Evidence,
        Confidence = value.Confidence
    };

    [GeneratedRegex(@"(?<!\d)(?<version>v?\d+(?:[._-]\d+){1,3}(?:[-_. ]?(?:alpha|beta|rc|pre|preview)\s*\d*)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionInFilenameRegex();

    [GeneratedRegex(@"[-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();
}
