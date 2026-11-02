using System.Buffers;
using System.Buffers.Binary;

namespace PhotoClippy.Imaging;

internal readonly record struct PreviewCandidate(long Offset, long Length, Size Size);

/// <summary>Finds and decodes the embedded JPEG previews of camera RAW files (no demosaicing).</summary>
internal static class RawPreviewExtractor
{
    public const string Method = "raw-preview";

    private const long MaxPreviewBytes = 64L * 1024 * 1024;
    private const int ScanLimit = 16 * 1024 * 1024;
    private const int MaxScanCandidates = 64;
    private const int MaxAttemptsPerPass = 3;
    private const int ScanThresholdEdge = 1024;
    private const double AspectTolerance = 0.03;
    private const ushort ExifPixelXDimension = 0xA002;
    private const ushort ExifPixelYDimension = 0xA003;

    public static DecodedFrame Decode(SourceFile file, int maxEdge, int? orientation, CancellationToken cancellationToken)
    {
        var state = new ExtractionState();
        TiffStructure? tiff = null;
        var head = file.Head;
        if (TiffStructure.LooksLikeTiff(head))
        {
            tiff = TiffStructure.TryOpen(file, 0);
            if (tiff is not null)
            {
                CollectTiff(tiff, state);
            }
        }
        else if (RafLayout.IsRaf(head))
        {
            RafLayout.Collect(file, state);
        }
        else if (IsoBmff.IsIsoBmff(head))
        {
            Cr3Layout.Collect(file, state);
        }

        var effectiveOrientation = orientation ?? state.Orientation;
        var scanned = false;
        if (state.Candidates.Count == 0 || state.BestLongEdge < Math.Min(maxEdge, ScanThresholdEdge))
        {
            ScanForJpegs(file, state, cancellationToken);
            scanned = true;
        }

        var frame = TryCandidates(file, state, maxEdge, effectiveOrientation, Method, cancellationToken);
        if (frame is null && !scanned)
        {
            ScanForJpegs(file, state, cancellationToken);
            frame = TryCandidates(file, state, maxEdge, effectiveOrientation, Method, cancellationToken);
        }

        if (frame is not null)
        {
            return frame;
        }

        if (tiff is not null)
        {
            var fallback = TiffImageDecoder.TryDecode(tiff, maxEdge, effectiveOrientation, Method, cancellationToken, out _);
            if (fallback is not null)
            {
                return fallback;
            }
        }

        throw new ImageDecodeException("no decodable embedded preview found");
    }

    /// <summary>Decodes the best JPEG referenced by the TIFF structure (no byte scanning); null when there is none.</summary>
    public static DecodedFrame? TryDecodeTiffJpeg(TiffStructure tiff, int maxEdge, int? orientation, string method, CancellationToken cancellationToken)
    {
        var state = new ExtractionState();
        CollectTiff(tiff, state);
        return TryCandidates(tiff.File, state, maxEdge, orientation ?? state.Orientation, method, cancellationToken);
    }

    internal static void AddCandidate(SourceFile file, ExtractionState state, long offset, long length)
    {
        if (offset <= 0 || length < 128 || offset >= file.Length || state.Offsets.Contains(offset))
        {
            return;
        }

        length = Math.Min(length, file.Length - offset);
        if (JpegProbe.Probe(file, offset, length) is { } info)
        {
            state.Add(new PreviewCandidate(offset, length, info.Size));
        }
    }

    private static void CollectTiff(TiffStructure tiff, ExtractionState state)
    {
        var file = tiff.File;
        if (tiff.ReadIfd(tiff.FirstIfdOffset) is { } ifd0)
        {
            var orientation = tiff.GetInt(ifd0, TiffTag.Orientation, 0);
            if (orientation is >= 1 and <= 8)
            {
                state.Orientation = orientation;
            }
        }

        foreach (var ifd in tiff.CollectIfds(includeSubIfds: true, includeExif: true))
        {
            var jpegOffset = tiff.GetValue(ifd, TiffTag.JpegInterchangeFormat);
            var jpegLength = tiff.GetValue(ifd, TiffTag.JpegInterchangeFormatLength);
            if (jpegOffset > 0 && jpegLength > 0)
            {
                AddCandidate(file, state, tiff.BaseOffset + jpegOffset.Value, jpegLength.Value);
            }

            if (ifd.TryGet(TiffTag.PanasonicJpgFromRaw, out var jpgFromRaw) && jpgFromRaw.Type is 1 or 7)
            {
                AddCandidate(file, state, jpgFromRaw.ValuePosition, jpgFromRaw.Count);
            }

            if (tiff.GetInt(ifd, TiffTag.Compression, 0) is 6 or 7
                && tiff.GetValues(ifd, TiffTag.StripOffsets, 1) is [var stripOffset]
                && tiff.GetValues(ifd, TiffTag.StripByteCounts, 1) is [var stripLength])
            {
                AddCandidate(file, state, tiff.BaseOffset + stripOffset, stripLength);
            }

            state.AddFullSize(tiff.GetValue(ifd, TiffTag.ImageWidth), tiff.GetValue(ifd, TiffTag.ImageLength));
            state.AddFullSize(tiff.GetValue(ifd, ExifPixelXDimension), tiff.GetValue(ifd, ExifPixelYDimension));
        }
    }

    private static DecodedFrame? TryCandidates(
        SourceFile file,
        ExtractionState state,
        int maxEdge,
        int? orientation,
        string method,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        foreach (var candidate in Order(state.Candidates, maxEdge))
        {
            if (!state.Tried.Add(candidate.Offset))
            {
                continue;
            }

            if (attempts++ >= MaxAttemptsPerPass)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(Math.Min(candidate.Length, file.Length - candidate.Offset), MaxPreviewBytes);
            byte[] buffer;
            try
            {
                buffer = file.ReadRange(candidate.Offset, length);
            }
            catch (ImageDecodeException)
            {
                continue;
            }

            try
            {
                var settings = new DecodeSettings(maxEdge, orientation, method, state.FullSizeFor(candidate.Size));
                var frame = SkiaDecoder.TryDecode(buffer, 0, (int)length, settings, cancellationToken);
                if (frame is not null)
                {
                    return frame;
                }
            }
            catch (ImageDecodeException)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>Smallest same-aspect preview that covers <paramref name="maxEdge"/>, then larger-to-smaller fallbacks.</summary>
    private static List<PreviewCandidate> Order(List<PreviewCandidate> candidates, int maxEdge)
    {
        var ordered = new List<PreviewCandidate>(candidates.Count);
        if (candidates.Count == 0)
        {
            return ordered;
        }

        var largest = candidates[0];
        foreach (var candidate in candidates)
        {
            if (Area(candidate.Size) > Area(largest.Size))
            {
                largest = candidate;
            }
        }

        var sufficient = new List<PreviewCandidate>();
        var insufficient = new List<PreviewCandidate>();
        var others = new List<PreviewCandidate>();
        foreach (var candidate in candidates)
        {
            if (!Geometry.SameAspect(candidate.Size, largest.Size, AspectTolerance))
            {
                others.Add(candidate);
            }
            else if (candidate.Size.LongEdge >= maxEdge)
            {
                sufficient.Add(candidate);
            }
            else
            {
                insufficient.Add(candidate);
            }
        }

        sufficient.Sort(static (a, b) => Area(a.Size).CompareTo(Area(b.Size)));
        insufficient.Sort(static (a, b) => Area(b.Size).CompareTo(Area(a.Size)));
        others.Sort(static (a, b) => Area(b.Size).CompareTo(Area(a.Size)));
        ordered.AddRange(sufficient);
        ordered.AddRange(insufficient);
        ordered.AddRange(others);
        return ordered;
    }

    private static long Area(Size size) => (long)size.Width * size.Height;

    /// <summary>Generic fallback: looks for JPEG SOI markers in the first 16 MB and probes each hit.</summary>
    private static void ScanForJpegs(SourceFile file, ExtractionState state, CancellationToken cancellationToken)
    {
        var windowLength = (int)Math.Min(file.Length, ScanLimit);
        if (windowLength < 4)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var window = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            windowLength = file.Read(0, window.AsSpan(0, windowLength));
            var data = window.AsSpan(0, windowLength);
            ReadOnlySpan<byte> soi = [0xFF, 0xD8, 0xFF];
            var position = 0;
            var found = 0;
            while (position < windowLength - 3 && found < MaxScanCandidates)
            {
                var index = data[position..].IndexOf(soi);
                if (index < 0)
                {
                    break;
                }

                position += index;
                var info = JpegProbe.Probe(data[position..]);
                if (info is null)
                {
                    position += 3;
                    continue;
                }

                var end = JpegProbe.FindEnd(data, position);
                long length = end > 0 ? end - position : Math.Min(MaxPreviewBytes, file.Length - position);
                if (!state.Offsets.Contains(position))
                {
                    state.Add(new PreviewCandidate(position, length, info.Value.Size));
                    found++;
                }

                position = end > 0 ? end : position + 3;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    internal static uint ReadU32BigEndian(SourceFile file, long offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        return file.Read(offset, bytes) == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0;
    }
}

internal sealed class ExtractionState
{
    private readonly List<Size> fullSizes = [];

    public List<PreviewCandidate> Candidates { get; } = [];

    public HashSet<long> Offsets { get; } = [];

    public HashSet<long> Tried { get; } = [];

    public int? Orientation { get; set; }

    public int BestLongEdge { get; private set; }

    public void Add(PreviewCandidate candidate)
    {
        if (Offsets.Add(candidate.Offset))
        {
            Candidates.Add(candidate);
            BestLongEdge = Math.Max(BestLongEdge, candidate.Size.LongEdge);
        }
    }

    public void AddFullSize(long? width, long? height)
    {
        if (width is > 0 and <= 1 << 18 && height is > 0 and <= 1 << 18)
        {
            fullSizes.Add(new Size((int)width.Value, (int)height.Value));
        }
    }

    /// <summary>Largest known full image size with the preview's aspect ratio, if larger than the preview itself.</summary>
    public Size? FullSizeFor(Size preview)
    {
        Size? best = null;
        var bestArea = (long)preview.Width * preview.Height;
        foreach (var size in fullSizes)
        {
            var area = (long)size.Width * size.Height;
            if (area > bestArea && Geometry.SameAspect(size, preview, 0.03))
            {
                best = size;
                bestArea = area;
            }
        }

        if (best is null)
        {
            foreach (var candidate in Candidates)
            {
                var area = (long)candidate.Size.Width * candidate.Size.Height;
                if (area > bestArea && Geometry.SameAspect(candidate.Size, preview, 0.03))
                {
                    best = candidate.Size;
                    bestArea = area;
                }
            }
        }

        return best;
    }
}

/// <summary>Fujifilm RAF: "FUJIFILMCCD-RAW" header with a big-endian JPEG offset/length at bytes 84/88.</summary>
internal static class RafLayout
{
    public static bool IsRaf(ReadOnlySpan<byte> head) => head.StartsWith("FUJIFILMCCD-RAW"u8);

    public static void Collect(SourceFile file, ExtractionState state)
    {
        var offset = RawPreviewExtractor.ReadU32BigEndian(file, 84);
        var length = RawPreviewExtractor.ReadU32BigEndian(file, 88);
        RawPreviewExtractor.AddCandidate(file, state, offset, length);
    }
}

internal readonly record struct BmffBox(uint Type, long Start, long PayloadStart, long End);

internal static class IsoBmff
{
    public const uint Ftyp = 0x66747970;

    public static bool IsIsoBmff(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(head[4..]) == Ftyp;

    public static string? MajorBrand(ReadOnlySpan<byte> head)
    {
        if (!IsIsoBmff(head))
        {
            return null;
        }

        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            chars[i] = (char)head[8 + i];
        }

        return new string(chars);
    }

    public static uint FourCc(string text) =>
        ((uint)text[0] << 24) | ((uint)text[1] << 16) | ((uint)text[2] << 8) | text[3];

    /// <summary>Enumerates the boxes in [start, end); stops at the first malformed header.</summary>
    public static List<BmffBox> Children(SourceFile file, long start, long end)
    {
        var boxes = new List<BmffBox>();
        Span<byte> header = stackalloc byte[16];
        var position = start;
        end = Math.Min(end, file.Length);
        while (position + 8 <= end && boxes.Count < 1024)
        {
            if (file.Read(position, header[..8]) != 8)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var payload = position + 8;
            if (size == 1)
            {
                if (file.Read(position + 8, header[8..16]) != 8)
                {
                    break;
                }

                var large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (large > long.MaxValue)
                {
                    break;
                }

                size = (long)large;
                payload += 8;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < payload - position || position + size > end)
            {
                break;
            }

            boxes.Add(new BmffBox(type, position, payload, position + size));
            position += size;
        }

        return boxes;
    }

    public static bool HasUuid(SourceFile file, BmffBox box, ReadOnlySpan<byte> uuid)
    {
        Span<byte> bytes = stackalloc byte[16];
        return box.End - box.PayloadStart >= 16 && file.Read(box.PayloadStart, bytes) == 16 && bytes.SequenceEqual(uuid);
    }
}

/// <summary>Canon CR3: PRVW inside uuid eaf42b5e... (JPEG ~72 bytes into the payload), THMB and CMT1 (moov/uuid 85c0b687...), and the full-size JPEG of the first track.</summary>
internal static class Cr3Layout
{
    private static readonly uint Moov = IsoBmff.FourCc("moov");
    private static readonly uint Uuid = IsoBmff.FourCc("uuid");
    private static readonly uint Trak = IsoBmff.FourCc("trak");
    private static readonly uint Mdia = IsoBmff.FourCc("mdia");
    private static readonly uint Minf = IsoBmff.FourCc("minf");
    private static readonly uint Stbl = IsoBmff.FourCc("stbl");
    private static readonly uint Stsz = IsoBmff.FourCc("stsz");
    private static readonly uint Stco = IsoBmff.FourCc("stco");
    private static readonly uint Co64 = IsoBmff.FourCc("co64");
    private static readonly uint Thmb = IsoBmff.FourCc("THMB");
    private static readonly uint Cmt1 = IsoBmff.FourCc("CMT1");

    private static ReadOnlySpan<byte> CanonUuid =>
        [0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48];

    private static ReadOnlySpan<byte> PreviewUuid =>
        [0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16];

    public static void Collect(SourceFile file, ExtractionState state)
    {
        foreach (var box in IsoBmff.Children(file, 0, file.Length))
        {
            if (box.Type == Moov)
            {
                CollectMoov(file, box, state);
            }
            else if (box.Type == Uuid && IsoBmff.HasUuid(file, box, PreviewUuid))
            {
                AddEmbeddedJpeg(file, box, state);
            }
        }
    }

    private static void CollectMoov(SourceFile file, BmffBox moov, ExtractionState state)
    {
        foreach (var box in IsoBmff.Children(file, moov.PayloadStart, moov.End))
        {
            if (box.Type == Uuid && IsoBmff.HasUuid(file, box, CanonUuid))
            {
                foreach (var child in IsoBmff.Children(file, box.PayloadStart + 16, box.End))
                {
                    if (child.Type == Thmb)
                    {
                        AddEmbeddedJpeg(file, child, state);
                    }
                    else if (child.Type == Cmt1)
                    {
                        ReadOrientation(file, child, state);
                    }
                }
            }
            else if (box.Type == Trak)
            {
                CollectTrack(file, box, state, 0);
            }
        }
    }

    private static void CollectTrack(SourceFile file, BmffBox container, ExtractionState state, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        foreach (var box in IsoBmff.Children(file, container.PayloadStart, container.End))
        {
            if (box.Type == Mdia || box.Type == Minf)
            {
                CollectTrack(file, box, state, depth + 1);
            }
            else if (box.Type == Stbl)
            {
                CollectSampleTable(file, box, state);
            }
        }
    }

    private static void CollectSampleTable(SourceFile file, BmffBox stbl, ExtractionState state)
    {
        long sampleSize = 0;
        long chunkOffset = 0;
        Span<byte> bytes = stackalloc byte[16];
        foreach (var box in IsoBmff.Children(file, stbl.PayloadStart, stbl.End))
        {
            if (box.Type == Stsz && file.Read(box.PayloadStart, bytes) == 16)
            {
                var fixedSize = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
                sampleSize = fixedSize != 0 ? fixedSize : BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
            }
            else if (box.Type == Co64 && file.Read(box.PayloadStart, bytes) == 16)
            {
                chunkOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]), long.MaxValue);
            }
            else if (box.Type == Stco && file.Read(box.PayloadStart, bytes[..12]) == 12)
            {
                chunkOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            }
        }

        if (sampleSize > 0 && chunkOffset > 0)
        {
            RawPreviewExtractor.AddCandidate(file, state, chunkOffset, sampleSize);
        }
    }

    private static void AddEmbeddedJpeg(SourceFile file, BmffBox box, ExtractionState state)
    {
        Span<byte> payload = stackalloc byte[128];
        var read = file.Read(box.PayloadStart, payload);
        ReadOnlySpan<byte> soi = [0xFF, 0xD8, 0xFF];
        var index = payload[..read].IndexOf(soi);
        if (index >= 0)
        {
            var start = box.PayloadStart + index;
            RawPreviewExtractor.AddCandidate(file, state, start, box.End - start);
        }
    }

    private static void ReadOrientation(SourceFile file, BmffBox cmt1, ExtractionState state)
    {
        var tiff = TiffStructure.TryOpen(file, cmt1.PayloadStart);
        if (tiff?.ReadIfd(tiff.FirstIfdOffset) is not { } ifd0)
        {
            return;
        }

        var orientation = tiff.GetInt(ifd0, TiffTag.Orientation, 0);
        if (orientation is >= 1 and <= 8)
        {
            state.Orientation = orientation;
        }

        state.AddFullSize(tiff.GetValue(ifd0, TiffTag.ImageWidth), tiff.GetValue(ifd0, TiffTag.ImageLength));
    }
}
