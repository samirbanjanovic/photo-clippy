namespace PhotoClippy.Imaging;

/// <summary>Parsed, validated description of one TIFF image (IFD) that the baseline decoder can handle.</summary>
internal sealed class TiffLayout
{
    private const int MaxDimension = 1 << 18;

    private TiffLayout()
    {
    }

    public required TiffIfd Ifd { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int BitsPerSample { get; init; }

    public int SamplesPerPixel { get; init; }

    public int Compression { get; init; }

    public int Photometric { get; init; }

    public bool Planar { get; init; }

    public int Predictor { get; init; }

    public bool Tiled { get; init; }

    public int ChunkWidth { get; init; }

    public int ChunkHeight { get; init; }

    public long[] Offsets { get; init; } = [];

    public long[] ByteCounts { get; init; } = [];

    public byte[]? JpegTables { get; init; }

    public ushort[]? ColorMap { get; init; }

    public int NewSubfileType { get; init; }

    public Size Size => new(Width, Height);

    public int ChunksAcross => Tiled ? (Width + ChunkWidth - 1) / ChunkWidth : 1;

    public int ChunksDown => (Height + ChunkHeight - 1) / ChunkHeight;

    public int Planes => Planar ? SamplesPerPixel : 1;

    public int SamplesPerChunkPixel => Planar ? 1 : SamplesPerPixel;

    public bool IsJpeg => Compression == 7;

    public bool HasPixelData => Offsets.Length > 0;

    /// <summary>Bytes in one decompressed row of one chunk (tile width or full width for strips).</summary>
    public int ChunkRowBytes => checked((int)(((long)ChunkWidth * SamplesPerChunkPixel * BitsPerSample + 7) / 8));

    public static int ReadPhotometric(TiffStructure tiff, TiffIfd ifd) =>
        tiff.GetInt(ifd, TiffTag.Photometric, tiff.GetInt(ifd, TiffTag.SamplesPerPixel, 1) >= 3 ? 2 : 1);

    public static Size? ReadSize(TiffStructure tiff, TiffIfd ifd)
    {
        var width = tiff.GetInt(ifd, TiffTag.ImageWidth, 0);
        var height = tiff.GetInt(ifd, TiffTag.ImageLength, 0);
        return width > 0 && height > 0 ? new Size(width, height) : null;
    }

    /// <summary>Parses the IFD; returns null with a reason when the image cannot be decoded.</summary>
    public static TiffLayout? TryCreate(TiffStructure tiff, TiffIfd ifd, out string reason)
    {
        reason = "unsupported TIFF layout";
        var width = tiff.GetInt(ifd, TiffTag.ImageWidth, 0);
        var height = tiff.GetInt(ifd, TiffTag.ImageLength, 0);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            reason = "invalid TIFF dimensions";
            return null;
        }

        var spp = tiff.GetInt(ifd, TiffTag.SamplesPerPixel, 1);
        var bitsValues = tiff.GetValues(ifd, TiffTag.BitsPerSample, 64);
        var bps = bitsValues is { Length: > 0 } ? (int)bitsValues[0] : 1;
        if (bitsValues is not null)
        {
            foreach (var value in bitsValues)
            {
                if (value != bps)
                {
                    reason = "mixed TIFF bit depths are not supported";
                    return null;
                }
            }
        }

        var compression = tiff.GetInt(ifd, TiffTag.Compression, 1);
        var photometric = ReadPhotometric(tiff, ifd);
        var planar = tiff.GetInt(ifd, TiffTag.PlanarConfiguration, 1) == 2 && spp > 1;
        var predictor = tiff.GetInt(ifd, TiffTag.Predictor, 1);
        var sampleFormat = tiff.GetInt(ifd, TiffTag.SampleFormat, 1);
        var fillOrder = tiff.GetInt(ifd, TiffTag.FillOrder, 1);

        if (spp is < 1 or > 16)
        {
            reason = "invalid TIFF samples per pixel";
            return null;
        }

        if (compression is not (1 or 5 or 7 or 8 or 32946 or 32773))
        {
            reason = "unsupported TIFF compression " + compression.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        if (sampleFormat is not (1 or 0) || fillOrder != 1)
        {
            reason = "unsupported TIFF sample format";
            return null;
        }

        if (compression == 7)
        {
            if (bps != 8 || photometric is not (1 or 2 or 6) || planar)
            {
                reason = "unsupported JPEG-in-TIFF variant";
                return null;
            }
        }
        else
        {
            var validDepth = bps is 8 or 16 || (bps is 1 or 2 or 4 && spp == 1 && photometric is 0 or 1 or 3);
            if (!validDepth)
            {
                reason = "unsupported TIFF bit depth " + bps.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }

            var validPhotometric = photometric switch
            {
                0 or 1 => true,
                2 => spp >= 3,
                3 => spp == 1 && bps <= 8,
                5 => spp >= 4,
                _ => false,
            };
            if (!validPhotometric)
            {
                reason = "unsupported TIFF photometric interpretation " + photometric.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }

            if (predictor is not (1 or 2) || (predictor == 2 && bps < 8))
            {
                reason = "unsupported TIFF predictor";
                return null;
            }
        }

        ushort[]? colorMap = null;
        if (photometric == 3 && compression != 7)
        {
            var map = tiff.GetValues(ifd, TiffTag.ColorMap, 3 * 256);
            if (map is null || map.Length < 3 << bps)
            {
                reason = "TIFF palette is missing";
                return null;
            }

            colorMap = new ushort[map.Length];
            for (var i = 0; i < map.Length; i++)
            {
                colorMap[i] = (ushort)map[i];
            }
        }

        var tiled = ifd.Has(TiffTag.TileOffsets);
        int chunkWidth;
        int chunkHeight;
        long[]? offsets;
        long[]? counts;
        if (tiled)
        {
            chunkWidth = tiff.GetInt(ifd, TiffTag.TileWidth, 0);
            chunkHeight = tiff.GetInt(ifd, TiffTag.TileLength, 0);
            if (chunkWidth <= 0 || chunkHeight <= 0 || chunkWidth > MaxDimension || chunkHeight > MaxDimension)
            {
                reason = "invalid TIFF tile size";
                return null;
            }

            offsets = tiff.GetValues(ifd, TiffTag.TileOffsets);
            counts = tiff.GetValues(ifd, TiffTag.TileByteCounts);
        }
        else
        {
            chunkWidth = width;
            var rows = tiff.GetValue(ifd, TiffTag.RowsPerStrip) ?? height;
            chunkHeight = rows <= 0 || rows > height ? height : (int)rows;
            offsets = tiff.GetValues(ifd, TiffTag.StripOffsets);
            counts = tiff.GetValues(ifd, TiffTag.StripByteCounts);
        }

        var chunksAcross = tiled ? (width + chunkWidth - 1) / chunkWidth : 1;
        var chunksDown = (height + chunkHeight - 1) / chunkHeight;
        var required = (long)chunksAcross * chunksDown * (planar ? spp : 1);
        if (offsets is null || offsets.Length < required)
        {
            reason = "TIFF image data offsets are missing";
            return null;
        }

        if (counts is null || counts.Length < required)
        {
            if (compression != 1)
            {
                reason = "TIFF image byte counts are missing";
                return null;
            }

            var chunkRowBytes = ((long)chunkWidth * (planar ? 1 : spp) * bps + 7) / 8;
            counts = new long[offsets.Length];
            Array.Fill(counts, chunkRowBytes * chunkHeight);
        }

        return new TiffLayout
        {
            Ifd = ifd,
            Width = width,
            Height = height,
            BitsPerSample = bps,
            SamplesPerPixel = spp,
            Compression = compression,
            Photometric = photometric,
            Planar = planar,
            Predictor = compression == 7 ? 1 : predictor,
            Tiled = tiled,
            ChunkWidth = chunkWidth,
            ChunkHeight = chunkHeight,
            JpegTables = compression == 7 ? tiff.GetBytes(ifd, TiffTag.JpegTables, 1 << 20) : null,
            ColorMap = colorMap,
            NewSubfileType = tiff.GetInt(ifd, TiffTag.NewSubfileType, 0),
            Offsets = offsets,
            ByteCounts = counts,
        };
    }

    public int ChunkIndex(int plane, int chunkRow, int chunkColumn) =>
        (plane * ChunksAcross * ChunksDown) + (chunkRow * ChunksAcross) + chunkColumn;
}
