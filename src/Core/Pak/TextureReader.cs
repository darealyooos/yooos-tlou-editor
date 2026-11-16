using FactionsReborn.WorldEditor.Core.Textures;

namespace FactionsReborn.WorldEditor.Core.Pak;

/// <summary>A texture resident in the pak's VRAM blob (the low mips; full-resolution mips stream from .ndb).</summary>
public sealed record TextureDesc(string Name, int DxgiFormat, int Width, int Height, int MipCount, int VramOffset, int VramSize);

/// <summary>Decoded mip 0 as BGRA8 (premultiplied-free, opaque alpha unless the format carries alpha).</summary>
public sealed record TextureImage(string Name, int Width, int Height, byte[] Bgra);

/// <summary>
/// VRAM_DESC data (relative to item data): +0x08 vramOffset, +0x10 size, +0x14 type (3 = texture),
/// +0x18 u64 hash, +0x28 DXGI format, +0x30 mips, +0x34 width, +0x38 height, +0x48 name.
/// Rows of each mip are padded to 256 bytes (D3D12 pitch), mips aligned to 512 bytes.
/// </summary>
public static class TextureReader
{
    public static IReadOnlyList<TextureDesc> List(PakFile pak)
    {
        var list = new List<TextureDesc>();
        foreach (var item in pak.FindAll("VRAM_DESC"))
        {
            int d = item.DataOffset;
            if (pak.I32(d + 0x14) != 3) continue;
            list.Add(new TextureDesc(
                pak.PtrString(d + 0x48),
                pak.I32(d + 0x28),
                pak.I32(d + 0x34),
                pak.I32(d + 0x38),
                pak.I32(d + 0x30),
                pak.I32(d + 0x08),
                pak.I32(d + 0x10)));
        }
        return list;
    }

    public static TextureImage? Decode(PakFile pak, TextureDesc desc)
    {
        int start = pak.VramOffset + desc.VramOffset;
        if (desc.Width <= 0 || desc.Height <= 0 || start < 0 || start + desc.VramSize > pak.Data.Length) return null;
        var src = pak.Data.AsSpan(start, desc.VramSize);
        var bgra = BlockDecoder.Decode(desc.DxgiFormat, src, desc.Width, desc.Height);
        return bgra is null ? null : new TextureImage(desc.Name, desc.Width, desc.Height, bgra);
    }
}
