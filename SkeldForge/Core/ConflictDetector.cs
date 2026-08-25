using SkeldForge.Models;

namespace SkeldForge.Core;

/// <summary>
/// Compares normalized, case-insensitive Windows target paths. Identical content may be
/// shared by multiple owners; different content is always a blocking conflict.
/// </summary>
public sealed class ConflictDetector
{
    public IReadOnlyList<ConflictInfo> Detect(IEnumerable<ModInfo> installedMods, ModInfo incomingMod)
    {
        ArgumentNullException.ThrowIfNull(installedMods);
        ArgumentNullException.ThrowIfNull(incomingMod);

        var conflicts = new List<ConflictInfo>();
        var existingOwners = installedMods
            .Where(x => x.IsEnabled && !x.Id.Equals(incomingMod.Id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(mod => mod.InstalledFiles
                .Where(file => file.IsCurrentlyApplied)
                .Select(file => (Mod: mod, File: file)))
            .GroupBy(x => Normalize(x.File.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var incomingFile in incomingMod.InstalledFiles)
        {
            var path = Normalize(incomingFile.RelativePath);
            if (!existingOwners.TryGetValue(path, out var owners))
            {
                continue;
            }

            foreach (var owner in owners)
            {
                conflicts.Add(new ConflictInfo
                {
                    RelativePath = path,
                    ExistingModId = owner.Mod.Id,
                    ExistingModName = owner.Mod.Name,
                    IncomingModId = incomingMod.Id,
                    IncomingModName = incomingMod.Name,
                    ExistingHash = owner.File.Sha256,
                    IncomingHash = incomingFile.Sha256,
                    HashesMatch = HashesMatch(owner.File.Sha256, incomingFile.Sha256)
                });
            }
        }

        return conflicts
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ExistingModName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ConflictInfo> Detect(ModpackInfo modpack, ModInfo incomingMod)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        return Detect(modpack.Mods, incomingMod);
    }

    public bool HasBlockingConflicts(IEnumerable<ConflictInfo> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        return conflicts.Any(x => !x.HashesMatch);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<ModInfo>> GetOwnership(ModpackInfo modpack)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        return modpack.Mods
            .SelectMany(mod => mod.InstalledFiles.Select(file => (Mod: mod, File: file)))
            .GroupBy(x => Normalize(x.File.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<ModInfo>)x.Select(y => y.Mod).DistinctBy(y => y.Id).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var value = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or ".."))
        {
            throw new ArgumentException("Tracked file path is unsafe.", nameof(relativePath));
        }

        return string.Join('/', segments);
    }

    private static bool HashesMatch(string first, string second) =>
        first.Length == 64 && second.Length == 64 &&
        first.Equals(second, StringComparison.OrdinalIgnoreCase);
}
