using System.Numerics;
using FactionsReborn.WorldEditor.Core.Archives;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;
using FactionsReborn.WorldEditor.Core.Textures;

namespace WorldEditor.Tests;

/// <summary>Pure format tests (no game files needed).</summary>
public class FormatTests
{
    [Fact]
    public void Bits_ReadsLsbFirstFieldsAcrossBytes()
    {
        // 11/11/11-bit packing as used by position streams: x=0x7FF, y=0x001, z=0x400
        ulong v = 0x7FFUL | (0x001UL << 11) | (0x400UL << 22);
        var data = BitConverter.GetBytes(v);
        Assert.Equal(0x7FFu, GeometryReader.Bits(data, 0, 0, 11));
        Assert.Equal(0x001u, GeometryReader.Bits(data, 0, 11, 11));
        Assert.Equal(0x400u, GeometryReader.Bits(data, 0, 22, 11));
    }

    [Fact]
    public void Bc1_DecodesEndpointsAndInterpolants()
    {
        // c0 = pure red (0xF800), c1 = pure blue (0x001F); indices 0,1,2,3 in the first row.
        var block = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0b11100100, 0, 0, 0 };
        var src = new byte[256]; block.CopyTo(src, 0);
        var bgra = BlockDecoder.Decode(71, src, 4, 4)!;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, bgra[0..4]);    // red
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, bgra[4..8]);    // blue
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, bgra[8..12]);  // 2/3 red + 1/3 blue
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, bgra[12..16]);
    }

    [Fact]
    public void Bc4_SixValueModeHasZeroAndOne()
    {
        var output = new byte[16];
        var block = new byte[] { 10, 200, 0b_111_110, 0, 0, 0, 0, 0 }; // pixel0 index 6 (0), pixel1 index 7 (255)
        BlockDecoder.AlphaBlock(block, output);
        Assert.Equal(0, output[0]);
        Assert.Equal(255, output[1]);
    }

    [Fact]
    public void Lz4_DecodesOverlappingMatch()
    {
        // literal "ab", then match offset 2 length 6 -> "abababab"
        var src = new byte[] { 0x22, (byte)'a', (byte)'b', 0x02, 0x00 };
        Assert.Equal("abababab", System.Text.Encoding.ASCII.GetString(Lz4.DecodeBlock(src, 8)));
    }

    [Fact]
    public void Scene_RoundTripsThroughJson()
    {
        var doc = new SceneDocument { Name = "Test arena" };
        doc.Maps.Add(new AssetRef("world-seattle-arrival.psarc", "pak68/sea-hub-hotel-street.pak"));
        doc.Objects.Add(new SceneObject
        {
            Name = "hesco", Category = SceneCategories.Buildings,
            Asset = new AssetRef("world-seattle-arrival.psarc", "pak68/sea-hub-hotel-street.pak"),
            Prototype = 100, PrototypeName = "art/shared/background/asset-library/prototypes/barricades/military/mil-hesco-2x2m-b",
            Position = new Vector3(1, 2, 3), RotationDegrees = new Vector3(0, 90, 0), Scale = new Vector3(1, 1, 2),
        });
        doc.Spawns.Add(new SpawnPoint { Team = Team.Seraphites, Position = new Vector3(4, 5, 6), YawDegrees = 45 });
        doc.Boundary.Points.AddRange([new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10)]);

        var path = Path.Combine(Path.GetTempPath(), $"fr-scene-{Guid.NewGuid():N}.frscene");
        try
        {
            doc.Save(path);
            var back = SceneDocument.Load(path);
            Assert.Equal("Test arena", back.Name);
            Assert.Equal(doc.Maps[0], back.Maps[0]);
            var o = Assert.Single(back.Objects);
            Assert.Equal(doc.Objects[0].Id, o.Id);
            Assert.Equal(100, o.Prototype);
            Assert.Equal(new Vector3(0, 90, 0), o.RotationDegrees);
            Assert.Equal(new Vector3(1, 1, 2), o.Scale);
            Assert.Equal(Team.Seraphites, back.Spawns[0].Team);
            Assert.Equal(3, back.Boundary.Points.Count);
            Assert.Contains("\"Seraphites\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EngineMatrix_UsesRowVectorsWithTranslationInLastRow()
    {
        var m = EngineTransform.Compose(new Vector3(-79.617f, 34.338f, -849.131f), new Vector3(0, 90, 0), Vector3.One);
        Assert.Equal(-79.617f, m.M41, 3);
        Assert.Equal(-849.131f, m.M43, 3);
        var x = Vector3.Transform(Vector3.UnitX, m) - m.Translation;
        Assert.Equal(-1f, x.Z, 4); // +X rotated 90° about Y points to -Z (right-handed)
    }

    [Fact]
    public void Boundary_ContainsUsesXzPolygonAndHeight()
    {
        var b = new MapBoundary { MinY = 0, MaxY = 10 };
        b.Points.AddRange([new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)]);
        Assert.True(b.Contains(new Vector3(5, 1, 5)));
        Assert.False(b.Contains(new Vector3(15, 1, 5)));
        Assert.False(b.Contains(new Vector3(5, 11, 5)));
    }
}
