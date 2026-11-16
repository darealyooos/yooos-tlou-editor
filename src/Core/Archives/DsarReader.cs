namespace FactionsReborn.WorldEditor.Core.Archives;

/// <summary>
/// Random-access reader over a Naughty Dog DSAR container (LZ4 chunks). Files that are not DSAR
/// are read as-is. Mirrors Experiments/RE/scripts/dsar_list.py, which was checked against the game.
/// Not thread-safe; open one reader per thread.
/// </summary>
public sealed class DsarReader : IDisposable
{
    private readonly FileStream _file;
    private readonly Chunk[]? _chunks;
    private readonly long[]? _starts;

    private readonly record struct Chunk(long DecodedOffset, long FileOffset, int DecodedSize, int StoredSize, byte Compression);

    public DsarReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[0x20];
        _file.ReadExactly(header);
        if (header[0] != (byte)'D' || header[1] != (byte)'S' || header[2] != (byte)'A' || header[3] != (byte)'R')
            return;

        int count = BitConverter.ToInt32(header[8..]);
        var table = new byte[count * 0x20];
        _file.ReadExactly(table);
        _chunks = new Chunk[count];
        _starts = new long[count];
        for (int k = 0; k < count; k++)
        {
            var e = table.AsSpan(k * 0x20, 0x20);
            _chunks[k] = new Chunk(
                BitConverter.ToInt64(e),
                BitConverter.ToInt64(e[8..]),
                BitConverter.ToInt32(e[16..]),
                BitConverter.ToInt32(e[20..]),
                e[24]);
            _starts[k] = _chunks[k].DecodedOffset;
        }
    }

    public bool IsDsar => _chunks is not null;

    public byte[] Read(long offset, int size)
    {
        var output = new byte[size];
        Read(offset, output);
        return output;
    }

    public void Read(long offset, Span<byte> output)
    {
        if (_chunks is null)
        {
            _file.Position = offset;
            _file.ReadExactly(output);
            return;
        }

        int written = 0;
        while (written < output.Length)
        {
            int idx = Array.BinarySearch(_starts!, offset);
            if (idx < 0) idx = ~idx - 1;
            var chunk = _chunks[idx];
            var data = DecodeChunk(idx);
            int within = (int)(offset - chunk.DecodedOffset);
            int take = Math.Min(output.Length - written, chunk.DecodedSize - within);
            if (take <= 0) throw new InvalidDataException($"DSAR read past chunk {idx} at 0x{offset:X}.");
            data.AsSpan(within, take).CopyTo(output[written..]);
            written += take;
            offset += take;
        }
    }

    private byte[] DecodeChunk(int idx)
    {
        if (ChunkCache.TryGet(this, idx) is { } hit) return hit;
        var chunk = _chunks![idx];
        var raw = new byte[chunk.StoredSize];
        _file.Position = chunk.FileOffset;
        _file.ReadExactly(raw);
        var data = chunk.Compression == 0 ? raw : Lz4.DecodeBlock(raw, chunk.DecodedSize);
        ChunkCache.Add(this, idx, data);
        return data;
    }

    public void Dispose() { ChunkCache.Forget(this); _file.Dispose(); }
}

/// <summary>
/// Decoded DSAR chunks shared by every open archive, bounded by a byte budget (least recently used first out).
/// Previously each archive kept up to 65 chunks; with ~40 archives open, browsing retained hundreds of MB
/// (measured with the editor's --profile). One global budget keeps the hit rate and bounds memory.
/// </summary>
public static class ChunkCache
{
    private static readonly object Gate = new();
    private static readonly LinkedList<(DsarReader Reader, int Index, byte[] Data)> Lru = new();
    private static readonly Dictionary<(DsarReader, int), LinkedListNode<(DsarReader Reader, int Index, byte[] Data)>> Map = new();
    private static long _bytes;

    /// <summary>Default 64 MB.</summary>
    public static long BudgetBytes { get; set; } = 64L << 20;
    public static long Bytes { get { lock (Gate) return _bytes; } }

    internal static byte[]? TryGet(DsarReader reader, int index)
    {
        lock (Gate)
        {
            if (!Map.TryGetValue((reader, index), out var node)) return null;
            if (node != Lru.First) { Lru.Remove(node); Lru.AddFirst(node); }
            return node.Value.Data;
        }
    }

    internal static void Add(DsarReader reader, int index, byte[] data)
    {
        lock (Gate)
        {
            if (Map.ContainsKey((reader, index))) return;
            Map[(reader, index)] = Lru.AddFirst((reader, index, data));
            _bytes += data.Length;
            while (_bytes > BudgetBytes && Lru.Last is { } last && last != Lru.First) Evict(last);
        }
    }

    internal static void Forget(DsarReader reader)
    {
        lock (Gate)
            for (var n = Lru.First; n is not null;) { var next = n.Next; if (ReferenceEquals(n.Value.Reader, reader)) Evict(n); n = next; }
    }

    public static void Clear() { lock (Gate) { Lru.Clear(); Map.Clear(); _bytes = 0; } }

    private static void Evict(LinkedListNode<(DsarReader Reader, int Index, byte[] Data)> node)
    {
        Lru.Remove(node);
        Map.Remove((node.Value.Reader, node.Value.Index));
        _bytes -= node.Value.Data.Length;
    }
}

/// <summary>Raw LZ4 block decoder (no frame header).</summary>
public static class Lz4
{
    public static byte[] DecodeBlock(ReadOnlySpan<byte> src, int decodedSize)
    {
        var dst = new byte[decodedSize];
        int i = 0, o = 0;
        while (i < src.Length)
        {
            int token = src[i++];
            int literal = token >> 4;
            if (literal == 15)
            {
                byte b;
                do { b = src[i++]; literal += b; } while (b == 255);
            }
            src.Slice(i, literal).CopyTo(dst.AsSpan(o));
            i += literal; o += literal;
            if (i >= src.Length) break;

            int offset = src[i] | (src[i + 1] << 8);
            i += 2;
            int match = token & 15;
            if (match == 15)
            {
                byte b;
                do { b = src[i++]; match += b; } while (b == 255);
            }
            match += 4;
            int from = o - offset;
            for (int k = 0; k < match; k++) dst[o + k] = dst[from + k];
            o += match;
        }
        if (o != decodedSize) throw new InvalidDataException($"LZ4 block decoded {o} bytes, expected {decodedSize}.");
        return dst;
    }
}
