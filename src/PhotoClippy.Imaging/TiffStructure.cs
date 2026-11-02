using System.Buffers.Binary;

namespace PhotoClippy.Imaging;

internal static class TiffTag
{
    public const ushort NewSubfileType = 0x00FE;
    public const ushort ImageWidth = 0x0100;
    public const ushort ImageLength = 0x0101;
    public const ushort BitsPerSample = 0x0102;
    public const ushort Compression = 0x0103;
    public const ushort Photometric = 0x0106;
    public const ushort FillOrder = 0x010A;
    public const ushort StripOffsets = 0x0111;
    public const ushort Orientation = 0x0112;
    public const ushort SamplesPerPixel = 0x0115;
    public const ushort RowsPerStrip = 0x0116;
    public const ushort StripByteCounts = 0x0117;
    public const ushort PlanarConfiguration = 0x011C;
    public const ushort Predictor = 0x013D;
    public const ushort ColorMap = 0x0140;
    public const ushort TileWidth = 0x0142;
    public const ushort TileLength = 0x0143;
    public const ushort TileOffsets = 0x0144;
    public const ushort TileByteCounts = 0x0145;
    public const ushort SubIfds = 0x014A;
    public const ushort SampleFormat = 0x0153;
    public const ushort JpegTables = 0x015B;
    public const ushort JpegInterchangeFormat = 0x0201;
    public const ushort JpegInterchangeFormatLength = 0x0202;
    public const ushort ExifIfd = 0x8769;
    public const ushort PanasonicJpgFromRaw = 0x002E;
}

internal readonly record struct TiffEntry(ushort Tag, ushort Type, long Count, long ValuePosition);

internal sealed class TiffIfd
{
    private readonly Dictionary<ushort, TiffEntry> entries;

    public TiffIfd(long position, Dictionary<ushort, TiffEntry> entries, long nextOffset)
    {
        Position = position;
        this.entries = entries;
        NextOffset = nextOffset;
    }

    public long Position { get; }

    public long NextOffset { get; }

    public bool TryGet(ushort tag, out TiffEntry entry) => entries.TryGetValue(tag, out entry);

    public bool Has(ushort tag) => entries.ContainsKey(tag);
}

/// <summary>Minimal TIFF/BigTIFF structure reader (both byte orders, embedded TIFFs via a base offset).</summary>
internal sealed class TiffStructure
{
    private const int MaxEntries = 4096;
    private const long MaxValues = 1 << 24;
    private static readonly int[] TypeSizes = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4, 0, 0, 8, 8, 8];

    private readonly SourceFile file;

    private TiffStructure(SourceFile file, long baseOffset, bool littleEndian, bool bigTiff, ushort magic, long firstIfdOffset)
    {
        this.file = file;
        BaseOffset = baseOffset;
        LittleEndian = littleEndian;
        BigTiff = bigTiff;
        Magic = magic;
        FirstIfdOffset = firstIfdOffset;
    }

    public long BaseOffset { get; }

    public bool LittleEndian { get; }

    public bool BigTiff { get; }

    public ushort Magic { get; }

    public long FirstIfdOffset { get; }

    public SourceFile File => file;

    public static bool LooksLikeTiff(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return false;
        }

        bool little;
        if (header[0] == (byte)'I' && header[1] == (byte)'I')
        {
            little = true;
        }
        else if (header[0] == (byte)'M' && header[1] == (byte)'M')
        {
            little = false;
        }
        else
        {
            return false;
        }

        var magic = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        return magic is 42 or 43 or 0x55 or 0x4F52 or 0x5352;
    }

    public static TiffStructure? TryOpen(SourceFile file, long baseOffset)
    {
        Span<byte> header = stackalloc byte[16];
        if (file.Read(baseOffset, header) < 8 || !LooksLikeTiff(header))
        {
            return null;
        }

        var little = header[0] == (byte)'I';
        var magic = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (magic == 43)
        {
            var offsetSize = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
            if (offsetSize != 8)
            {
                return null;
            }

            var first = little ? BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) : BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            return first > long.MaxValue ? null : new TiffStructure(file, baseOffset, little, true, magic, (long)first);
        }

        var firstIfd = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        return new TiffStructure(file, baseOffset, little, false, magic, firstIfd);
    }

    public TiffIfd? ReadIfd(long relativeOffset)
    {
        if (relativeOffset < 8)
        {
            return null;
        }

        var position = BaseOffset + relativeOffset;
        var countSize = BigTiff ? 8 : 2;
        var entrySize = BigTiff ? 20 : 12;
        var nextSize = BigTiff ? 8 : 4;
        Span<byte> countBytes = stackalloc byte[8];
        if (file.Read(position, countBytes[..countSize]) != countSize)
        {
            return null;
        }

        var count = BigTiff ? (long)ReadU64(countBytes) : ReadU16(countBytes);
        if (count <= 0 || count > MaxEntries)
        {
            return null;
        }

        var blockLength = (int)(count * entrySize) + nextSize;
        Span<byte> block = blockLength <= 1024 ? stackalloc byte[blockLength] : new byte[blockLength];
        var read = file.Read(position + countSize, block);
        if (read < count * entrySize)
        {
            return null;
        }

        var entries = new Dictionary<ushort, TiffEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = block.Slice(i * entrySize, entrySize);
            var tag = ReadU16(e);
            var type = ReadU16(e[2..]);
            var valueCount = BigTiff ? (long)Math.Min(ReadU64(e[4..]), long.MaxValue) : ReadU32(e[4..]);
            var inlineSize = BigTiff ? 8 : 4;
            var valueField = BigTiff ? 12 : 8;
            var typeSize = type < TypeSizes.Length ? TypeSizes[type] : 0;
            if (typeSize == 0 || valueCount < 0 || valueCount > MaxValues)
            {
                continue;
            }

            long valuePosition;
            if (valueCount * typeSize <= inlineSize)
            {
                valuePosition = position + countSize + (i * entrySize) + valueField;
            }
            else
            {
                var offset = BigTiff ? (long)Math.Min(ReadU64(e[valueField..]), long.MaxValue) : ReadU32(e[valueField..]);
                valuePosition = BaseOffset + offset;
            }

            entries.TryAdd(tag, new TiffEntry(tag, type, valueCount, valuePosition));
        }

        long next = 0;
        if (read >= blockLength)
        {
            var nextBytes = block.Slice((int)(count * entrySize), nextSize);
            next = BigTiff ? (long)Math.Min(ReadU64(nextBytes), long.MaxValue) : ReadU32(nextBytes);
        }

        return new TiffIfd(position, entries, next);
    }

    public long? GetValue(TiffIfd ifd, ushort tag)
    {
        if (!ifd.TryGet(tag, out var entry) || entry.Count < 1)
        {
            return null;
        }

        Span<byte> value = stackalloc byte[8];
        var size = TypeSizes[entry.Type];
        if (file.Read(entry.ValuePosition, value[..size]) != size)
        {
            return null;
        }

        return Decode(entry.Type, value);
    }

    public int GetInt(TiffIfd ifd, ushort tag, int fallback)
    {
        var value = GetValue(ifd, tag);
        return value is null || value > int.MaxValue || value < int.MinValue ? fallback : (int)value.Value;
    }

    public long[]? GetValues(TiffIfd ifd, ushort tag, long maxCount = MaxValues)
    {
        if (!ifd.TryGet(tag, out var entry) || entry.Count < 1 || entry.Count > maxCount)
        {
            return null;
        }

        var size = TypeSizes[entry.Type];
        var total = checked((int)(entry.Count * size));
        var raw = new byte[total];
        if (file.Read(entry.ValuePosition, raw) != total)
        {
            return null;
        }

        var values = new long[entry.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Decode(entry.Type, raw.AsSpan(i * size, size));
        }

        return values;
    }

    public byte[]? GetBytes(TiffIfd ifd, ushort tag, int maxLength)
    {
        if (!ifd.TryGet(tag, out var entry) || entry.Count < 1)
        {
            return null;
        }

        var total = entry.Count * TypeSizes[entry.Type];
        if (total > maxLength)
        {
            return null;
        }

        var raw = new byte[total];
        return file.Read(entry.ValuePosition, raw) == total ? raw : null;
    }

    public ushort ReadU16(ReadOnlySpan<byte> bytes) =>
        LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    public uint ReadU32(ReadOnlySpan<byte> bytes) =>
        LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    public ulong ReadU64(ReadOnlySpan<byte> bytes) =>
        LittleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);

    /// <summary>Enumerates IFD0's chain plus SubIFDs and the EXIF IFD (depth-limited, loop-safe).</summary>
    public List<TiffIfd> CollectIfds(bool includeSubIfds, bool includeExif)
    {
        var result = new List<TiffIfd>();
        var visited = new HashSet<long>();
        var pending = new Queue<long>();
        var chain = FirstIfdOffset;
        var chainCount = 0;
        while (chain > 0 && chainCount++ < 64)
        {
            var ifd = Visit(chain, visited, result);
            if (ifd is null)
            {
                break;
            }

            chain = ifd.NextOffset;
            EnqueueChildren(ifd, pending, includeSubIfds, includeExif);
        }

        while (pending.Count > 0 && result.Count < 256)
        {
            var offset = pending.Dequeue();
            var ifd = Visit(offset, visited, result);
            if (ifd is not null)
            {
                EnqueueChildren(ifd, pending, includeSubIfds, includeExif);
            }
        }

        return result;
    }

    private void EnqueueChildren(TiffIfd ifd, Queue<long> pending, bool includeSubIfds, bool includeExif)
    {
        if (includeSubIfds && GetValues(ifd, TiffTag.SubIfds, 64) is { } subs)
        {
            foreach (var sub in subs)
            {
                pending.Enqueue(sub);
            }
        }

        if (includeExif && GetValue(ifd, TiffTag.ExifIfd) is { } exif)
        {
            pending.Enqueue(exif);
        }
    }

    private TiffIfd? Visit(long offset, HashSet<long> visited, List<TiffIfd> result)
    {
        if (offset <= 0 || BaseOffset + offset >= file.Length || !visited.Add(offset))
        {
            return null;
        }

        var ifd = ReadIfd(offset);
        if (ifd is not null)
        {
            result.Add(ifd);
        }

        return ifd;
    }

    private long Decode(ushort type, ReadOnlySpan<byte> value) => type switch
    {
        1 or 2 or 7 => value[0],
        6 => (sbyte)value[0],
        3 => ReadU16(value),
        8 => (short)ReadU16(value),
        4 or 13 => ReadU32(value),
        9 => (int)ReadU32(value),
        16 or 18 => (long)Math.Min(ReadU64(value), long.MaxValue),
        17 => (long)ReadU64(value),
        5 => ReadU32(value[4..]) == 0 ? 0 : ReadU32(value) / ReadU32(value[4..]),
        _ => 0,
    };
}
