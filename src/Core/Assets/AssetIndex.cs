using FactionsReborn.WorldEditor.Core.Archives;
using FactionsReborn.WorldEditor.Core.Scene;

namespace FactionsReborn.WorldEditor.Core.Assets;

public enum AssetKind { Actor, Level }

/// <summary>One pak found in the game's psarcs.</summary>
public sealed record AssetEntry(AssetRef Ref, AssetKind Kind, string Name, long Size, string Category);

/// <summary>Locates build/pc/main of the user's own TLOU Part II install.</summary>
public static class GameLocator
{
    public const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\The Last of Us Part II";

    public static string? FindBuildDir(string? configured = null)
    {
        foreach (var root in Candidates(configured))
        {
            var main = Path.Combine(root, "build", "pc", "main");
            if (File.Exists(Path.Combine(main, "common.psarc"))) return main;
            if (File.Exists(Path.Combine(root, "common.psarc"))) return root;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var env = Environment.GetEnvironmentVariable("TLOU2_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return DefaultSteamPath;
        // Other Steam libraries listed in libraryfolders.vdf.
        var vdf = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
        if (File.Exists(vdf))
            foreach (var line in File.ReadLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
                var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    yield return Path.Combine(parts[^1].Replace(@"\\", @"\"), "steamapps", "common", "The Last of Us Part II");
            }
    }
}

/// <summary>Scans every *.psarc in build/pc/main for actor97/*.pak (placeable actors) and pak68/*.pak (levels).</summary>
public static class AssetIndex
{
    private static readonly string[] SkipActorPrefixes = ["anim-", "cin-", "animstream", "audio-", "sfx-", "fx-", "tts-"];
    private static readonly string[] SkipLevelSuffixes = ["-ingame", "-phys", "-audio", "-audio-ingame", "-audio-phys"];

    public static List<AssetEntry> Scan(string buildDir, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var result = new List<AssetEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archives = Directory.GetFiles(buildDir, "*.psarc")
            .OrderBy(p => Path.GetFileName(p).StartsWith("world-", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var path in archives)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"Scanning {Path.GetFileName(path)}…");
            PsarcArchive archive;
            try { archive = new PsarcArchive(path); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { continue; }
            using (archive)
                foreach (var name in archive.Names)
                {
                    var entry = Classify(archive, name);
                    if (entry is not null && seen.Add(name)) result.Add(entry);
                }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static AssetEntry? Classify(PsarcArchive archive, string name)
    {
        if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Path.GetFileNameWithoutExtension(name);
        if (name.StartsWith("actor97/", StringComparison.OrdinalIgnoreCase))
        {
            if (SkipActorPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return null;
            return new AssetEntry(new AssetRef(archive.FileName, name), AssetKind.Actor, file, archive.GetLength(name), GuessCategory(file));
        }
        if (name.StartsWith("pak68/", StringComparison.OrdinalIgnoreCase))
        {
            if (SkipLevelSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return null;
            return new AssetEntry(new AssetRef(archive.FileName, name), AssetKind.Level, file, archive.GetLength(name), SceneCategories.Map);
        }
        return null;
    }

    private static readonly string[] VegetationWords = ["tree", "shrub", "grass", "plant", "fern", "bush", "foliage", "ivy", "weed", "vine", "moss", "flower", "leaf", "leaves", "vegetation", "branch", "log-"];
    private static readonly string[] BuildingWords = ["building", "bldg", "house", "wall", "door", "window", "roof", "stair", "facade", "fence", "gate", "garage", "shed", "barricade", "hesco", "pillar", "column", "bridge", "pier", "scaffold", "tower"];

    public static string GuessCategory(string pathOrName)
    {
        var s = pathOrName.ToLowerInvariant();
        if (VegetationWords.Any(s.Contains)) return SceneCategories.Vegetation;
        if (BuildingWords.Any(s.Contains)) return SceneCategories.Buildings;
        return SceneCategories.Props;
    }
}
