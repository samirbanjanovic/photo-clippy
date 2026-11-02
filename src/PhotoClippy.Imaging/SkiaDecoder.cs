using System.Buffers;
using System.Runtime.InteropServices;
using PhotoClippy.Contracts;
using SkiaSharp;

namespace PhotoClippy.Imaging;

internal readonly record struct DecodeSettings(
    int MaxEdge,
    int? Orientation,
    string? Method,
    Size? SourceOverride = null);

/// <summary>Skia-backed decoding (JPEG with DCT scaling, PNG, WebP, GIF, BMP, ...).</summary>
internal static class SkiaDecoder
{
    private const int BatchRows = 16;
    private const long MaxFullDecodeBytes = 1L << 30;
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>Returns null when Skia does not recognise the data.</summary>
    public static DecodedFrame? TryDecode(byte[] buffer, int offset, int length, DecodeSettings settings, CancellationToken cancellationToken)
    {
        if (length <= 0 || offset < 0 || offset + length > buffer.Length)
        {
            return null;
        }

        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var data = SKData.Create(pin.AddrOfPinnedObject() + offset, length);
            if (data is null)
            {
                return null;
            }

            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            return Decode(codec, settings, cancellationToken);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Fully decodes the data into a pooled, tightly packed RGBA buffer (caller returns it to the pool).</summary>
    public static byte[] DecodeRgba(byte[] buffer, int length, out int width, out int height)
    {
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var data = SKData.Create(pin.AddrOfPinnedObject(), length);
            using var codec = data is null ? null : SKCodec.Create(data);
            if (codec is null)
            {
                throw new ImageDecodeException("corrupt embedded JPEG data");
            }

            width = codec.Info.Width;
            height = codec.Info.Height;
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb);
            var total = (long)width * height * 4;
            if (width <= 0 || height <= 0 || total > MaxFullDecodeBytes)
            {
                throw new ImageDecodeException("invalid embedded JPEG dimensions");
            }

            var pixels = ArrayPool<byte>.Shared.Rent((int)total);
            var pixelPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var succeeded = false;
            try
            {
                var result = codec.GetPixels(info, pixelPin.AddrOfPinnedObject());
                if (result != SKCodecResult.Success)
                {
                    throw new ImageDecodeException("corrupt embedded JPEG data: " + result.ToString());
                }

                succeeded = true;
                return pixels;
            }
            finally
            {
                pixelPin.Free();
                if (!succeeded)
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        }
        finally
        {
            pin.Free();
        }
    }

    private static DecodedFrame Decode(SKCodec codec, DecodeSettings settings, CancellationToken cancellationToken)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ImageDecodeException("image has no pixels");
        }

        var stored = new Size(info.Width, info.Height);
        var orientation = settings.Orientation ?? OriginToOrientation(codec.EncodedOrigin) ?? 1;
        orientation = Geometry.NormalizeOrientation(orientation);
        var target = Geometry.Target(stored, settings.MaxEdge);
        var decodeSize = ChooseDecodeSize(codec, stored, target);
        target = new Size(Math.Min(target.Width, decodeSize.Width), Math.Min(target.Height, decodeSize.Height));

        var alpha = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, alpha, Srgb);
        var outputLength = Geometry.RgbLength(target);
        var output = ArrayPool<byte>.Shared.Rent(outputLength);
        try
        {
            var writer = new OrientedWriter(output, target.Width, target.Height, orientation);
            using (var resampler = new AreaResampler(decodeSize.Width, decodeSize.Height, writer))
            {
                if (!TryScanlineDecode(codec, decodeInfo, resampler, cancellationToken))
                {
                    FullDecode(codec, decodeInfo, resampler, cancellationToken);
                }

                if (!resampler.IsComplete)
                {
                    throw new ImageDecodeException("truncated image data");
                }
            }

            var source = Geometry.Displayed(settings.SourceOverride ?? stored, orientation);
            var displayed = Geometry.Displayed(target, orientation);
            var frame = new Frame(displayed.Width, displayed.Height, output.AsMemory(0, outputLength), source.Width, source.Height);
            var method = settings.Method ?? (codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? "jpeg" : "codec");
            return new DecodedFrame(frame, method, output);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    private static Size ChooseDecodeSize(SKCodec codec, Size stored, Size target)
    {
        if (target == stored)
        {
            return stored;
        }

        for (var k = 1; k < 8; k++)
        {
            var scaled = codec.GetScaledDimensions(k / 8f);
            if (scaled.Width >= target.Width && scaled.Height >= target.Height
                && scaled.Width <= stored.Width && scaled.Height <= stored.Height)
            {
                return new Size(scaled.Width, scaled.Height);
            }
        }

        return stored;
    }

    private static bool TryScanlineDecode(SKCodec codec, SKImageInfo info, AreaResampler resampler, CancellationToken cancellationToken)
    {
        if (codec.EncodedFormat is SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp)
        {
            return false;
        }

        var start = codec.StartScanlineDecode(info);
        if (start != SKCodecResult.Success || codec.ScanlineOrder != SKCodecScanlineOrder.TopDown)
        {
            return false;
        }

        var rowBytes = info.Width * 4;
        var batch = ArrayPool<byte>.Shared.Rent(rowBytes * BatchRows);
        var pin = GCHandle.Alloc(batch, GCHandleType.Pinned);
        try
        {
            var pointer = pin.AddrOfPinnedObject();
            var remaining = info.Height;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(BatchRows, remaining);
                var decoded = codec.GetScanlines(pointer, count, rowBytes);
                if (decoded < count)
                {
                    throw new ImageDecodeException("truncated or corrupt image data");
                }

                resampler.PushRows(batch, count, rowBytes, 4);
                remaining -= count;
            }

            return true;
        }
        finally
        {
            pin.Free();
            ArrayPool<byte>.Shared.Return(batch);
        }
    }

    private static void FullDecode(SKCodec codec, SKImageInfo info, AreaResampler resampler, CancellationToken cancellationToken)
    {
        var rowBytes = info.Width * 4;
        var total = (long)rowBytes * info.Height;
        if (total > MaxFullDecodeBytes)
        {
            throw new ImageDecodeException("image is too large");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pixels = ArrayPool<byte>.Shared.Rent((int)total);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, pin.AddrOfPinnedObject());
            if (result != SKCodecResult.Success)
            {
                throw new ImageDecodeException(result == SKCodecResult.IncompleteInput
                    ? "truncated image data"
                    : "decode failed: " + result.ToString());
            }

            cancellationToken.ThrowIfCancellationRequested();
            resampler.PushRows(pixels, info.Height, rowBytes, 4);
        }
        finally
        {
            pin.Free();
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static int? OriginToOrientation(SKEncodedOrigin origin)
    {
        var value = (int)origin;
        return value is >= 1 and <= 8 ? value : null;
    }
}
