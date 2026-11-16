using System.Buffers.Binary;
using System.Text;

namespace FactionsReborn.WorldEditor.Core.Pak;

/// <summary>
/// Naughty Dog resource pak (magic 0xA79) as found in actor97/ and pak68/ inside the psarcs.
/// Layout established on bucket-rusty-a.pak and sea-hub-hotel-street.pak (2026-11-14):
///  header: magic, hdrSize, loginPage, loginOfs, pageCount, pageTableOfs, fixupPages, fixupTableOfs, vramSize
///  page table: {u32 fileOffset, u32 size, u32 ?} per page; pages start with DEADBEEF DEADBEEF.
///  page header +0x12 u16 itemCount, +0x14 per item {u32 nameOfs, u32 0, u32 itemOfs, u32 0x12345} (page relative).
///  item header (0x30): u64 nameOfs, u64 typeOfs (page relative), u32 size, 0x12345 tags; data at +0x30.
///  fixup table: {u16 srcPage, u16 dstPage, u32 srcOffset}; the u64 at src holds an offset into dstPage.
///  VRAM blob (textures) is stored after the last page; VRAM_DESC offsets are relative to it.
/// </summary>
public sealed class PakFile
{
    public const uint Magic = 0xA79;

    private readonly Dictionary<long, long> _fixups = new();

    public byte[] Data { get; }
    public IReadOnlyList<(int Offset, int Size)> Pages { get; }
    public IReadOnlyList<PakItem> Items { get; }
    public int VramOffset { get; }
    public int VramSize { get; }

    public PakFile(byte[] data)
    {
        Data = data;
        if (U32(0) != Magic) throw new InvalidDataException($"Not a ND pak (magic 0x{U32(0):X}).");
        int pageCount = (int)U32(0x10);
        int pageTable = (int)U32(0x14);
        int fixupTable = (int)U32(0x1C);
        VramSize = (int)U32(0x20);

        var pages = new (int, int)[pageCount];
        for (int i = 0; i < pageCount; i++)
            pages[i] = ((int)U32(pageTable + 12 * i), (int)U32(pageTable + 12 * i + 4));
        Pages = pages;
        VramOffset = pageCount == 0 ? 0 : pages[^1].Item1 + pages[^1].Item2;

        int fixupOffset = (int)U32(fixupTable + 4);
        int fixupCount = (int)U32(fixupTable + 8);
        for (int k = 0; k < fixupCount; k++)
        {
            int rec = fixupOffset + 8 * k;
            int src = U16(rec), dst = U16(rec + 2);
            long at = pages[src].Item1 + U32(rec + 4);
            _fixups[at] = pages[dst].Item1 + (long)U64((int)at);
        }

        var items = new List<PakItem>();
        foreach (var (pageOffset, _) in pages)
        {
            int count = U16(pageOffset + 0x12);
            for (int k = 0; k < count; k++)
            {
                int itemOffset = pageOffset + (int)U32(pageOffset + 0x1C + 16 * k);
                string name = CString(pageOffset + (int)U64(itemOffset));
                string type = CString(pageOffset + (int)U64(itemOffset + 8));
                items.Add(new PakItem(type, name, itemOffset + 0x30, (int)U32(itemOffset + 0x10)));
            }
        }
        Items = items;
    }

    public PakItem? Find(string type) => Items.FirstOrDefault(i => i.Type == type);
    public IEnumerable<PakItem> FindAll(string type) => Items.Where(i => i.Type == type);

    /// <summary>Resolved absolute file offset of the pointer stored at <paramref name="at"/>, or 0.</summary>
    public int Ptr(int at) => _fixups.TryGetValue(at, out long v) ? (int)v : 0;
    public bool HasPtr(int at) => _fixups.ContainsKey(at);

    public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));
    public int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at));
    public ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at));
    public ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(at));
    public float F32(int at) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(at));

    public string CString(int at)
    {
        if (at <= 0 || at >= Data.Length) return string.Empty;
        int end = Array.IndexOf(Data, (byte)0, at);
        if (end < 0) end = Data.Length;
        return Encoding.Latin1.GetString(Data, at, end - at);
    }

    /// <summary>String referenced by a fixed-up pointer at <paramref name="at"/>.</summary>
    public string PtrString(int at) => HasPtr(at) ? CString(Ptr(at)) : string.Empty;
}

public sealed record PakItem(string Type, string Name, int DataOffset, int Size);
