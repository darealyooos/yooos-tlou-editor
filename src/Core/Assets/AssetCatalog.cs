using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;

namespace FactionsReborn.WorldEditor.Core.Assets;

/// <summary>String IDs as used by the engine: FNV-1a 64 over the raw name bytes.</summary>
public static class Sid
{
    public static ulong Fnv1a64(string text)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) { h ^= b; h *= 0x100000001B3UL; }
        return h;
    }

    public static string Hex(string text) => "0x" + Fnv1a64(text).ToString("x16");
}

/// <summary>Name-based browsing groups shared by the editor and the public catalogue.</summary>
public static class AssetGroups
{
    public const string Characters = "Characters", Weapons = "Weapons", Vehicles = "Vehicles";
    public static readonly string[] Order = [SceneCategories.Buildings, SceneCategories.Props, SceneCategories.Vegetation, Characters, Weapons, Vehicles, SceneCategories.Objects];

    private static readonly HashSet<string> CharacterHeads = new(StringComparer.Ordinal)
    {
        "abby", "ellie", "joel", "dina", "tommy", "jesse", "yara", "lev", "manny", "mel", "owen", "nora", "isaac", "jordan",
        "marlene", "militia", "scar", "clicker", "shambler", "bloater", "ratking", "horse", "dog", "corpse", "prisoner",
        "slaver", "rattler", "survivor", "npc", "gore", "lily", "emily", "maria", "seth", "eugene", "whitney", "leah",
    };
    private static readonly HashSet<string> WeaponHeads = new(StringComparer.Ordinal)
    {
        "rifle", "pistol", "shotgun", "bow", "crossbow", "revolver", "smg", "weapon", "knife", "machete", "ammo", "grenade", "molotov",
    };
    private static readonly HashSet<string> VehicleWords = new(StringComparer.Ordinal)
    {
        "car", "truck", "vehicle", "boat", "bike", "bicycle", "bus", "van", "jeep", "humvee", "trailer", "tractor", "motorcycle",
        "ambulance", "sedan", "suv", "forklift", "helicopter", "train", "tram", "rowboat", "sailboat",
    };

    /// <summary>Browsing group inferred from the name; falls back to the scene category.</summary>
    public static string Of(string name, string sceneCategory)
    {
        var tokens = name.ToLowerInvariant().Split(['-', '_', '=', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return sceneCategory;
        if (WeaponHeads.Contains(tokens[0]) || tokens.Contains("weapon")) return Weapons;
        if (tokens.Any(VehicleWords.Contains)) return Vehicles;
        if (CharacterHeads.Contains(tokens[0]) || tokens[0].StartsWith("runner", StringComparison.Ordinal) || tokens[0].StartsWith("stalker", StringComparison.Ordinal)) return Characters;
        return sceneCategory;
    }
}

public sealed class CatalogMesh
{
    public int Triangles { get; set; }
    public int Submeshes { get; set; }
    public int Prototypes { get; set; }
    /// <summary>Width (X), height (Y), depth (Z) in metres, LOD0.</summary>
    public float[] Dimensions { get; set; } = [];
    public string[] Textures { get; set; } = [];
    public bool Collision { get; set; }
}

public sealed class CatalogEntry
{
    public required string Name { get; set; }
    /// <summary>"actor" or "level".</summary>
    public required string Kind { get; set; }
    public required string Category { get; set; }
    public required string Archive { get; set; }
    public required string Path { get; set; }
    public long Size { get; set; }
    public required string Sid { get; set; }
    public CatalogMesh? Mesh { get; set; }
    /// <summary>True when the pak was decoded and contains no geometry (skeleton, animation, data).</summary>
    public bool NoMesh { get; set; }
    /// <summary>Relative thumbnail path, only when thumbnails were exported.</summary>
    public string? Thumbnail { get; set; }
    /// <summary>Relative path of a horizontal strip of <see cref="TurntableFrames"/> square views, only when exported.</summary>
    public string? Turntable { get; set; }
    public int TurntableFrames { get; set; }
}

public sealed class CatalogDocument
{
    public string Format { get; set; } = "yooos-asset-catalog";
    public int Version { get; set; } = 1;
    public required string Generated { get; set; }
    public required string Generator { get; set; }
    public Dictionary<string, int> Counts { get; set; } = [];
    public List<CatalogEntry> Assets { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };
}

/// <summary>
/// Builds the public asset catalogue from the user's own install: metadata only (names, archives, sizes, SIDs,
/// mesh statistics). No geometry, textures or other game content is written.
/// </summary>
public static class CatalogBuilder
{
    public static CatalogDocument Build(string buildDir, string generator, IProgress<string>? progress = null, CancellationToken cancel = default, int? parallelism = null)
    {
        var index = AssetIndex.Scan(buildDir, progress, cancel);
        var entries = new ConcurrentBag<CatalogEntry>();
        using var lib = new AssetLibrary(buildDir);
        int done = 0, actors = index.Count(e => e.Kind == AssetKind.Actor);

        foreach (var e in index.Where(e => e.Kind == AssetKind.Level))
            entries.Add(new CatalogEntry
            {
                Name = e.Name, Kind = "level", Category = "Level", Archive = e.Ref.Archive, Path = e.Ref.Path, Size = e.Size, Sid = Sid.Hex(e.Name),
            });

        Parallel.ForEach(index.Where(e => e.Kind == AssetKind.Actor),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancel },
            e =>
            {
                var entry = new CatalogEntry
                {
                    Name = e.Name, Kind = "actor", Category = AssetGroups.Of(e.Name, e.Category), Archive = e.Ref.Archive, Path = e.Ref.Path,
                    Size = e.Size, Sid = Sid.Hex(e.Name),
                };
                try { entry.Mesh = Describe(AssetLibrary.Decode(e.Ref, lib.ReadRaw(e.Ref))); }
                catch (InvalidDataException) { entry.NoMesh = true; }
                catch (Exception ex) when (ex is IOException or IndexOutOfRangeException or ArgumentException) { /* unreadable: metadata only */ }
                entries.Add(entry);
                int n = Interlocked.Increment(ref done);
                if (n % 250 == 0) progress?.Report($"Described {n}/{actors} actors…");
            });

        var doc = new CatalogDocument
        {
            Generated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Generator = generator,
            Assets = entries.OrderBy(a => a.Kind == "level" ? 1 : 0).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        doc.Counts["actors"] = doc.Assets.Count(a => a.Kind == "actor");
        doc.Counts["levels"] = doc.Assets.Count(a => a.Kind == "level");
        doc.Counts["withMesh"] = doc.Assets.Count(a => a.Mesh is not null);
        foreach (var g in doc.Assets.Where(a => a.Kind == "actor").GroupBy(a => a.Category)) doc.Counts["category:" + g.Key] = g.Count();
        return doc;
    }

    public static CatalogMesh Describe(LoadedPak pak)
    {
        var geo = pak.Geometry;
        int[] subs = GeometryReader.ActorLod0(geo);
        var usable = subs.Where(s => geo.Submeshes[s].Indices.Length > 0).ToArray();
        var dims = Array.Empty<float>();
        if (usable.Length > 0)
        {
            var min = usable.Select(s => geo.Submeshes[s].BoundsMin).Aggregate(System.Numerics.Vector3.Min);
            var max = usable.Select(s => geo.Submeshes[s].BoundsMax).Aggregate(System.Numerics.Vector3.Max);
            var size = max - min;
            dims = [Round(size.X), Round(size.Y), Round(size.Z)];
        }
        return new CatalogMesh
        {
            Triangles = usable.Sum(s => geo.Submeshes[s].Indices.Length / 3),
            Submeshes = usable.Length,
            Prototypes = geo.Prototypes.Count,
            Dimensions = dims,
            Textures = usable.Select(s => geo.Materials[geo.Submeshes[s].MaterialIndex].BaseColorTexture).OfType<string>()
                .Select(t => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(t)) ?? t).Distinct().ToArray(),
            Collision = pak.HasCollision,
        };
    }

    private static float Round(float v) => MathF.Round(v, 3);

    public static void Write(CatalogDocument doc, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) JsonSerializer.Serialize(fs, doc, CatalogDocument.Json);
        File.Move(tmp, path, overwrite: true);
    }
}
