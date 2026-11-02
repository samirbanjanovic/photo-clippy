using System.Diagnostics;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoClippy.Imaging.Tests;

public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 6000;
    private const int Height = 4000;

    private static readonly Lazy<byte[]> Jpeg24 = new(() => NoisyJpeg(Width, Height));

    private readonly TestDirectory directory = new();

    [Fact]
    public void Jpeg24MegapixelDecodesQuickly()
    {
        var path = directory.Write("24mp.jpg", Jpeg24.Value);
        output.WriteLine($"24 MP JPEG: {new FileInfo(path).Length / 1024} KiB");

        foreach (var maxEdge in new[] { 512, 1024, 256 })
        {
            using (var warm = Decoding.Ok(path, maxEdge))
            {
                Assert.Equal("jpeg", warm.Method);
                Assert.Equal((Width, Height), (warm.Frame.SourceWidth, warm.Frame.SourceHeight));
            }

            const int iterations = 5;
            var best = double.MaxValue;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var start = watch.Elapsed;
                using var decoded = Decoding.Ok(path, maxEdge);
                best = Math.Min(best, (watch.Elapsed - start).TotalMilliseconds);
            }

            var mean = watch.Elapsed.TotalMilliseconds / iterations;
            output.WriteLine($"6000x4000 JPEG -> {maxEdge}px: mean {mean:F1} ms, best {best:F1} ms");
            if (maxEdge == 512)
            {
                Assert.True(best < 150, $"decode took {best:F1} ms");
            }
        }
    }

    [Fact]
    public void ParallelThroughputIsReported()
    {
        var path = directory.Write("24mp-parallel.jpg", Jpeg24.Value);
        using (Decoding.Ok(path, 512))
        {
        }

        const int count = 32;
        var watch = Stopwatch.StartNew();
        Parallel.For(0, count, _ =>
        {
            using var decoded = Decoding.Ok(path, 512);
        });
        watch.Stop();
        output.WriteLine($"{count} parallel 24 MP decodes -> 512px: {watch.Elapsed.TotalMilliseconds:F0} ms total, {count / watch.Elapsed.TotalSeconds:F1} images/s on {Environment.ProcessorCount} cores");
    }

    [Fact]
    public void TiffScanDownscaleTimingIsReported()
    {
        const int width = 5000;
        const int height = 3500;
        var data = new ushort[width * height * 3];
        var state = 0x9E3779B9u;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (ushort)((i / 3 % width * 200 / width) + (state & 31));
        }

        foreach (var compression in new[] { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate })
        {
            var image = new TiffImage
            {
                Width = width,
                Height = height,
                Samples = 3,
                BitsPerSample = 8,
                Data = data,
                Compression = compression,
                Predictor = compression != TiffCompression.None,
                RowsPerStrip = 32,
            };
            var path = directory.Write($"scan-{compression}.tif", TiffWriter.Write(image));
            using (Decoding.Ok(path, 512))
            {
            }

            var watch = Stopwatch.StartNew();
            using var decoded = Decoding.Ok(path, 512);
            output.WriteLine($"{width}x{height} RGB TIFF ({compression}, {new FileInfo(path).Length / 1048576.0:F1} MiB) -> 512px: {watch.Elapsed.TotalMilliseconds:F1} ms");
        }
    }

    private static byte[] NoisyJpeg(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        var state = 0x2545F491u;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                var noise = (int)(state & 15) - 8;
                var i = row + (x * 4);
                rgba[i] = (byte)Math.Clamp((x * 255 / width) + noise, 0, 255);
                rgba[i + 1] = (byte)Math.Clamp((y * 255 / height) + noise, 0, 255);
                rgba[i + 2] = (byte)Math.Clamp(((x ^ y) & 255) + noise, 0, 255);
                rgba[i + 3] = 255;
            }
        }

        return TestImages.Encode(width, height, rgba, SKEncodedImageFormat.Jpeg, 90);
    }

    public void Dispose() => directory.Dispose();
}
