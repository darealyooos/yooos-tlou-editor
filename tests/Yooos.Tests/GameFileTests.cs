using System.Numerics;
using Xunit.Abstractions;
using N3 = System.Numerics.Vector3;
using FactionsReborn.WorldEditor.Core.Archives;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;

namespace WorldEditor.Tests;

/// <summary>
/// Read-only tests against the user's own TLOU Part II install. Expected values were measured with the
/// Python probes on 2026-11-14. Each test returns early (and logs) when the game is not installed.
/// </summary>
[Collection("Shared chunk cache")]
public class GameFileTests
{
    private const string World = "world-seattle-arrival.psarc";
    private static readonly string? BuildDir = GameLocator.FindBuildDir();
    private readonly ITestOutputHelper _out;

    public GameFileTests(ITestOutputHelper output) => _out = output;

    private bool Missing()
    {
        if (BuildDir is not null && File.Exists(Path.Combine(BuildDir, World))) return false;
        _out.WriteLine("TLOU Part II not found: game-file test not run.");
        return true;
    }

    [Fact]
    public void Psarc_ListsAndExtractsActorPak()
    {
        if (Missing()) return;
        using var archive = new PsarcArchive(Path.Combine(BuildDir!, World));
        Assert.Contains("actor97/bucket-rusty-a.pak", archive.Names);
        Assert.Contains("pak68/sea-hub-hotel-street.pak", archive.Names);
        var data = archive.ReadFile("actor97/bucket-rusty-a.pak");
        Assert.Equal(1124048, data.Length);
        Assert.Equal(PakFile.Magic, BitConverter.ToUInt32(data, 0));
    }

    [Fact]
    public void Actor_BucketDecodesRealMeshAndTexture()
    {
        if (Missing()) return;
        using var lib = new AssetLibrary(BuildDir!);
        var bucket = lib.Load(new AssetRef(World, "actor97/bucket-rusty-a.pak"));
        var geo = bucket.Geometry;
        Assert.Equal(9, geo.Submeshes.Count);
        Assert.Equal(3, geo.Prototypes.Count);
        Assert.Empty(geo.Instances);

        var lod0 = GeometryReader.ActorLod0(geo);
        Assert.Equal(3, lod0.Length); // bucket, handle, water
        Assert.All(lod0, i => Assert.Contains("LOD0", geo.Submeshes[i].Name));

        var body = geo.Submeshes[0];
        Assert.Equal(1215, body.Positions.Length);
        Assert.Equal(5016, body.Indices.Length);
        Assert.All(body.Positions, p =>
        {
            Assert.InRange(p.X, body.BoundsMin.X - 0.01f, body.BoundsMax.X + 0.01f);
            Assert.InRange(p.Y, body.BoundsMin.Y - 0.01f, body.BoundsMax.Y + 0.01f);
            Assert.InRange(p.Z, body.BoundsMin.Z - 0.01f, body.BoundsMax.Z + 0.01f);
        });
        Assert.NotNull(body.TexCoords);

        var material = geo.Materials[body.MaterialIndex];
        Assert.Contains("bucket-rusty-a-color.tga", material.BaseColorTexture);
        var tex = bucket.Textures[material.BaseColorTexture!];
        Assert.Equal(64, tex.Width);
        Assert.True(tex.Bgra.Chunk(4).Select(p => p[2]).Distinct().Count() > 16, "colour texture should not be flat");
        Assert.True(bucket.HasCollision);
    }

    [Fact]
    public void Level_HotelStreetHasPrototypesAndPlacedInstances()
    {
        if (Missing()) return;
        using var lib = new AssetLibrary(BuildDir!);
        var level = lib.Load(new AssetRef(World, "pak68/sea-hub-hotel-street.pak"));
        var geo = level.Geometry;
        Assert.Equal(0x4C1, geo.Submeshes.Count);
        Assert.Equal(0x142, geo.Prototypes.Count);
        Assert.Equal(0x73D, geo.Instances.Count);
        var first = geo.Instances[0];
        Assert.Equal(new Vector3(-79.617f, 34.338f, -849.131f), first.Matrix.Translation, new Vec3Tolerance(0.01f));
        Assert.Contains(geo.Prototypes, p => p.Name.EndsWith("mil-hesco-2x2m-b", StringComparison.Ordinal));
        // Prototype bounds enclose their LOD0 submesh bounds (regression: bounds are at +0x10/+0x20).
        Assert.All(geo.Prototypes.Where(p => p.Lod0Submeshes.Length > 0), p =>
        {
            foreach (var s in p.Lod0Submeshes)
            {
                Assert.True(geo.Submeshes[s].BoundsMin.X >= p.BoundsMin.X - 0.05f && geo.Submeshes[s].BoundsMax.Y <= p.BoundsMax.Y + 0.05f, p.Name);
            }
        });
        // The first instance's world position lies inside its prototype's bounds moved by the instance matrix.
        var proto0 = geo.Prototypes[first.Prototype];
        Assert.True(proto0.BoundsMax.X - proto0.BoundsMin.X > 4.5f, "plank prototype is ~5 m long");
        // A downward ray onto the first instance hits it.
        var top = N3.Transform((proto0.BoundsMin + proto0.BoundsMax) / 2, first.Matrix) + new Vector3(0, 5, 0);
        var hit = Raycast.Instances(new Ray(top, -Vector3.UnitY), geo, geo.Instances.Select(i => (i.Prototype, i.Matrix)));
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value, 4.5f, 5.5f);
        Assert.True(level.Textures.Count > 50, $"only {level.Textures.Count} base-colour textures decoded");
        _out.WriteLine($"textures decoded: {level.Textures.Count}; triangles LOD0: " +
            geo.Instances.Sum(i => geo.Prototypes[i.Prototype].Lod0Submeshes.Sum(s => geo.Submeshes[s].Indices.Length / 3)));
    }

    [Fact]
    public void Index_FindsActorsAndLevelsInOneWorldArchive()
    {
        if (Missing()) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var all = AssetIndex.Scan(BuildDir!);
        Assert.Contains(all, e => e.Kind == AssetKind.Actor && e.Name == "bucket-rusty-a");
        Assert.Contains(all, e => e.Kind == AssetKind.Level && e.Name == "sea-hub-hotel-street");
        Assert.DoesNotContain(all, e => e.Name.StartsWith("anim-", StringComparison.Ordinal));
        _out.WriteLine($"actors {all.Count(e => e.Kind == AssetKind.Actor)}, levels {all.Count(e => e.Kind == AssetKind.Level)}, {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("pak68/sea-hub-hotel-street.pak")]
    [InlineData("pak68/sea-hub-hotel-street-low.pak")]
    [InlineData("pak68/part-level-sea-hub-hotel-street.pak")]
    public void LevelBounds_AreFiniteAndCityScale(string path)
    {
        if (Missing()) return;
        using var lib = new AssetLibrary(BuildDir!);
        var geo = lib.Load(new AssetRef(World, path)).Geometry;
        int absurd = geo.Prototypes.Count(p => MathF.Abs(p.BoundsMin.X) > 1e6f || MathF.Abs(p.BoundsMax.X) > 1e6f);
        var (min, max) = LevelBounds.Compute(geo, geo.Instances.Select(i => (i.Prototype, i.Matrix)));
        var size = max - min;
        _out.WriteLine($"{path}: {geo.Prototypes.Count} prototypes ({absurd} with absurd header bounds), bounds {min} .. {max}, size {size}");
        Assert.True(float.IsFinite(size.X) && float.IsFinite(size.Y) && float.IsFinite(size.Z));
        Assert.True(size.X < 5000 && size.Y < 5000 && size.Z < 5000, $"bounds too large: {size}");
    }

    [Theory]
    [InlineData("abby-80s-body")]
    [InlineData("abby-arm")]
    public void SkinnedCharacter_FloatStreams_AreDecoded(string name)
    {
        // Character meshes store positions as float32 and UVs as float16 (stream kind without the packed bit).
        if (BuildDir is null) { _out.WriteLine("TLOU Part II not found: game-file test not run."); return; }
        var entry = AssetIndex.Scan(BuildDir).First(e => e.Name == name);
        using var lib = new AssetLibrary(BuildDir);
        var geo = lib.Load(entry.Ref).Geometry;
        foreach (int s in GeometryReader.ActorLod0(geo))
        {
            var m = geo.Submeshes[s];
            if (m.Indices.Length == 0) continue;
            Assert.Contains(m.Positions, p => p != N3.Zero);
            Assert.All(m.Positions, p => Assert.True(
                p.X >= m.BoundsMin.X - 0.01f && p.X <= m.BoundsMax.X + 0.01f &&
                p.Y >= m.BoundsMin.Y - 0.01f && p.Y <= m.BoundsMax.Y + 0.01f &&
                p.Z >= m.BoundsMin.Z - 0.01f && p.Z <= m.BoundsMax.Z + 0.01f, $"{p} outside {m.BoundsMin}..{m.BoundsMax}"));
            Assert.NotNull(m.TexCoords);
        }
    }

    private sealed class Vec3Tolerance(float eps) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
