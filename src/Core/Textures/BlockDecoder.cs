namespace FactionsReborn.WorldEditor.Core.Textures;

/// <summary>
/// Decodes mip 0 of a D3D12 texture (rows padded to 256 bytes) to BGRA8.
/// Supported DXGI formats: BC1 (71/72), BC2 (74/75), BC3 (77/78), BC4 (80/81), BC5 (83/84),
/// R8G8B8A8 (28/29), B8G8R8A8 (87/91), R8 (61). BC6H/BC7 return null (shown untextured).
/// </summary>
public static class BlockDecoder
{
    public const int PitchAlignment = 256;

    public static bool IsSupported(int dxgi) => dxgi is 71 or 72 or 74 or 75 or 77 or 78 or 80 or 81 or 83 or 84 or 28 or 29 or 87 or 91 or 61;

    public static byte[]? Decode(int dxgi, ReadOnlySpan<byte> src, int width, int height)
    {
        switch (dxgi)
        {
            case 71 or 72: return DecodeBlocks(src, width, height, 8, Bc1);
            case 74 or 75: return DecodeBlocks(src, width, height, 16, Bc2);
            case 77 or 78: return DecodeBlocks(src, width, height, 16, Bc3);
            case 80 or 81: return DecodeBlocks(src, width, height, 8, Bc4);
            case 83 or 84: return DecodeBlocks(src, width, height, 16, Bc5);
            case 28 or 29: return DecodeLinear(src, width, height, 4, (s, d) => { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3]; });
            case 87 or 91: return DecodeLinear(src, width, height, 4, (s, d) => { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = s[3]; });
            case 61: return DecodeLinear(src, width, height, 1, (s, d) => { d[0] = d[1] = d[2] = s[0]; d[3] = 255; });
            default: return null;
        }
    }

    private delegate void BlockFn(ReadOnlySpan<byte> block, Span<byte> rgba64);
    private delegate void PixelFn(ReadOnlySpan<byte> src, Span<byte> bgra);

    private static int Align(int v, int a) => (v + a - 1) / a * a;

    private static byte[]? DecodeBlocks(ReadOnlySpan<byte> src, int width, int height, int blockBytes, BlockFn fn)
    {
        int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
        int pitch = Align(bw * blockBytes, PitchAlignment);
        if ((long)pitch * (bh - 1) + bw * blockBytes > src.Length) return null;
        var output = new byte[width * height * 4];
        Span<byte> px = stackalloc byte[64]; // 16 pixels BGRA
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                fn(src.Slice(by * pitch + bx * blockBytes, blockBytes), px);
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= height) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int pxx = bx * 4 + x;
                        if (pxx >= width) break;
                        px.Slice((y * 4 + x) * 4, 4).CopyTo(output.AsSpan((py * width + pxx) * 4));
                    }
                }
            }
        return output;
    }

    private static byte[]? DecodeLinear(ReadOnlySpan<byte> src, int width, int height, int bpp, PixelFn fn)
    {
        int pitch = Align(width * bpp, PitchAlignment);
        if ((long)pitch * (height - 1) + width * bpp > src.Length) return null;
        var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                fn(src.Slice(y * pitch + x * bpp, bpp), output.AsSpan((y * width + x) * 4, 4));
        return output;
    }

    // Output pixels are BGRA.
    private static void Bc1(ReadOnlySpan<byte> b, Span<byte> o) => ColorBlock(b, o, allowPunchThrough: true);

    private static void ColorBlock(ReadOnlySpan<byte> b, Span<byte> o, bool allowPunchThrough)
    {
        int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4], a = stackalloc int[4];
        Unpack565(c0, out r[0], out g[0], out bl[0]);
        Unpack565(c1, out r[1], out g[1], out bl[1]);
        a[0] = a[1] = a[2] = a[3] = 255;
        if (c0 > c1 || !allowPunchThrough)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2;
            r[3] = g[3] = bl[3] = 0; a[3] = 0;
        }
        uint bits = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((bits >> (2 * i)) & 3);
            o[i * 4] = (byte)bl[k]; o[i * 4 + 1] = (byte)g[k]; o[i * 4 + 2] = (byte)r[k]; o[i * 4 + 3] = (byte)a[k];
        }
    }

    private static void Unpack565(int c, out int r, out int g, out int b)
    {
        r = ((c >> 11) & 31) * 255 / 31;
        g = ((c >> 5) & 63) * 255 / 63;
        b = (c & 31) * 255 / 31;
    }

    private static void Bc2(ReadOnlySpan<byte> b, Span<byte> o)
    {
        ColorBlock(b[8..], o, allowPunchThrough: false);
        for (int i = 0; i < 16; i++)
        {
            int nibble = (b[i / 2] >> (4 * (i & 1))) & 15;
            o[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    private static void Bc3(ReadOnlySpan<byte> b, Span<byte> o)
    {
        ColorBlock(b[8..], o, allowPunchThrough: false);
        Span<byte> alpha = stackalloc byte[16];
        AlphaBlock(b, alpha);
        for (int i = 0; i < 16; i++) o[i * 4 + 3] = alpha[i];
    }

    private static void Bc4(ReadOnlySpan<byte> b, Span<byte> o)
    {
        Span<byte> v = stackalloc byte[16];
        AlphaBlock(b, v);
        for (int i = 0; i < 16; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = v[i]; o[i * 4 + 3] = 255; }
    }

    private static void Bc5(ReadOnlySpan<byte> b, Span<byte> o)
    {
        Span<byte> red = stackalloc byte[16], green = stackalloc byte[16];
        AlphaBlock(b, red);
        AlphaBlock(b[8..], green);
        for (int i = 0; i < 16; i++) { o[i * 4] = 255; o[i * 4 + 1] = green[i]; o[i * 4 + 2] = red[i]; o[i * 4 + 3] = 255; }
    }

    /// <summary>BC3/BC4 8-byte interpolated single channel block.</summary>
    internal static void AlphaBlock(ReadOnlySpan<byte> b, Span<byte> output)
    {
        Span<int> v = stackalloc int[8];
        v[0] = b[0]; v[1] = b[1];
        if (v[0] > v[1])
            for (int k = 1; k < 7; k++) v[k + 1] = ((7 - k) * v[0] + k * v[1]) / 7;
        else
        {
            for (int k = 1; k < 5; k++) v[k + 1] = ((5 - k) * v[0] + k * v[1]) / 5;
            v[6] = 0; v[7] = 255;
        }
        ulong bits = 0;
        for (int k = 0; k < 6; k++) bits |= (ulong)b[2 + k] << (8 * k);
        for (int i = 0; i < 16; i++) output[i] = (byte)v[(int)((bits >> (3 * i)) & 7)];
    }
}
