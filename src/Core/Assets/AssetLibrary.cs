using System.Collections.Concurrent;
using FactionsReborn.WorldEditor.Core.Archives;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;

namespace FactionsReborn.WorldEditor.Core.Assets;

/// <summary>A loaded pak: decoded geometry and the textures its materials reference.</summary>
public sealed class LoadedPak
{
    public required AssetRef Ref { get; init; }
    public required GeometryData Geometry { get; init; }
    public required IReadOnlyDictionary<string, TextureImage> Textures { get; init; }
    public bool HasCollision { get; init; }
}

/// <summary>Opens psarcs lazily and caches decoded paks. Thread-safe.</summary>
public sealed class AssetLibrary : IDisposable
{
    private readonly ConcurrentDictionary<string, PsarcArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<LoadedPak>> _paks = new(StringComparer.OrdinalIgnoreCase);

    public string BuildDir { get; }

    public AssetLibrary(string buildDir) => BuildDir = buildDir;

    public byte[] ReadRaw(AssetRef asset)
    {
        var archive = _archives.GetOrAdd(asset.Archive, a => new PsarcArchive(Path.Combine(BuildDir, a)));
        return archive.ReadFile(asset.Path);
    }

    public LoadedPak Load(AssetRef asset) =>
        _paks.GetOrAdd(asset.Key, _ => new Lazy<LoadedPak>(() => Decode(asset), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>The decoded pak if it is already cached (and finished decoding), without loading it.</summary>
    public LoadedPak? TryGetLoaded(AssetRef asset) =>
        _paks.TryGetValue(asset.Key, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;

    public int CachedCount => _paks.Count;

    /// <summary>Releases decoded paks whose key is not in <paramref name="keep"/>. Returns how many were dropped.</summary>
    public int Trim(ISet<string> keep)
    {
        int removed = 0;
        foreach (var key in _paks.Keys)
            if (!keep.Contains(key) && _paks.TryRemove(key, out _)) removed++;
        return removed;
    }

    public static LoadedPak Decode(AssetRef asset, byte[] data)
    {
        var pak = new PakFile(data);
        if (!GeometryReader.HasGeometry(pak))
            throw new InvalidDataException($"{asset.Path} has no GEOMETRY_1 (not a mesh pak).");
        var geometry = GeometryReader.Read(pak);
        var wanted = geometry.Materials.Select(m => m.BaseColorTexture).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var textures = new Dictionary<string, TextureImage>(StringComparer.Ordinal);
        foreach (var desc in TextureReader.List(pak))
        {
            if (!wanted.Contains(desc.Name) || textures.ContainsKey(desc.Name)) continue;
            var image = TextureReader.Decode(pak, desc);
            if (image is not null) textures[desc.Name] = image;
        }
        return new LoadedPak
        {
            Ref = asset,
            Geometry = geometry,
            Textures = textures,
            HasCollision = pak.Find("COLLISION_DATA_FOREGROUND") is not null || pak.Find("COLLISION_DATA_BACKGROUND") is not null,
        };
    }

    private LoadedPak Decode(AssetRef asset) => Decode(asset, ReadRaw(asset));

    public void Dispose()
    {
        foreach (var a in _archives.Values) a.Dispose();
        _archives.Clear();
    }
}
