using System.Numerics;
using System.Text.RegularExpressions;

namespace FactionsReborn.WorldEditor.Core.Pak;

/// <summary>Decoded triangle mesh in engine units (metres, Y up, right-handed).</summary>
public sealed class MeshData
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public Vector2[]? TexCoords { get; init; }
    public required int[] Indices { get; init; }
    public required int MaterialIndex { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
}

public sealed class MaterialData
{
    public required string Name { get; init; }
    /// <summary>VRAM_DESC name (".ndb" suffix) of the base-colour texture, if the material has one.</summary>
    public string? BaseColorTexture { get; init; }
    /// <summary>ND helper materials (shared/utilities/: lib-collision, lib-shadowblocker, rainbow) that the game does not draw.</summary>
    public bool IsHelper => Name.StartsWith("shared/utilities/", StringComparison.Ordinal);
}

/// <summary>A prototype: a named group of submeshes (all LODs) placed by instances.</summary>
public sealed class PrototypeData
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int[] Submeshes { get; init; }
    /// <summary>Visible LOD0 submeshes (higher LODs and helper materials excluded).</summary>
    public required int[] Lod0Submeshes { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
}

/// <summary>An instance of a prototype; Matrix uses the engine's row-vector convention (translation in M41..M43).</summary>
public sealed record InstanceData(int Prototype, Matrix4x4 Matrix);

public sealed class GeometryData
{
    public required IReadOnlyList<MeshData> Submeshes { get; init; }
    public required IReadOnlyList<MaterialData> Materials { get; init; }
    public required IReadOnlyList<PrototypeData> Prototypes { get; init; }
    public required IReadOnlyList<InstanceData> Instances { get; init; }
}

/// <summary>
/// Reads GEOMETRY_1 (data offsets relative to the item data):
///  +0x08 u32 submeshCount, +0x0C u32 prototypeCount, +0x10 u32 instanceCount,
///  +0x28 ptr submesh array (stride 0xC0), +0x30 ptr prototype pointer array, +0x38 ptr instance pointer array.
/// Submesh: +0x00 bbox min/max (2x vec4), +0x20 name, +0x30 stream array (stride 0x40), +0x40 u16 indices,
///  +0x48 material, +0x88 vertexCount, +0x8C indexCount, +0x90 streamCount.
/// Stream: +0 data, +8 count, +0x0C bitsPerVertex, +0x14 flags (low byte 0x40 position, 0x41 uv0),
///  +0x18 per-component bit widths, +0x1C (total bits | componentOffset1 &lt;&lt; 8 | componentOffset2 &lt;&lt; 16),
///  +0x20 scale vec4, +0x30 bias vec4. Value = bias + quantized * scale, LSB-first bit stream.
/// Prototype: +0x00 (0,0,0,1), +0x10 bbox min, +0x20 bbox max, +0x30 name, +0x40 list {u64 submeshPtr, u64 index}, +0x84 u32 listCount.
/// Instance: +0x00 4x4 row-major matrix, +0x40 world bbox, +0x60 local bbox, +0x80 prototype ptr.
/// </summary>
public static partial class GeometryReader
{
    private const int SubmeshStride = 0xC0;
    private const int StreamStride = 0x40;

    public static bool HasGeometry(PakFile pak) => pak.Find("GEOMETRY_1") is not null;

    public static GeometryData Read(PakFile pak)
    {
        var item = pak.Find("GEOMETRY_1") ?? throw new InvalidDataException("Pak has no GEOMETRY_1 item.");
        int g = item.DataOffset;
        int submeshCount = pak.I32(g + 0x08);
        int protoCount = pak.I32(g + 0x0C);
        int instanceCount = pak.I32(g + 0x10);
        int submeshArray = pak.Ptr(g + 0x28);

        var materials = new List<MaterialData>();
        var materialIndex = new Dictionary<int, int>();
        var submeshes = new List<MeshData>(submeshCount);
        var submeshByAddress = new Dictionary<int, int>();
        for (int k = 0; k < submeshCount; k++)
        {
            int s = submeshArray + SubmeshStride * k;
            submeshByAddress[s] = k;
            int mat = pak.Ptr(s + 0x48);
            if (!materialIndex.TryGetValue(mat, out int mi))
            {
                mi = materials.Count;
                materialIndex[mat] = mi;
                materials.Add(ReadMaterial(pak, mat));
            }
            submeshes.Add(ReadSubmesh(pak, s, mi));
        }

        var prototypes = new List<PrototypeData>(protoCount);
        int protoArray = pak.Ptr(g + 0x30);
        for (int k = 0; k < protoCount && protoArray != 0; k++)
        {
            int p = pak.Ptr(protoArray + 8 * k);
            if (p == 0) continue;
            int list = pak.Ptr(p + 0x40);
            int count = pak.I32(p + 0x84);
            var members = new List<int>();
            for (int j = 0; j < count && list != 0; j++)
                if (submeshByAddress.TryGetValue(pak.Ptr(list + 16 * j), out int idx)) members.Add(idx);
            var visible = members.Where(i => !materials[submeshes[i].MaterialIndex].IsHelper).ToList();
            var lod0 = visible.Where(i => !IsHigherLod(submeshes[i].Name)).ToArray();
            prototypes.Add(new PrototypeData
            {
                Index = k,
                Name = PrototypeName(pak.PtrString(p + 0x30)),
                Submeshes = members.ToArray(),
                Lod0Submeshes = lod0.Length > 0 ? lod0 : visible.ToArray(),
                BoundsMin = Vec3(pak, p + 0x10),
                BoundsMax = Vec3(pak, p + 0x20),
            });
        }

        var protoByAddress = new Dictionary<int, int>();
        for (int k = 0; k < protoCount && protoArray != 0; k++) protoByAddress[pak.Ptr(protoArray + 8 * k)] = k;
        var instances = new List<InstanceData>(instanceCount);
        int instArray = pak.Ptr(g + 0x38);
        for (int k = 0; k < instanceCount && instArray != 0; k++)
        {
            int a = pak.Ptr(instArray + 8 * k);
            if (a == 0 || !protoByAddress.TryGetValue(pak.Ptr(a + 0x80), out int proto)) continue;
            var m = new Matrix4x4(
                pak.F32(a), pak.F32(a + 4), pak.F32(a + 8), pak.F32(a + 12),
                pak.F32(a + 16), pak.F32(a + 20), pak.F32(a + 24), pak.F32(a + 28),
                pak.F32(a + 32), pak.F32(a + 36), pak.F32(a + 40), pak.F32(a + 44),
                pak.F32(a + 48), pak.F32(a + 52), pak.F32(a + 56), pak.F32(a + 60));
            instances.Add(new InstanceData(proto, m));
        }

        return new GeometryData { Submeshes = submeshes, Materials = materials, Prototypes = prototypes, Instances = instances };
    }

    /// <summary>The submeshes to draw for a standalone actor pak: LOD0 of prototype 0.</summary>
    public static int[] ActorLod0(GeometryData geo) =>
        geo.Prototypes.Count > 0 ? geo.Prototypes[0].Lod0Submeshes
        : Enumerable.Range(0, geo.Submeshes.Count)
            .Where(i => !IsHigherLod(geo.Submeshes[i].Name) && !geo.Materials[geo.Submeshes[i].MaterialIndex].IsHelper).ToArray();

    // "…|bucket_rusty_LOD1|bucket_rusty_LODShape1_…", "…|LOD_1|…", "…_LOD2" — LOD index > 0.
    [GeneratedRegex(@"LOD_?(Shape)?[1-9]", RegexOptions.IgnoreCase)]
    private static partial Regex HigherLod();
    public static bool IsHigherLod(string submeshName) => HigherLod().IsMatch(submeshName);

    private static string PrototypeName(string raw)
    {
        // "pmCollection0.art/levels/…/name.ma" -> "art/levels/…/name"
        var s = raw.StartsWith("pmCollection0.", StringComparison.Ordinal) ? raw["pmCollection0.".Length..] : raw;
        return s.EndsWith(".ma", StringComparison.Ordinal) ? s[..^3] : s;
    }

    private static MeshData ReadSubmesh(PakFile pak, int s, int materialIndex)
    {
        int vertexCount = pak.I32(s + 0x88);
        int indexCount = pak.I32(s + 0x8C);
        int streamCount = pak.I32(s + 0x90);
        int streams = pak.Ptr(s + 0x30);
        Vector3[]? positions = null;
        Vector2[]? uvs = null;
        for (int k = 0; k < streamCount && streams != 0; k++)
        {
            int st = streams + StreamStride * k;
            // Low byte of the stream descriptor: bits 0-5 = semantic (0 position, 1 texcoord0), bit 6 = bit-packed
            // (quantised with scale/bias). Without bit 6 the components are plain float32 / float16, which is how
            // skinned character meshes store them (verified: raw floats match the submesh bounds exactly).
            int kind = (int)(pak.U32(st + 0x14) & 0xFF);
            int semantic = kind & 0x3F;
            bool packed = (kind & 0x40) != 0;
            if (semantic == 0 && positions is null) positions = DecodeStream3(pak, st, vertexCount, packed);
            else if (semantic == 1 && uvs is null) uvs = DecodeStream2(pak, st, vertexCount, packed);
        }
        positions ??= new Vector3[vertexCount];

        int indexData = pak.Ptr(s + 0x40);
        var indices = new int[indexData == 0 ? 0 : indexCount];
        bool wide = vertexCount > 0xFFFF; // never seen in shipped data; u16 everywhere observed
        for (int i = 0; i < indices.Length; i++)
            indices[i] = wide ? pak.I32(indexData + 4 * i) : pak.U16(indexData + 2 * i);
        for (int i = 0; i < indices.Length; i++)
            if ((uint)indices[i] >= (uint)vertexCount) { indices = []; break; }

        return new MeshData
        {
            Name = pak.PtrString(s + 0x20),
            Positions = positions,
            TexCoords = uvs,
            Indices = indices,
            MaterialIndex = materialIndex,
            BoundsMin = Vec3(pak, s),
            BoundsMax = Vec3(pak, s + 0x10),
        };
    }

    private static Vector3[] DecodeStream3(PakFile pak, int st, int count, bool packed = true)
    {
        var (data, bpv, w, o, scale, bias) = StreamHeader(pak, st);
        var output = new Vector3[count];
        if (data == 0 || bpv == 0) return output;
        for (int i = 0; i < count; i++)
        {
            long bit = (long)i * bpv;
            output[i] = new Vector3(
                bias.X + Component(pak.Data, data, bit + o[0], w[0], packed) * scale.X,
                bias.Y + Component(pak.Data, data, bit + o[1], w[1], packed) * scale.Y,
                bias.Z + Component(pak.Data, data, bit + o[2], w[2], packed) * scale.Z);
        }
        return output;
    }

    private static Vector2[] DecodeStream2(PakFile pak, int st, int count, bool packed = true)
    {
        var (data, bpv, w, o, scale, bias) = StreamHeader(pak, st);
        var output = new Vector2[count];
        if (data == 0 || bpv == 0) return output;
        for (int i = 0; i < count; i++)
        {
            long bit = (long)i * bpv;
            output[i] = new Vector2(
                bias.X + Component(pak.Data, data, bit + o[0], w[0], packed) * scale.X,
                bias.Y + Component(pak.Data, data, bit + o[1], w[1], packed) * scale.Y);
        }
        return output;
    }

    /// <summary>One vertex component: an unsigned bit field when packed, otherwise a float32 or float16.</summary>
    internal static float Component(byte[] data, int baseOffset, long bit, int width, bool packed)
    {
        uint raw = Bits(data, baseOffset, bit, width);
        if (packed) return raw;
        return width switch
        {
            32 => BitConverter.UInt32BitsToSingle(raw),
            16 => (float)BitConverter.UInt16BitsToHalf((ushort)raw),
            _ => raw,
        };
    }

    private static (int Data, int Bpv, int[] Widths, int[] Offsets, Vector4 Scale, Vector4 Bias) StreamHeader(PakFile pak, int st)
    {
        uint widths = pak.U32(st + 0x18), offsets = pak.U32(st + 0x1C);
        int[] w = [(int)(widths & 0xFF), (int)((widths >> 8) & 0xFF), (int)((widths >> 16) & 0xFF)];
        int[] o = [0, (int)((offsets >> 8) & 0xFF), (int)((offsets >> 16) & 0xFF)];
        var scale = new Vector4(pak.F32(st + 0x20), pak.F32(st + 0x24), pak.F32(st + 0x28), pak.F32(st + 0x2C));
        var bias = new Vector4(pak.F32(st + 0x30), pak.F32(st + 0x34), pak.F32(st + 0x38), pak.F32(st + 0x3C));
        return (pak.Ptr(st), pak.I32(st + 0x0C), w, o, scale, bias);
    }

    /// <summary>Unsigned little-endian bit field of <paramref name="width"/> bits (≤ 32) starting at bit <paramref name="bit"/>.</summary>
    internal static uint Bits(byte[] data, int baseOffset, long bit, int width)
    {
        if (width == 0) return 0;
        int at = baseOffset + (int)(bit >> 3);
        int shift = (int)(bit & 7);
        ulong v = 0;
        int need = (shift + width + 7) >> 3;
        for (int b = 0; b < need && at + b < data.Length; b++) v |= (ulong)data[at + b] << (8 * b);
        return (uint)((v >> shift) & (width == 32 ? 0xFFFFFFFFUL : (1UL << width) - 1));
    }

    private static MaterialData ReadMaterial(PakFile pak, int m)
    {
        if (m == 0) return new MaterialData { Name = "(none)" };
        // Params are {name, source .tga, 0, texture entry (name = VRAM_DESC name without ".ndb"), …} with stride 0x30.
        // g_tLm* are lightmap defaults; the surface colour is g_tNdFetchBaseColor01Map (then any other *BaseColor*).
        string? baseColor = null;
        int bestRank = int.MaxValue;
        int list = pak.Ptr(m + 0x20);
        for (int k = 0; k < 64 && list != 0; k++)
        {
            int e = list + 0x30 * k;
            if (!pak.HasPtr(e)) break;
            string param = pak.PtrString(e);
            if (!param.StartsWith("g_t", StringComparison.Ordinal)) break;
            int tex = pak.Ptr(e + 0x18);
            if (tex == 0 || !param.Contains("BaseColor", StringComparison.Ordinal) || param.StartsWith("g_tLm", StringComparison.Ordinal)) continue;
            int rank = param.StartsWith("g_tNdFetchBaseColor01", StringComparison.Ordinal) ? 0 : 1;
            if (rank < bestRank) { bestRank = rank; baseColor = pak.PtrString(tex) + ".ndb"; }
        }
        return new MaterialData { Name = pak.PtrString(m), BaseColorTexture = baseColor };
    }

    private static Vector3 Vec3(PakFile pak, int at) => new(pak.F32(at), pak.F32(at + 4), pak.F32(at + 8));
}
