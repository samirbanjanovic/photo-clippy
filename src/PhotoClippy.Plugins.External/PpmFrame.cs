using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>RGB24 pixels ready to be sent to a plugin, optionally downscaled into a pooled buffer.</summary>
internal sealed class PpmFrame : IDisposable
{
    private byte[]? _rented;

    private PpmFrame(int width, int height, int sourceWidth, int sourceHeight, ReadOnlyMemory<byte> rgb, byte[]? rented)
    {
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Rgb = rgb;
        _rented = rented;
    }

    public int Width { get; }
    public int Height { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public ReadOnlyMemory<byte> Rgb { get; }

    /// <summary>Wraps <paramref name="frame"/>; downsizes by area averaging when its long edge exceeds <paramref name="maxEdge"/>.</summary>
    public static PpmFrame From(Frame frame, int maxEdge)
    {
        var longEdge = Math.Max(frame.Width, frame.Height);
        if (maxEdge <= 0 || longEdge <= maxEdge)
        {
            return new PpmFrame(frame.Width, frame.Height, frame.SourceWidth, frame.SourceHeight, frame.Rgb, null);
        }

        var scale = (double)maxEdge / longEdge;
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var length = width * height * 3;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        Downscale(frame.Rgb.Span, frame.Width, frame.Height, rented.AsSpan(0, length), width, height);
        return new PpmFrame(width, height, frame.SourceWidth, frame.SourceHeight, rented.AsMemory(0, length), rented);
    }

    /// <summary>Writes a binary PPM (P6, maxval 255) without copying the pixels.</summary>
    public void WriteFile(string path)
    {
        Span<byte> header = stackalloc byte[32];
        var headerLength = Header(header);
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None, preallocationSize: headerLength + Rgb.Length);
        RandomAccess.Write(handle, header[..headerLength], 0);
        RandomAccess.Write(handle, Rgb.Span, headerLength);
    }

    /// <summary>Writes the protocol <c>frame</c> object; either references <paramref name="path"/> or inlines base64 PPM data.</summary>
    public void WriteJson(Utf8JsonWriter writer, string? path)
    {
        writer.WriteStartObject();
        writer.WriteNumber("width"u8, Width);
        writer.WriteNumber("height"u8, Height);
        writer.WriteNumber("sourceWidth"u8, SourceWidth);
        writer.WriteNumber("sourceHeight"u8, SourceHeight);
        writer.WriteString("encoding"u8, "ppm"u8);
        if (path is not null)
        {
            writer.WriteString("path"u8, path);
        }
        else
        {
            Span<byte> header = stackalloc byte[32];
            var headerLength = Header(header);
            var total = headerLength + Rgb.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                header[..headerLength].CopyTo(buffer);
                Rgb.Span.CopyTo(buffer.AsSpan(headerLength));
                writer.WriteBase64String("data"u8, buffer.AsSpan(0, total));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        writer.WriteEndObject();
    }

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private int Header(Span<byte> destination)
    {
        var ok = System.Text.Unicode.Utf8.TryWrite(destination, CultureInfo.InvariantCulture, $"P6\n{Width} {Height}\n255\n", out var written);
        return ok ? written : throw new InvalidOperationException("PPM header buffer too small.");
    }

    internal static void Downscale(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        var xStart = ArrayPool<int>.Shared.Rent(dstWidth + 1);
        var acc = ArrayPool<long>.Shared.Rent(dstWidth * 3);
        try
        {
            for (var dx = 0; dx <= dstWidth; dx++)
            {
                xStart[dx] = (int)((long)dx * srcWidth / dstWidth);
            }

            var srcStride = srcWidth * 3;
            for (var dy = 0; dy < dstHeight; dy++)
            {
                var y0 = (int)((long)dy * srcHeight / dstHeight);
                var y1 = Math.Max(y0 + 1, (int)((long)(dy + 1) * srcHeight / dstHeight));
                var sums = acc.AsSpan(0, dstWidth * 3);
                sums.Clear();
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = src.Slice(sy * srcStride, srcStride);
                    for (var dx = 0; dx < dstWidth; dx++)
                    {
                        var x0 = xStart[dx];
                        var x1 = Math.Max(x0 + 1, xStart[dx + 1]);
                        long r = 0, g = 0, b = 0;
                        for (var i = x0 * 3; i < x1 * 3; i += 3)
                        {
                            r += row[i];
                            g += row[i + 1];
                            b += row[i + 2];
                        }

                        sums[dx * 3] += r;
                        sums[(dx * 3) + 1] += g;
                        sums[(dx * 3) + 2] += b;
                    }
                }

                var outRow = dst.Slice(dy * dstWidth * 3, dstWidth * 3);
                var rows = y1 - y0;
                for (var dx = 0; dx < dstWidth; dx++)
                {
                    long count = rows * Math.Max(1, xStart[dx + 1] - xStart[dx]);
                    var half = count / 2;
                    outRow[dx * 3] = (byte)((sums[dx * 3] + half) / count);
                    outRow[(dx * 3) + 1] = (byte)((sums[(dx * 3) + 1] + half) / count);
                    outRow[(dx * 3) + 2] = (byte)((sums[(dx * 3) + 2] + half) / count);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xStart);
            ArrayPool<long>.Shared.Return(acc);
        }
    }
}
