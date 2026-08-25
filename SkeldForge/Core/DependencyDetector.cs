using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SkeldForge.Models;

namespace SkeldForge.Core;

/// <summary>
/// Detects dependencies only from package layout and managed assembly metadata. It never
/// assumes that every Among Us mod requires Reactor or MiraAPI.
/// </summary>
public sealed class DependencyDetector
{
    private static readonly string[] KnownDependencies = ["BepInEx", "Reactor", "MiraAPI"];

    public IReadOnlyList<DependencyInfo> DetectFromArchive(
        string archivePath,
        ModPackageAnalysis? analysis = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries.Where(x =>
                         x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && x.Length > 0))
            {
                TryReadAssemblyEvidence(entry, provided, referenced);
                AddFilenameProvider(entry.Name, provided);
            }
        }

        var mappedPaths = analysis?.Entries.Select(x => x.RelativePath).ToArray() ?? [];
        var bepinexLayoutEvidence = mappedPaths.Any(IsBepInExPluginPath);
        var dependencies = new List<DependencyInfo>();

        foreach (var dependency in KnownDependencies)
        {
            var isProvided = provided.Contains(dependency);
            var isReferenced = referenced.Contains(dependency);
            if (dependency == "BepInEx" && bepinexLayoutEvidence)
            {
                isReferenced = true;
            }

            if (!isProvided && !isReferenced)
            {
                continue;
            }

            dependencies.Add(new DependencyInfo
            {
                Name = dependency,
                Status = isProvided ? DependencyStatus.Installed : DependencyStatus.Unknown,
                Confidence = isReferenced || isProvided ? PackageConfidence.High : PackageConfidence.Unknown,
                Evidence = BuildEvidence(dependency, isProvided, referenced.Contains(dependency),
                    dependency == "BepInEx" && bepinexLayoutEvidence)
            });
        }

        return dependencies;
    }

    /// <summary>
    /// Evaluates previously detected requirements against files currently provided by
    /// enabled mods and known loader locations in the isolated game directory.
    /// </summary>
    public IReadOnlyList<DependencyInfo> Evaluate(
        ModInfo mod,
        ModpackInfo modpack,
        string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var requirements = mod.Dependencies.Select(Clone).ToList();
        if (requirements.All(x => !x.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) &&
            mod.InstalledFiles.Any(x => IsBepInExPluginPath(x.RelativePath)))
        {
            requirements.Add(new DependencyInfo
            {
                Name = "BepInEx",
                Status = DependencyStatus.Unknown,
                Confidence = PackageConfidence.High,
                Evidence = "Installed file is mapped under BepInEx/plugins."
            });
        }

        var provided = CollectInstalledProviders(modpack, gameDirectory);
        foreach (var requirement in requirements)
        {
            var canonical = CanonicalDependency(requirement.Name);
            if (canonical is null)
            {
                requirement.Status = DependencyStatus.Unknown;
                continue;
            }

            if (provided.TryGetValue(canonical, out var evidence))
            {
                requirement.Status = DependencyStatus.Installed;
                requirement.Evidence = string.IsNullOrWhiteSpace(requirement.Evidence)
                    ? evidence
                    : $"{requirement.Evidence} {evidence}";
            }
            else if (requirement.Status != DependencyStatus.NotRequired)
            {
                requirement.Status = DependencyStatus.Missing;
            }
        }

        return requirements;
    }

    public IReadOnlyList<DependencyInfo> DetectRequirements(string archivePath, ModPackageAnalysis? analysis = null) =>
        DetectFromArchive(archivePath, analysis);

    private static Dictionary<string, string> CollectInstalledProviders(ModpackInfo modpack, string gameDirectory)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enabledFiles = modpack.Mods
            .Where(x => x.IsEnabled)
            .SelectMany(x => x.InstalledFiles)
            .Where(x => x.IsCurrentlyApplied)
            .Select(x => x.RelativePath)
            .ToArray();

        if (File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.Core.dll")) ||
            File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.dll")) ||
            enabledFiles.Any(IsBepInExCorePath))
        {
            providers["BepInEx"] = "BepInEx core is present in the isolated instance.";
        }

        foreach (var relativePath in enabledFiles)
        {
            var dependency = CanonicalDependency(Path.GetFileNameWithoutExtension(relativePath));
            if (dependency is not null && dependency != "BepInEx")
            {
                providers[dependency] = $"Enabled mod file provides {Path.GetFileName(relativePath)}.";
            }
        }

        InspectProviderCandidates(gameDirectory, "Reactor", providers);
        InspectProviderCandidates(gameDirectory, "MiraAPI", providers);
        return providers;
    }

    private static void InspectProviderCandidates(
        string gameDirectory,
        string dependency,
        IDictionary<string, string> providers)
    {
        var pluginDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins");
        if (!Directory.Exists(pluginDirectory))
        {
            return;
        }

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var file in candidates)
        {
            if (CanonicalDependency(Path.GetFileNameWithoutExtension(file)) == dependency ||
                TryGetAssemblyIdentity(file, out var identity) && CanonicalDependency(identity) == dependency)
            {
                providers[dependency] = $"{dependency} assembly is present in BepInEx/plugins.";
                return;
            }
        }
    }

    private static void TryReadAssemblyEvidence(
        ZipArchiveEntry entry,
        ISet<string> provided,
        ISet<string> referenced)
    {
        const long maximumMetadataCandidateBytes = 64L * 1024 * 1024;
        if (entry.Length is <= 0 or > maximumMetadataCandidateBytes)
        {
            return;
        }

        try
        {
            using var stream = entry.Open();
            using var image = new MemoryStream(checked((int)entry.Length));
            stream.CopyTo(image);
            image.Position = 0;
            using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                return;
            }

            var reader = pe.GetMetadataReader();
            if (reader.IsAssembly)
            {
                var definition = reader.GetAssemblyDefinition();
                AddCanonical(reader.GetString(definition.Name), provided);
            }

            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                AddCanonical(reader.GetString(reference.Name), referenced);
            }
        }
        catch (BadImageFormatException)
        {
            // Native and malformed DLLs provide no managed-reference evidence.
        }
        catch (InvalidDataException)
        {
            // The analyzer/ZipService reports archive integrity separately.
        }
        catch (IOException)
        {
            // A locked/corrupt entry results in unknown dependency evidence.
        }
    }

    private static void AddFilenameProvider(string fileName, ISet<string> provided)
    {
        var canonical = CanonicalDependency(Path.GetFileNameWithoutExtension(fileName));
        if (canonical is not null)
        {
            provided.Add(canonical);
        }
    }

    private static void AddCanonical(string assemblyName, ISet<string> values)
    {
        var canonical = CanonicalDependency(assemblyName);
        if (canonical is not null)
        {
            values.Add(canonical);
        }
    }

    private static string? CanonicalDependency(string name)
    {
        if (name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("BepInEx.", StringComparison.OrdinalIgnoreCase))
        {
            return "BepInEx";
        }

        if (name.Equals("Reactor", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Reactor.", StringComparison.OrdinalIgnoreCase))
        {
            return "Reactor";
        }

        if (name.Equals("MiraAPI", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("MiraAPI.", StringComparison.OrdinalIgnoreCase))
        {
            return "MiraAPI";
        }

        return null;
    }

    private static bool IsBepInExPluginPath(string path)
    {
        var value = path.Replace('\\', '/');
        return value.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) &&
               value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBepInExCorePath(string path)
    {
        var value = path.Replace('\\', '/');
        return value.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) &&
               (value.EndsWith("BepInEx.Core.dll", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("BepInEx.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildEvidence(string name, bool provided, bool referenced, bool layout)
    {
        var evidence = new List<string>();
        if (provided)
        {
            evidence.Add($"Package provides a {name} assembly.");
        }

        if (referenced)
        {
            evidence.Add($"Managed assembly metadata references {name}.");
        }

        if (layout)
        {
            evidence.Add("Package installs a DLL under BepInEx/plugins.");
        }

        return string.Join(' ', evidence);
    }

    private static bool TryGetAssemblyIdentity(string path, out string name)
    {
        name = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return false;
            }

            name = reader.GetString(reader.GetAssemblyDefinition().Name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DependencyInfo Clone(DependencyInfo value) => new()
    {
        Name = value.Name,
        Status = value.Status,
        Evidence = value.Evidence,
        Confidence = value.Confidence
    };
}
