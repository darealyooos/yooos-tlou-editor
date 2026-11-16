using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FactionsReborn.WorldEditor.Core.Archives;

/// <summary>
/// Read-only PSAR archive (optionally inside DSAR) as shipped in TLOU Part II PC build/pc/main.
/// Entry 0 is the manifest of paths; other entries are keyed by MD5(path).
/// </summary>
public sealed class PsarcArchive : IDisposable
{
    private readonly DsarReader _reader;
    private readonly Entry[] _entries;     // index k+1 belongs to Names[k]
    private readonly int[] _blocks;
    private readonly int _blockSize;
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public readonly record struct Entry(int BlockIndex, long Length, long Offset);

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public IReadOnlyList<string> Names { get; }

    public PsarcArchive(string path)
    {
        Path = path;
        _reader = new DsarReader(path);
        var header = _reader.Read(0, 32);
        if (Encoding.ASCII.GetString(header, 0, 4) != "PSAR")
            throw new InvalidDataException($"{path}: not a PSAR archive.");

        int tocLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12));
        int entrySize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16));
        int fileCount = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));
        _blockSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));

        var toc = _reader.Read(32, tocLength - 32);
        var raw = new Entry[fileCount];
        var md5Index = new Dictionary<UInt128, int>(fileCount);
        for (int k = 0; k < fileCount; k++)
        {
            var e = toc.AsSpan(k * entrySize, entrySize);
            int block = BinaryPrimitives.ReadInt32BigEndian(e[16..]);
            long length = ReadBE40(e[20..]);
            long offset = ReadBE40(e[25..]);
            raw[k] = new Entry(block, length, offset);
            md5Index[BinaryPrimitives.ReadUInt128BigEndian(e)] = k;
        }

        int blockWidth = _blockSize <= 65536 ? 2 : 3;
        var blockTable = toc.AsSpan(fileCount * entrySize);
        _blocks = new int[blockTable.Length / blockWidth];
        for (int k = 0; k < _blocks.Length; k++)
        {
            int v = 0;
            for (int b = 0; b < blockWidth; b++) v = (v << 8) | blockTable[k * blockWidth + b];
            _blocks[k] = v;
        }

        var manifest = Encoding.Latin1.GetString(ReadEntry(raw[0]));
        var names = manifest.Split(['\n', '\r', '\0', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        _entries = new Entry[names.Length + 1];
        _entries[0] = raw[0];
        for (int k = 0; k < names.Length; k++)
        {
            var digest = MD5.HashData(Encoding.ASCII.GetBytes(names[k]));
            _entries[k + 1] = md5Index.TryGetValue(BinaryPrimitives.ReadUInt128BigEndian(digest), out int idx) ? raw[idx] : raw[0];
            _byName[names[k]] = k + 1;
        }
        Names = names;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public long GetLength(string name) => _entries[_byName[name]].Length;

    public byte[] ReadFile(string name)
    {
        if (!_byName.TryGetValue(name, out int idx))
            throw new FileNotFoundException($"{name} is not in {FileName}.");
        return ReadEntry(_entries[idx]);
    }

    private byte[] ReadEntry(Entry entry)
    {
        lock (_gate)
        {
            var output = new byte[entry.Length];
            long written = 0, position = entry.Offset;
            int block = entry.BlockIndex;
            while (written < entry.Length)
            {
                int stored = _blocks[block] == 0 ? _blockSize : _blocks[block];
                var chunk = _reader.Read(position, stored);
                position += stored;
                block++;
                int want = (int)Math.Min(_blockSize, entry.Length - written);
                if (stored == want)
                {
                    chunk.CopyTo(output.AsSpan((int)written));
                    written += stored;
                }
                else
                {
                    written += Inflate(chunk, output.AsSpan((int)written, want));
                }
            }
            return output;
        }
    }

    private static int Inflate(byte[] chunk, Span<byte> destination)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(chunk), CompressionMode.Decompress);
            int total = 0, n;
            while (total < destination.Length && (n = z.Read(destination[total..])) > 0) total += n;
            return total;
        }
        catch (InvalidDataException)
        {
            // Same fallback as dsar_list.py: an uncompressed block shorter than the block size.
            int n = Math.Min(chunk.Length, destination.Length);
            chunk.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }

    private static long ReadBE40(ReadOnlySpan<byte> s) =>
        ((long)s[0] << 32) | ((long)s[1] << 24) | ((long)s[2] << 16) | ((long)s[3] << 8) | s[4];

    public void Dispose() => _reader.Dispose();
}
