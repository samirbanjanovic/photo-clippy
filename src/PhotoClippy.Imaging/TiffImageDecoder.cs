using System.Buffers;
using System.Buffers.Binary;
using PhotoClippy.Contracts;

namespace PhotoClippy.Imaging;

/// <summary>
/// Baseline TIFF decoder: strips (streamed row by row) and tiles (banded), chunky and planar,
/// 1/2/4/8/16-bit gray, palette, RGB(A) and CMYK, uncompressed/LZW/Deflate/PackBits and JPEG-in-TIFF.
/// Pixels are area-averaged into the output grid while streaming, so no full-resolution buffer is allocated.
/// </summary>
internal static class TiffImageDecoder
{
    public const string Method = "tiff";
    private const int StripInputBuffer = 128 * 1024;
    private const int TileInputBuffer = 32 * 1024;
    private const long MaxJpegChunkBytes = 512L * 1024 * 1024;

    public static DecodedFrame Decode(TiffStructure tiff, int maxEdge, int? orientation, CancellationToken cancellationToken)
        => TryDecode(tiff, maxEdge, orientation, Method, cancellationToken, out var error)
           ?? throw new ImageDecodeException(error);

    /// <summary>Decodes the main image (or a cheaper same-aspect reduced copy); returns null with a reason on failure.</summary>
    public static DecodedFrame? TryDecode(TiffStructure tiff, int maxEdge, int? orientation, string method, CancellationToken cancellationToken, out string error)
    {
        var ifds = tiff.CollectIfds(includeSubIfds: true, includeExif: false);
        if (ifds.Count == 0)
        {
            error = "TIFF contains no images";
            return null;
        }

        var main = FindMainImage(tiff, ifds, out var mainSize);
        if (main is null)
        {
            error = "TIFF contains no decodable image";
            return null;
        }

        var resolvedOrientation = Geometry.NormalizeOrientation(
            orientation ?? (int?)(tiff.GetValue(main, TiffTag.Orientation) ?? tiff.GetValue(ifds[0], TiffTag.Orientation)) ?? 1);
        var targetLongEdge = Geometry.Target(mainSize, maxEdge).LongEdge;

        if (targetLongEdge < mainSize.LongEdge)
        {
            foreach (var candidate in FindReducedCandidates(tiff, ifds, main, mainSize, targetLongEdge))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var frame = candidate.Layout is not null
                        ? DecodeLayout(tiff, candidate.Layout, maxEdge, resolvedOrientation, mainSize, method, cancellationToken)
                        : DecodeEmbeddedJpeg(tiff.File, candidate.JpegOffset, candidate.JpegLength, maxEdge, resolvedOrientation, mainSize, method, cancellationToken);
                    if (frame is not null)
                    {
                        error = string.Empty;
                        return frame;
                    }
                }
                catch (Exception ex) when (ex is ImageDecodeException or InvalidDataException or EndOfStreamException)
                {
                }
            }
        }

        var layout = TiffLayout.TryCreate(tiff, main, out var reason);
        if (layout is null)
        {
            error = reason;
            return null;
        }

        error = string.Empty;
        return DecodeLayout(tiff, layout, maxEdge, resolvedOrientation, mainSize, method, cancellationToken);
    }

    private static bool IsRawData(int photometric) => photometric is 32803 or 34892;

    private static TiffIfd? FindMainImage(TiffStructure tiff, List<TiffIfd> ifds, out Size size)
    {
        TiffIfd? best = null;
        size = default;
        var bestFull = false;
        long bestArea = 0;
        foreach (var ifd in ifds)
        {
            var candidate = TiffLayout.ReadSize(tiff, ifd);
            if (candidate is null
                || !(ifd.Has(TiffTag.StripOffsets) || ifd.Has(TiffTag.TileOffsets))
                || IsRawData(TiffLayout.ReadPhotometric(tiff, ifd)))
            {
                continue;
            }

            var full = (tiff.GetInt(ifd, TiffTag.NewSubfileType, 0) & 1) == 0;
            var area = (long)candidate.Value.Width * candidate.Value.Height;
            if (best is null || (full && !bestFull) || (full == bestFull && area > bestArea))
            {
                best = ifd;
                size = candidate.Value;
                bestFull = full;
                bestArea = area;
            }
        }

        return best;
    }

    private static List<ReducedCandidate> FindReducedCandidates(TiffStructure tiff, List<TiffIfd> ifds, TiffIfd main, Size mainSize, int targetLongEdge)
    {
        var candidates = new List<ReducedCandidate>();
        foreach (var ifd in ifds)
        {
            if (!ReferenceEquals(ifd, main))
            {
                var size = TiffLayout.ReadSize(tiff, ifd);
                if (size is { } reduced
                    && reduced.LongEdge >= targetLongEdge
                    && (long)reduced.Width * reduced.Height < (long)mainSize.Width * mainSize.Height
                    && Geometry.SameAspect(reduced, mainSize, 0.03)
                    && !IsRawData(TiffLayout.ReadPhotometric(tiff, ifd))
                    && TiffLayout.TryCreate(tiff, ifd, out _) is { } layout)
                {
                    candidates.Add(new ReducedCandidate(reduced, layout, 0, 0));
                }
            }

            var jpegOffset = tiff.GetValue(ifd, TiffTag.JpegInterchangeFormat);
            var jpegLength = tiff.GetValue(ifd, TiffTag.JpegInterchangeFormatLength);
            if (jpegOffset is > 0 && jpegLength is > 0)
            {
                var offset = tiff.BaseOffset + jpegOffset.Value;
                if (JpegProbe.Probe(tiff.File, offset, jpegLength.Value) is { } info
                    && info.Size.LongEdge >= targetLongEdge
                    && Geometry.SameAspect(info.Size, mainSize, 0.03))
                {
                    candidates.Add(new ReducedCandidate(info.Size, null, offset, jpegLength.Value));
                }
            }
        }

        candidates.Sort(static (a, b) => ((long)a.Size.Width * a.Size.Height).CompareTo((long)b.Size.Width * b.Size.Height));
        return candidates;
    }

    private static DecodedFrame? DecodeEmbeddedJpeg(SourceFile file, long offset, long length, int maxEdge, int orientation, Size source, string method, CancellationToken cancellationToken)
    {
        if (offset < 0 || length <= 0 || offset >= file.Length || length > MaxJpegChunkBytes)
        {
            return null;
        }

        length = Math.Min(length, file.Length - offset);
        var buffer = file.ReadRange(offset, length);
        try
        {
            return SkiaDecoder.TryDecode(buffer, 0, (int)length, new DecodeSettings(maxEdge, orientation, method, source), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DecodedFrame DecodeLayout(TiffStructure tiff, TiffLayout layout, int maxEdge, int orientation, Size source, string method, CancellationToken cancellationToken)
    {
        if (layout.IsJpeg && layout.ChunksAcross == 1 && layout.ChunksDown == 1)
        {
            var single = DecodeSingleJpegChunk(tiff, layout, maxEdge, orientation, source, method, cancellationToken);
            if (single is not null)
            {
                return single;
            }
        }

        var target = Geometry.Target(layout.Size, maxEdge);
        var outputLength = Geometry.RgbLength(target);
        var output = ArrayPool<byte>.Shared.Rent(outputLength);
        try
        {
            var writer = new OrientedWriter(output, target.Width, target.Height, orientation);
            using (var resampler = new AreaResampler(layout.Width, layout.Height, writer))
            {
                if (layout.IsJpeg)
                {
                    DecodeJpegBands(tiff, layout, resampler, cancellationToken);
                }
                else if (layout.Tiled)
                {
                    DecodeTiles(tiff, layout, resampler, cancellationToken);
                }
                else
                {
                    DecodeStrips(tiff, layout, resampler, cancellationToken);
                }

                if (!resampler.IsComplete)
                {
                    throw new ImageDecodeException("truncated TIFF image data");
                }
            }

            var displayed = Geometry.Displayed(target, orientation);
            var full = Geometry.Displayed(source, orientation);
            var frame = new Frame(displayed.Width, displayed.Height, output.AsMemory(0, outputLength), full.Width, full.Height);
            return new DecodedFrame(frame, method, output);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    private static void ValidateChunk(SourceFile file, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset >= file.Length)
        {
            throw new ImageDecodeException("TIFF image data is out of range");
        }
    }

    private static void DecodeStrips(TiffStructure tiff, TiffLayout layout, AreaResampler resampler, CancellationToken cancellationToken)
    {
        var planes = layout.Planes;
        var rowBytes = layout.ChunkRowBytes;
        var converter = new TiffRowConverter(layout, tiff.LittleEndian);
        var rows = new byte[planes][];
        var decompressors = new ChunkDecompressor?[planes];
        try
        {
            for (var p = 0; p < planes; p++)
            {
                rows[p] = ArrayPool<byte>.Shared.Rent(rowBytes);
            }

            for (var y = 0; y < layout.Height; y++)
            {
                if (y % layout.ChunkHeight == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var strip = y / layout.ChunkHeight;
                    for (var p = 0; p < planes; p++)
                    {
                        decompressors[p]?.Dispose();
                        decompressors[p] = null;
                        var index = layout.ChunkIndex(p, strip, 0);
                        var offset = layout.Offsets[index];
                        var length = layout.ByteCounts[index];
                        ValidateChunk(tiff.File, offset, length);
                        decompressors[p] = ChunkDecompressor.Create(layout.Compression, new ChunkInput(tiff.File, offset, length, StripInputBuffer));
                    }
                }
                else if ((y & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                for (var p = 0; p < planes; p++)
                {
                    var row = rows[p].AsSpan(0, rowBytes);
                    decompressors[p]!.ReadExactly(row);
                    if (layout.Predictor == 2)
                    {
                        HorizontalPredictor.Undo(row, layout.BitsPerSample, layout.SamplesPerChunkPixel, tiff.LittleEndian);
                    }
                }

                converter.Push(rows, 0, resampler);
            }
        }
        finally
        {
            foreach (var decompressor in decompressors)
            {
                decompressor?.Dispose();
            }

            foreach (var row in rows)
            {
                if (row is not null)
                {
                    ArrayPool<byte>.Shared.Return(row);
                }
            }

            converter.Dispose();
        }
    }

    private static void DecodeTiles(TiffStructure tiff, TiffLayout layout, AreaResampler resampler, CancellationToken cancellationToken)
    {
        var planes = layout.Planes;
        var tileRowBytes = layout.ChunkRowBytes;
        var planeRowBytes = checked((int)(((long)layout.Width * layout.SamplesPerChunkPixel * layout.BitsPerSample + 7) / 8));
        var bandBytes = checked(planeRowBytes * layout.ChunkHeight);
        var converter = new TiffRowConverter(layout, tiff.LittleEndian);
        var bands = new byte[planes][];
        var tile = ArrayPool<byte>.Shared.Rent(tileRowBytes);
        try
        {
            for (var p = 0; p < planes; p++)
            {
                bands[p] = ArrayPool<byte>.Shared.Rent(bandBytes);
            }

            for (var ty = 0; ty < layout.ChunksDown; ty++)
            {
                var bandRows = Math.Min(layout.ChunkHeight, layout.Height - (ty * layout.ChunkHeight));
                for (var p = 0; p < planes; p++)
                {
                    for (var tx = 0; tx < layout.ChunksAcross; tx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var index = layout.ChunkIndex(p, ty, tx);
                        var offset = layout.Offsets[index];
                        var length = layout.ByteCounts[index];
                        ValidateChunk(tiff.File, offset, length);
                        var destinationStart = tx * tileRowBytes;
                        var copy = Math.Min(tileRowBytes, planeRowBytes - destinationStart);
                        using var decompressor = ChunkDecompressor.Create(layout.Compression, new ChunkInput(tiff.File, offset, length, TileInputBuffer));
                        for (var r = 0; r < bandRows; r++)
                        {
                            var row = tile.AsSpan(0, tileRowBytes);
                            decompressor.ReadExactly(row);
                            if (layout.Predictor == 2)
                            {
                                HorizontalPredictor.Undo(row, layout.BitsPerSample, layout.SamplesPerChunkPixel, tiff.LittleEndian);
                            }

                            row[..copy].CopyTo(bands[p].AsSpan((r * planeRowBytes) + destinationStart, copy));
                        }
                    }
                }

                for (var r = 0; r < bandRows; r++)
                {
                    converter.Push(bands, r * planeRowBytes, resampler);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tile);
            foreach (var band in bands)
            {
                if (band is not null)
                {
                    ArrayPool<byte>.Shared.Return(band);
                }
            }

            converter.Dispose();
        }
    }

    private static DecodedFrame? DecodeSingleJpegChunk(TiffStructure tiff, TiffLayout layout, int maxEdge, int orientation, Size source, string method, CancellationToken cancellationToken)
    {
        var buffer = BuildJpegChunk(tiff.File, layout, 0, out var length);
        try
        {
            if (JpegProbe.Probe(buffer.AsSpan(0, length)) is not { } info || info.Size != layout.Size)
            {
                return null;
            }

            return SkiaDecoder.TryDecode(buffer, 0, length, new DecodeSettings(maxEdge, orientation, method, source), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void DecodeJpegBands(TiffStructure tiff, TiffLayout layout, AreaResampler resampler, CancellationToken cancellationToken)
    {
        var bandRowBytes = checked(layout.Width * 3);
        var band = ArrayPool<byte>.Shared.Rent(checked(bandRowBytes * layout.ChunkHeight));
        try
        {
            for (var ty = 0; ty < layout.ChunksDown; ty++)
            {
                var bandRows = Math.Min(layout.ChunkHeight, layout.Height - (ty * layout.ChunkHeight));
                for (var tx = 0; tx < layout.ChunksAcross; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var compressed = BuildJpegChunk(tiff.File, layout, layout.ChunkIndex(0, ty, tx), out var length);
                    byte[] pixels;
                    int width;
                    int height;
                    try
                    {
                        pixels = SkiaDecoder.DecodeRgba(compressed, length, out width, out height);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(compressed);
                    }

                    try
                    {
                        var x0 = tx * layout.ChunkWidth;
                        var columns = Math.Min(Math.Min(width, layout.ChunkWidth), layout.Width - x0);
                        var rows = Math.Min(height, bandRows);
                        if (rows < bandRows)
                        {
                            throw new ImageDecodeException("JPEG-in-TIFF chunk is smaller than expected");
                        }

                        for (var r = 0; r < rows; r++)
                        {
                            var source = pixels.AsSpan(r * width * 4, columns * 4);
                            var destination = band.AsSpan((r * bandRowBytes) + (x0 * 3), columns * 3);
                            for (int s = 0, d = 0; d < destination.Length; s += 4, d += 3)
                            {
                                destination[d] = source[s];
                                destination[d + 1] = source[s + 1];
                                destination[d + 2] = source[s + 2];
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixels);
                    }
                }

                resampler.PushRows(band, bandRows, bandRowBytes, 3);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    private static byte[] BuildJpegChunk(SourceFile file, TiffLayout layout, int index, out int length)
    {
        var offset = layout.Offsets[index];
        var count = layout.ByteCounts[index];
        ValidateChunk(file, offset, count);
        count = Math.Min(count, file.Length - offset);
        if (count < 4 || count > MaxJpegChunkBytes)
        {
            throw new ImageDecodeException("invalid JPEG-in-TIFF chunk");
        }

        var tables = layout.JpegTables;
        var prefix = 0;
        if (tables is { Length: >= 4 } && tables[0] == 0xFF && tables[1] == 0xD8)
        {
            prefix = tables[^2] == 0xFF && tables[^1] == 0xD9 ? tables.Length - 2 : tables.Length;
        }

        Span<byte> soi = stackalloc byte[2];
        file.ReadExactly(offset, soi);
        var skip = prefix > 0 && soi[0] == 0xFF && soi[1] == 0xD8 ? 2 : 0;
        length = checked(prefix + (int)count - skip);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            tables.AsSpan(0, prefix).CopyTo(buffer);
            file.ReadExactly(offset + skip, buffer.AsSpan(prefix, (int)count - skip));
            return buffer;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private readonly record struct ReducedCandidate(Size Size, TiffLayout? Layout, long JpegOffset, long JpegLength);
}

/// <summary>Undoes TIFF horizontal differencing (Predictor 2) on one decompressed row.</summary>
internal static class HorizontalPredictor
{
    public static void Undo(Span<byte> row, int bitsPerSample, int samplesPerPixel, bool littleEndian)
    {
        if (bitsPerSample == 8)
        {
            for (var i = samplesPerPixel; i < row.Length; i++)
            {
                row[i] += row[i - samplesPerPixel];
            }

            return;
        }

        if (bitsPerSample == 16)
        {
            var stride = samplesPerPixel * 2;
            for (var i = stride; i + 1 < row.Length; i += 2)
            {
                var span = row.Slice(i, 2);
                var previous = row.Slice(i - stride, 2);
                if (littleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(span) + BinaryPrimitives.ReadUInt16LittleEndian(previous)));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(span) + BinaryPrimitives.ReadUInt16BigEndian(previous)));
                }
            }
        }
    }
}

/// <summary>Converts decompressed TIFF rows (any supported sample layout) to RGB and feeds the resampler.</summary>
internal sealed class TiffRowConverter : IDisposable
{
    private readonly int width;
    private readonly int bitsPerSample;
    private readonly int samplesPerPixel;
    private readonly int photometric;
    private readonly bool planar;
    private readonly bool littleEndian;
    private readonly ushort[]? colorMap;
    private readonly int planeRowBytes;
    private byte[]? samples;
    private byte[]? rgb;

    public TiffRowConverter(TiffLayout layout, bool littleEndian)
    {
        width = layout.Width;
        bitsPerSample = layout.BitsPerSample;
        samplesPerPixel = layout.SamplesPerPixel;
        photometric = layout.Photometric;
        planar = layout.Planar;
        this.littleEndian = littleEndian;
        colorMap = layout.ColorMap;
        planeRowBytes = checked((int)(((long)width * layout.SamplesPerChunkPixel * bitsPerSample + 7) / 8));
        samples = ArrayPool<byte>.Shared.Rent(checked(width * samplesPerPixel));
        rgb = ArrayPool<byte>.Shared.Rent(checked(width * 3));
    }

    public void Push(byte[][] planeRows, int offset, AreaResampler resampler)
    {
        var interleaved = samples!;
        if (!planar && bitsPerSample == 8 && photometric == 2 && samplesPerPixel is 3 or 4)
        {
            resampler.PushRow(planeRows[0].AsSpan(offset, planeRowBytes), samplesPerPixel);
            return;
        }

        var scale = photometric != 3;
        if (planar)
        {
            for (var p = 0; p < samplesPerPixel; p++)
            {
                ToBytes(planeRows[p].AsSpan(offset, planeRowBytes), interleaved, p, samplesPerPixel, width, scale);
            }
        }
        else
        {
            ToBytes(planeRows[0].AsSpan(offset, planeRowBytes), interleaved, 0, 1, width * samplesPerPixel, scale);
        }

        if (photometric == 2 && samplesPerPixel is 3 or 4)
        {
            resampler.PushRow(interleaved.AsSpan(0, width * samplesPerPixel), samplesPerPixel);
            return;
        }

        var output = rgb.AsSpan(0, width * 3);
        var spp = samplesPerPixel;
        switch (photometric)
        {
            case 0:
            case 1:
                var invert = photometric == 0 ? (byte)255 : (byte)0;
                for (int i = 0, s = 0; i < output.Length; i += 3, s += spp)
                {
                    var v = (byte)(interleaved[s] ^ invert);
                    output[i] = v;
                    output[i + 1] = v;
                    output[i + 2] = v;
                }

                break;
            case 2:
                for (int i = 0, s = 0; i < output.Length; i += 3, s += spp)
                {
                    output[i] = interleaved[s];
                    output[i + 1] = interleaved[s + 1];
                    output[i + 2] = interleaved[s + 2];
                }

                break;
            case 3:
                var map = colorMap!;
                var entries = 1 << bitsPerSample;
                for (int i = 0, s = 0; i < output.Length; i += 3, s += spp)
                {
                    var index = interleaved[s];
                    output[i] = (byte)(map[index] >> 8);
                    output[i + 1] = (byte)(map[entries + index] >> 8);
                    output[i + 2] = (byte)(map[(2 * entries) + index] >> 8);
                }

                break;
            default:
                for (int i = 0, s = 0; i < output.Length; i += 3, s += spp)
                {
                    var k = 255 - interleaved[s + 3];
                    output[i] = (byte)((((255 - interleaved[s]) * k) + 127) / 255);
                    output[i + 1] = (byte)((((255 - interleaved[s + 1]) * k) + 127) / 255);
                    output[i + 2] = (byte)((((255 - interleaved[s + 2]) * k) + 127) / 255);
                }

                break;
        }

        resampler.PushRow(output, 3);
    }

    public void Dispose()
    {
        if (samples is not null)
        {
            ArrayPool<byte>.Shared.Return(samples);
            samples = null;
        }

        if (rgb is not null)
        {
            ArrayPool<byte>.Shared.Return(rgb);
            rgb = null;
        }
    }

    private void ToBytes(ReadOnlySpan<byte> source, byte[] destination, int start, int stride, int count, bool scale)
    {
        switch (bitsPerSample)
        {
            case 8:
                if (stride == 1)
                {
                    source[..count].CopyTo(destination.AsSpan(start, count));
                    return;
                }

                for (int i = 0, d = start; i < count; i++, d += stride)
                {
                    destination[d] = source[i];
                }

                return;
            case 16:
                for (int i = 0, s = 0, d = start; i < count; i++, s += 2, d += stride)
                {
                    int v = littleEndian ? source[s] | (source[s + 1] << 8) : (source[s] << 8) | source[s + 1];
                    destination[d] = (byte)(((v * 255) + 32895) >> 16);
                }

                return;
            default:
                var bits = bitsPerSample;
                var mask = (1 << bits) - 1;
                var factor = scale ? 255 / mask : 1;
                for (int i = 0, bit = 0, d = start; i < count; i++, bit += bits, d += stride)
                {
                    var value = (source[bit >> 3] >> (8 - bits - (bit & 7))) & mask;
                    destination[d] = (byte)(value * factor);
                }

                return;
        }
    }
}
