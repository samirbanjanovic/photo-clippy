using System.Buffers;

namespace PhotoClippy.Imaging;

/// <summary>Routes a file to the right decoder by sniffing its header (extensions are only hints).</summary>
internal static class DecodePipeline
{
    private static readonly HashSet<string> HeifBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "mif2", "msf1", "avif", "avis",
    };

    public static DecodedFrame Run(string path, string extension, int maxEdge, int? orientation, CancellationToken cancellationToken)
    {
        var isRaw = FormatCatalog.IsRaw(extension);
        var isTiff = FormatCatalog.IsTiff(extension);
        using var file = SourceFile.Open(path, randomAccess: isRaw || isTiff);
        if (file.Length == 0)
        {
            throw new ImageDecodeException("empty file");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var head = file.Head;
        if (JpegProbe.StartsWithSoi(head))
        {
            return DecodeWhole(file, maxEdge, orientation, cancellationToken)
                ?? throw new ImageDecodeException("corrupt JPEG data");
        }

        if (RafLayout.IsRaf(head))
        {
            return RawPreviewExtractor.Decode(file, maxEdge, orientation, cancellationToken);
        }

        if (TiffStructure.LooksLikeTiff(head))
        {
            return DecodeTiffContainer(file, isRaw, maxEdge, orientation, cancellationToken);
        }

        if (IsoBmff.MajorBrand(head) is { } brand)
        {
            return DecodeIsoBmff(file, brand, isRaw, maxEdge, orientation, cancellationToken);
        }

        if (isRaw)
        {
            return RawPreviewExtractor.Decode(file, maxEdge, orientation, cancellationToken);
        }

        var frame = DecodeWhole(file, maxEdge, orientation, cancellationToken);
        if (frame is not null)
        {
            return frame;
        }

        if (FormatCatalog.IsKnownImage(extension))
        {
            throw new ImageDecodeException("unrecognised or corrupt image data");
        }

        throw new UnsupportedImageException("unsupported file format");
    }

    private static DecodedFrame DecodeTiffContainer(SourceFile file, bool isRaw, int maxEdge, int? orientation, CancellationToken cancellationToken)
    {
        var tiff = TiffStructure.TryOpen(file, 0);
        if (isRaw || tiff is null || tiff.Magic is not (42 or 43))
        {
            return RawPreviewExtractor.Decode(file, maxEdge, orientation, cancellationToken);
        }

        var frame = TiffImageDecoder.TryDecode(tiff, maxEdge, orientation, TiffImageDecoder.Method, cancellationToken, out var error);
        if (frame is not null)
        {
            return frame;
        }

        return RawPreviewExtractor.TryDecodeTiffJpeg(tiff, maxEdge, orientation, TiffImageDecoder.Method, cancellationToken)
            ?? throw new ImageDecodeException(error);
    }

    private static DecodedFrame DecodeIsoBmff(SourceFile file, string brand, bool isRaw, int maxEdge, int? orientation, CancellationToken cancellationToken)
    {
        if (brand == "crx " || isRaw)
        {
            return RawPreviewExtractor.Decode(file, maxEdge, orientation, cancellationToken);
        }

        if (HeifBrands.Contains(brand))
        {
            return DecodeWhole(file, maxEdge, orientation, cancellationToken)
                ?? throw new UnsupportedImageException("HEIF/AVIF images are not supported on this platform");
        }

        throw new UnsupportedImageException("unsupported ISO-BMFF file (brand '" + brand.TrimEnd() + "')");
    }

    private static DecodedFrame? DecodeWhole(SourceFile file, int maxEdge, int? orientation, CancellationToken cancellationToken)
    {
        var buffer = file.ReadAll(out var length, cancellationToken);
        try
        {
            return SkiaDecoder.TryDecode(buffer, 0, length, new DecodeSettings(maxEdge, orientation, null), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
