using System.Buffers.Binary;
using System.IO.Compression;

namespace UICtl.Core.Interop;

/// <summary>
/// Minimal dependency-free PNG reader/writer - avoids pulling in a whole
/// image library (System.Drawing.Common needs libgdiplus on Linux and is
/// unsupported there; SixLabors.ImageSharp is a much bigger dependency
/// than this one format, in one place, needs) for a well-specified,
/// bounded format. Uses the BCL's own <see cref="ZLibStream"/> for the
/// zlib/deflate layer (RFC 1950) - the only genuinely nontrivial piece
/// (CRC32, chunk framing, scanline filtering) is ~100 lines of stable,
/// unchanging-since-1996 spec, the same spirit as this project's other
/// hand-rolled protocol code (Xlib/EWMH, uinput, the GNOME Shell
/// extension's D-Bus export).
///
/// Encode always writes 8-bit RGBA with filter type 0 (None) per scanline
/// - larger files than a real encoder's filter heuristics would produce,
/// but simpler and still fully spec-compliant. Decode supports what a
/// screenshot actually needs: non-interlaced, 8-bit depth, color type 2
/// (RGB) or 6 (RGBA) - anything else (a palette PNG, 16-bit depth,
/// Adam7 interlacing) throws naming what was found, rather than silently
/// producing garbage - no source in this codebase currently emits those,
/// this is a decoder for our own encoder's output and the portal
/// screenshot backend's PNG files (GNOME's screenshot portal saves 8-bit
/// RGBA, confirmed live - see gnome-extension-adjacent WaylandScreenshotBackend.cs).
/// </summary>
internal static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace method
        WriteChunk(output, "IHDR", ihdr);

        var raw = new byte[height * (1 + width * 4)];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int rawOffset = y * (1 + stride);
            raw[rawOffset] = 0; // filter type: None
            Buffer.BlockCopy(rgba, y * stride, raw, rawOffset + 1, stride);
        }

        using var idatStream = new MemoryStream();
        using (var zlib = new ZLibStream(idatStream, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(output, "IDAT", idatStream.ToArray());

        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    public static (byte[] Rgba, int Width, int Height) Decode(byte[] png)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new UiCtlException("not a PNG file (bad signature)");

        int pos = Signature.Length;
        int width = 0, height = 0, colorType = -1;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int dataStart = pos + 8;
            if (dataStart + (int)length > png.Length)
                throw new UiCtlException("truncated PNG chunk");

            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataStart));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataStart + 4));
                    int bitDepth = png[dataStart + 8];
                    colorType = png[dataStart + 9];
                    int interlace = png[dataStart + 12];
                    if (bitDepth != 8 || (colorType != 2 && colorType != 6) || interlace != 0)
                        throw new UiCtlException($"unsupported PNG (bit depth {bitDepth}, color type {colorType}, interlace {interlace}) - only 8-bit non-interlaced RGB/RGBA is supported");
                    break;
                case "IDAT":
                    idat.Write(png, dataStart, (int)length);
                    break;
                case "IEND":
                    pos = png.Length;
                    continue;
            }
            pos = dataStart + (int)length + 4; // + CRC
        }

        if (width == 0 || height == 0) throw new UiCtlException("PNG has no IHDR");

        int channels = colorType == 6 ? 4 : 3;
        int stride = width * channels;
        var raw = new byte[height * (1 + stride)];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            ReadExactly(zlib, raw);

        var rgba = new byte[width * height * 4];
        var prevRow = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rawOffset = y * (1 + stride);
            byte filter = raw[rawOffset];
            var row = raw.AsSpan(rawOffset + 1, stride);
            Unfilter(filter, row, prevRow, channels);

            int dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                rgba[dst + x * 4 + 0] = row[s + 0];
                rgba[dst + x * 4 + 1] = row[s + 1];
                rgba[dst + x * 4 + 2] = row[s + 2];
                rgba[dst + x * 4 + 3] = channels == 4 ? row[s + 3] : (byte)255;
            }
            row.CopyTo(prevRow);
        }
        return (rgba, width, height);
    }

    /// <summary>PNG scanline unfiltering (spec section 9.2-9.4) - reverses whichever of the 5 filter types the encoder used for this row, in place.</summary>
    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> prevRow, int channels)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= channels ? row[i - channels] : 0;
            int b = prevRow[i];
            int c = i >= channels ? prevRow[i - channels] : 0;
            int add = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new UiCtlException($"unsupported PNG filter type {filter}"),
            };
            row[i] = (byte)(row[i] + add);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) throw new UiCtlException("PNG data ended before expected pixel count (truncated file?)");
            total += n;
        }
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        output.Write(lenBytes);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
