using System.Buffers.Binary;
using System.IO.Compression;

namespace PhotoClippy.Imaging.Tests;

public enum TiffCompression
{
    None = 1,
    Lzw = 5,
    Jpeg = 7,
    Deflate = 8,
    PackBits = 32773,
    AdobeDeflate = 32946,
}

/// <summary>Chunky sample data (row-major, <see cref="Samples"/> values per pixel) plus layout options.</summary>
internal sealed record TiffImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Samples { get; init; }
    public required int BitsPerSample { get; init; }
    public required ushort[] Data { get; init; }
    public int Photometric { get; init; } = 2;
    public TiffCompression Compression { get; init; } = TiffCompression.None;
    public bool Predictor { get; init; }
    public int RowsPerStrip { get; init; } = 16;
    public int? TileSize { get; init; }
    public bool Planar { get; init; }
    public bool BigEndian { get; init; }
    public int? Orientation { get; init; }
    public ushort[]? ColorMap { get; init; }

    /// <summary>Optional JPEG thumbnail stored in IFD1 via JPEGInterchangeFormat.</summary>
    public byte[]? PreviewJpeg { get; init; }

    public ushort this[int x, int y, int sample] => Data[(((y * Width) + x) * Samples) + sample];
}

/// <summary>Minimal baseline TIFF writer for decoder tests (strips or tiles, chunky or planar, 1/8/16-bit).</summary>
internal static class TiffWriter
{
    private const ushort Short = 3;
    private const ushort Long = 4;

    public static byte[] Write(TiffImage image)
    {
        var output = new List<byte>(image.Width * image.Height * image.Samples * 2);
        var big = image.BigEndian;
        output.AddRange(big ? "MM"u8.ToArray() : "II"u8.ToArray());
        AddU16(output, 42, big);
        AddU32(output, 0, big);

        var chunks = BuildChunks(image);
        var offsets = new uint[chunks.Count];
        var counts = new uint[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            offsets[i] = (uint)output.Count;
            counts[i] = (uint)chunks[i].Length;
            output.AddRange(chunks[i]);
            Pad(output);
        }

        uint previewOffset = 0;
        if (image.PreviewJpeg is { } preview)
        {
            previewOffset = (uint)output.Count;
            output.AddRange(preview);
            Pad(output);
        }

        var entries = new List<(ushort Tag, ushort Type, uint[] Values)>
        {
            (256, Long, [(uint)image.Width]),
            (257, Long, [(uint)image.Height]),
            (258, Short, Enumerable.Repeat((uint)image.BitsPerSample, image.Samples).ToArray()),
            (259, Short, [(uint)image.Compression]),
            (262, Short, [(uint)image.Photometric]),
            (277, Short, [(uint)image.Samples]),
            (284, Short, [image.Planar ? 2u : 1u]),
        };

        if (image.TileSize is { } tile)
        {
            entries.Add((322, Long, [(uint)tile]));
            entries.Add((323, Long, [(uint)tile]));
            entries.Add((324, Long, offsets));
            entries.Add((325, Long, counts));
        }
        else
        {
            entries.Add((273, Long, offsets));
            entries.Add((278, Long, [(uint)image.RowsPerStrip]));
            entries.Add((279, Long, counts));
        }

        if (image.Orientation is { } orientation)
        {
            entries.Add((274, Short, [(uint)orientation]));
        }

        if (image.Predictor)
        {
            entries.Add((317, Short, [2]));
        }

        if (image.Samples == 4 && image.Photometric == 2)
        {
            entries.Add((338, Short, [2]));
        }

        if (image.ColorMap is { } map)
        {
            entries.Add((320, Short, map.Select(static v => (uint)v).ToArray()));
        }

        var ifd0 = (uint)output.Count;
        var next = WriteIfd(output, entries, big);
        if (image.PreviewJpeg is { } jpeg)
        {
            var ifd1 = (uint)output.Count;
            WriteIfd(output,
            [
                (254, Long, [1]),
                (259, Short, [6]),
                (513, Long, [previewOffset]),
                (514, Long, [(uint)jpeg.Length]),
            ], big);
            Patch(output, next, ifd1, big);
        }

        Patch(output, 4, ifd0, big);
        return [.. output];
    }

    private static List<byte[]> BuildChunks(TiffImage image)
    {
        var chunks = new List<byte[]>();
        var planes = image.Planar ? image.Samples : 1;
        for (var plane = 0; plane < planes; plane++)
        {
            if (image.TileSize is { } tile)
            {
                for (var ty = 0; ty < image.Height; ty += tile)
                {
                    for (var tx = 0; tx < image.Width; tx += tile)
                    {
                        chunks.Add(EncodeChunk(image, tx, ty, tile, tile, image.Planar ? plane : -1));
                    }
                }
            }
            else
            {
                for (var y = 0; y < image.Height; y += image.RowsPerStrip)
                {
                    var rows = Math.Min(image.RowsPerStrip, image.Height - y);
                    chunks.Add(EncodeChunk(image, 0, y, image.Width, rows, image.Planar ? plane : -1));
                }
            }
        }

        return chunks;
    }

    private static byte[] EncodeChunk(TiffImage image, int x0, int y0, int width, int height, int plane)
    {
        var spp = plane < 0 ? image.Samples : 1;
        if (image.Compression == TiffCompression.Jpeg)
        {
            var rgba = TestImages.Rgba(width, height, (x, y) =>
            {
                var sx = Math.Min(x0 + x, image.Width - 1);
                var sy = Math.Min(y0 + y, image.Height - 1);
                return new Rgb(image[sx, sy, 0], image[sx, sy, 1], image[sx, sy, 2]);
            });
            return TestImages.Encode(width, height, rgba, quality: 95);
        }

        var bits = image.BitsPerSample;
        var rowBytes = ((width * spp * bits) + 7) / 8;
        var raw = new byte[rowBytes * height];
        var row = new int[width * spp];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var s = 0; s < spp; s++)
                {
                    var sx = x0 + x;
                    var sy = y0 + y;
                    row[(x * spp) + s] = sx < image.Width && sy < image.Height ? image[sx, sy, plane < 0 ? s : plane] : 0;
                }
            }

            if (image.Predictor)
            {
                var mask = (1 << bits) - 1;
                for (var i = row.Length - 1; i >= spp; i--)
                {
                    row[i] = (row[i] - row[i - spp]) & mask;
                }
            }

            var destination = raw.AsSpan(y * rowBytes, rowBytes);
            for (var i = 0; i < row.Length; i++)
            {
                switch (bits)
                {
                    case 16 when image.BigEndian:
                        BinaryPrimitives.WriteUInt16BigEndian(destination[(i * 2)..], (ushort)row[i]);
                        break;
                    case 16:
                        BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], (ushort)row[i]);
                        break;
                    case 8:
                        destination[i] = (byte)row[i];
                        break;
                    default:
                        var bit = i * bits;
                        destination[bit / 8] |= (byte)(row[i] << (8 - bits - (bit % 8)));
                        break;
                }
            }
        }

        return image.Compression switch
        {
            TiffCompression.None => raw,
            TiffCompression.PackBits => PackBits(raw, rowBytes),
            TiffCompression.Lzw => Lzw(raw),
            TiffCompression.Deflate or TiffCompression.AdobeDeflate => Deflate(raw),
            _ => throw new NotSupportedException(),
        };
    }

    public static byte[] PackBits(byte[] raw, int rowBytes)
    {
        var output = new List<byte>(raw.Length);
        for (var offset = 0; offset < raw.Length; offset += rowBytes)
        {
            var src = raw.AsSpan(offset, rowBytes);
            var i = 0;
            while (i < src.Length)
            {
                var run = 1;
                while (i + run < src.Length && run < 128 && src[i + run] == src[i])
                {
                    run++;
                }

                if (run >= 3)
                {
                    output.Add(unchecked((byte)(1 - run)));
                    output.Add(src[i]);
                    i += run;
                    continue;
                }

                var start = i;
                var length = 0;
                while (i < src.Length && length < 128 && !(i + 2 < src.Length && src[i] == src[i + 1] && src[i] == src[i + 2]))
                {
                    i++;
                    length++;
                }

                output.Add((byte)(length - 1));
                output.AddRange(src.Slice(start, length));
            }
        }

        return [.. output];
    }

    /// <summary>TIFF LZW (MSB-first, early code-width change), matching libtiff's encoder.</summary>
    public static byte[] Lzw(ReadOnlySpan<byte> data)
    {
        const int clear = 256;
        const int end = 257;
        var bits = new BitWriter();
        var table = new Dictionary<int, int>();
        var width = 9;
        var next = 258;
        bits.Write(clear, width);
        if (data.IsEmpty)
        {
            bits.Write(end, width);
            return bits.ToArray();
        }

        int prefix = data[0];
        for (var i = 1; i < data.Length; i++)
        {
            var value = data[i];
            var key = (prefix << 8) | value;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            bits.Write(prefix, width);
            table[key] = next++;
            if (next == 4094)
            {
                bits.Write(clear, width);
                table.Clear();
                next = 258;
                width = 9;
            }
            else if (next > (1 << width) - 1)
            {
                width++;
            }

            prefix = value;
        }

        bits.Write(prefix, width);
        next++;
        if (next == 4094)
        {
            bits.Write(clear, width);
            width = 9;
        }
        else if (next > (1 << width) - 1)
        {
            width++;
        }

        bits.Write(end, width);
        return bits.ToArray();
    }

    public static byte[] Deflate(byte[] raw)
    {
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return stream.ToArray();
    }

    private static int WriteIfd(List<byte> output, List<(ushort Tag, ushort Type, uint[] Values)> entries, bool big)
    {
        entries.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));
        var start = output.Count;
        var valuesStart = start + 2 + (entries.Count * 12) + 4;
        var values = new List<byte>();
        AddU16(output, (ushort)entries.Count, big);
        foreach (var (tag, type, data) in entries)
        {
            AddU16(output, tag, big);
            AddU16(output, type, big);
            AddU32(output, (uint)data.Length, big);
            var encoded = new List<byte>();
            foreach (var value in data)
            {
                if (type == Short)
                {
                    AddU16(encoded, (ushort)value, big);
                }
                else
                {
                    AddU32(encoded, value, big);
                }
            }

            if (encoded.Count <= 4)
            {
                while (encoded.Count < 4)
                {
                    encoded.Add(0);
                }

                output.AddRange(encoded);
            }
            else
            {
                AddU32(output, (uint)(valuesStart + values.Count), big);
                values.AddRange(encoded);
                Pad(values);
            }
        }

        var nextPosition = output.Count;
        AddU32(output, 0, big);
        output.AddRange(values);
        return nextPosition;
    }

    private static void Patch(List<byte> output, int position, uint value, bool big)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        for (var i = 0; i < 4; i++)
        {
            output[position + i] = bytes[i];
        }
    }

    private static void AddU16(List<byte> output, ushort value, bool big)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        }

        output.AddRange(bytes);
    }

    private static void AddU32(List<byte> output, uint value, bool big)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        output.AddRange(bytes);
    }

    private static void Pad(List<byte> output)
    {
        if ((output.Count & 1) != 0)
        {
            output.Add(0);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int accumulator;
        private int count;

        public void Write(int code, int width)
        {
            accumulator = (accumulator << width) | code;
            count += width;
            while (count >= 8)
            {
                bytes.Add((byte)(accumulator >> (count - 8)));
                count -= 8;
            }

            accumulator &= (1 << count) - 1;
        }

        public byte[] ToArray()
        {
            if (count > 0)
            {
                bytes.Add((byte)(accumulator << (8 - count)));
                count = 0;
            }

            return [.. bytes];
        }
    }
}
