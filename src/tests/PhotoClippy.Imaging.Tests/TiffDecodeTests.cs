using PhotoClippy.Contracts;
using PhotoClippy.Testing;

namespace PhotoClippy.Imaging.Tests;

public sealed class TiffDecodeTests : IDisposable
{
    private readonly TestDirectory directory = new();

    public static TheoryData<TiffCompression, bool> RgbVariants => new()
    {
        { TiffCompression.None, false },
        { TiffCompression.PackBits, false },
        { TiffCompression.Deflate, false },
        { TiffCompression.AdobeDeflate, true },
        { TiffCompression.Lzw, false },
        { TiffCompression.Lzw, true },
    };

    [Theory]
    [MemberData(nameof(RgbVariants))]
    public void Rgb8DecodesExactlyAtFullResolution(TiffCompression compression, bool predictor)
    {
        var image = Rgb8(203, 141) with { Compression = compression, Predictor = predictor };
        using var decoded = Decode(image, 4096);
        Assert.Equal("tiff", decoded.Method);
        AssertExact(image, decoded.Frame);
    }

    [Theory]
    [MemberData(nameof(RgbVariants))]
    public void Rgb8DownscalesWhileStreaming(TiffCompression compression, bool predictor)
    {
        var image = Rgb8(900, 600) with { Compression = compression, Predictor = predictor };
        using var decoded = Decode(image, 300);
        Assert.Equal((300, 200), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((900, 600), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
        AssertDownscaled(image, decoded.Frame);
    }

    [Theory]
    [InlineData(TiffCompression.None, false, false)]
    [InlineData(TiffCompression.Lzw, true, false)]
    [InlineData(TiffCompression.Deflate, false, true)]
    [InlineData(TiffCompression.PackBits, false, true)]
    public void Gray16Decodes(TiffCompression compression, bool predictor, bool bigEndian)
    {
        const int width = 257;
        const int height = 190;
        var data = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[(y * width) + x] = (ushort)((long)(x + y) * 65535 / (width + height - 2));
            }
        }

        var image = new TiffImage
        {
            Width = width,
            Height = height,
            Samples = 1,
            BitsPerSample = 16,
            Data = data,
            Photometric = 1,
            Compression = compression,
            Predictor = predictor,
            BigEndian = bigEndian,
        };
        using var decoded = Decode(image, 4096);
        AssertExact(image, decoded.Frame);
    }

    [Fact]
    public void Rgb16BigEndianWithPredictorDecodes()
    {
        var rgb8 = Rgb8(160, 120);
        var image = rgb8 with
        {
            BitsPerSample = 16,
            Data = Array.ConvertAll(rgb8.Data, static v => (ushort)(v * 257)),
            Compression = TiffCompression.Lzw,
            Predictor = true,
            BigEndian = true,
        };
        using var decoded = Decode(image, 4096);
        AssertExact(rgb8, decoded.Frame);
    }

    [Fact]
    public void WhiteIsZeroIsInverted()
    {
        var image = Gray8(120, 80) with { Photometric = 0, Compression = TiffCompression.PackBits };
        using var decoded = Decode(image, 4096);
        for (var y = 0; y < image.Height; y += 7)
        {
            for (var x = 0; x < image.Width; x += 5)
            {
                var pixel = TestImages.Pixel(decoded.Frame, x, y);
                Assert.Equal(255 - image[x, y, 0], pixel.R);
                Assert.True(pixel.R == pixel.G && pixel.G == pixel.B);
            }
        }
    }

    [Fact]
    public void BilevelImageDecodes()
    {
        var gray = Gray8(77, 31);
        var image = gray with
        {
            BitsPerSample = 1,
            Data = Array.ConvertAll(gray.Data, static v => (ushort)(v > 127 ? 1 : 0)),
            Photometric = 1,
        };
        using var decoded = Decode(image, 4096);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                Assert.Equal(image[x, y, 0] == 1 ? 255 : 0, TestImages.Pixel(decoded.Frame, x, y).R);
            }
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void PaletteImageDecodes(int bits)
    {
        var entries = 1 << bits;
        var map = new ushort[3 * entries];
        for (var i = 0; i < entries; i++)
        {
            map[i] = (ushort)(i * 65535 / (entries - 1));
            map[entries + i] = (ushort)(65535 - map[i]);
            map[(2 * entries) + i] = 0x8080;
        }

        var gray = Gray8(64, 40);
        var image = gray with
        {
            BitsPerSample = bits,
            Data = Array.ConvertAll(gray.Data, v => (ushort)(v % entries)),
            Photometric = 3,
            ColorMap = map,
            Compression = TiffCompression.Lzw,
        };
        using var decoded = Decode(image, 4096);
        for (var y = 0; y < image.Height; y += 3)
        {
            for (var x = 0; x < image.Width; x += 3)
            {
                var index = image[x, y, 0];
                var pixel = TestImages.Pixel(decoded.Frame, x, y);
                Assert.Equal(map[index] >> 8, pixel.R);
                Assert.Equal(map[entries + index] >> 8, pixel.G);
                Assert.Equal(0x80, pixel.B);
            }
        }
    }

    [Fact]
    public void RgbaIgnoresAlpha()
    {
        var rgb = Rgb8(150, 100);
        var data = new ushort[rgb.Width * rgb.Height * 4];
        for (var i = 0; i < rgb.Width * rgb.Height; i++)
        {
            data[(i * 4) + 0] = rgb.Data[(i * 3) + 0];
            data[(i * 4) + 1] = rgb.Data[(i * 3) + 1];
            data[(i * 4) + 2] = rgb.Data[(i * 3) + 2];
            data[(i * 4) + 3] = (ushort)(i % 256);
        }

        var image = rgb with { Samples = 4, Data = data, Compression = TiffCompression.Deflate };
        using var decoded = Decode(image, 4096);
        AssertExact(rgb, decoded.Frame);
    }

    [Theory]
    [InlineData(TiffCompression.Deflate, false)]
    [InlineData(TiffCompression.Lzw, true)]
    [InlineData(TiffCompression.None, false)]
    public void TiledImagesDecode(TiffCompression compression, bool predictor)
    {
        var image = Rgb8(150, 101) with { TileSize = 32, Compression = compression, Predictor = predictor };
        using (var full = Decode(image, 4096))
        {
            AssertExact(image, full.Frame);
        }

        using var scaled = Decode(image, 75, "tiled-small.tif");
        Assert.Equal((75, 51), (scaled.Frame.Width, scaled.Frame.Height));
        AssertDownscaled(image, scaled.Frame);
    }

    [Theory]
    [InlineData(TiffCompression.Lzw, false)]
    [InlineData(TiffCompression.Deflate, true)]
    public void PlanarImagesDecode(TiffCompression compression, bool tiled)
    {
        var image = Rgb8(130, 90) with { Planar = true, Compression = compression, TileSize = tiled ? 48 : null };
        using var decoded = Decode(image, 4096);
        AssertExact(image, decoded.Frame);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(400)]
    public void JpegCompressedTiffDecodes(int rowsPerStrip)
    {
        var image = new TiffImage
        {
            Width = 600,
            Height = 400,
            Samples = 3,
            BitsPerSample = 8,
            Data = RawPreviewTests.MarkerData(600, 400),
            Photometric = 6,
            Compression = TiffCompression.Jpeg,
            RowsPerStrip = rowsPerStrip,
        };
        using var decoded = Decode(image, 300);
        Assert.Equal((300, 200), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
    }

    [Fact]
    public void OrientationTagRotatesOutput()
    {
        var image = Rgb8(90, 60) with { Orientation = 6, Compression = TiffCompression.Lzw };
        using var decoded = Decode(image, 4096);
        var frame = decoded.Frame;
        Assert.Equal((60, 90), (frame.Width, frame.Height));
        Assert.Equal((60, 90), (frame.SourceWidth, frame.SourceHeight));
        for (var dy = 0; dy < frame.Height; dy += 4)
        {
            for (var dx = 0; dx < frame.Width; dx += 4)
            {
                var pixel = TestImages.Pixel(frame, dx, dy);
                var sx = dy;
                var sy = image.Height - 1 - dx;
                Assert.Equal(image[sx, sy, 0], pixel.R);
                Assert.Equal(image[sx, sy, 1], pixel.G);
            }
        }

        using var overridden = Decode(image, 4096, "override.tif", orientation: 1);
        Assert.Equal((90, 60), (overridden.Frame.Width, overridden.Frame.Height));
    }

    [Fact]
    public void EmbeddedPreviewIsPreferredWhenLargeEnough()
    {
        var image = Rgb8(1200, 800) with
        {
            Compression = TiffCompression.Deflate,
            PreviewJpeg = TestImages.SolidJpeg(600, 400, new Rgb(20, 40, 220)),
        };

        using (var small = Decode(image, 300, "preview-small.tif"))
        {
            Assert.Equal("tiff", small.Method);
            Assert.Equal((300, 200), (small.Frame.Width, small.Frame.Height));
            Assert.Equal((1200, 800), (small.Frame.SourceWidth, small.Frame.SourceHeight));
            Assert.True(TestImages.Mean(small.Frame, 0.4, 0.4, 0.6, 0.6).B > 180, "embedded preview expected");
        }

        using var large = Decode(image, 800, "preview-large.tif");
        Assert.Equal((800, 533), (large.Frame.Width, large.Frame.Height));
        AssertDownscaled(image, large.Frame);
    }

    [Fact]
    public void JpegOnlyTiffDecodesThroughItsJpeg()
    {
        var tiff = ExifWriter.BuildTiff(new ExifSpec { Orientation = 6 }, TestImages.MarkerImage(600, 400));
        var path = directory.Write("jpeg-only.tif", tiff);
        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal("tiff", decoded.Method);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopRight, Corner.BottomRight);
    }

    [Fact]
    public void LargeScanDownscalesWithinBudget()
    {
        const int width = 6000;
        const int height = 4000;
        var data = new ushort[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                data[i] = (ushort)(x * 255 / (width - 1));
                data[i + 1] = (ushort)(y * 255 / (height - 1));
                data[i + 2] = 90;
            }
        }

        var image = new TiffImage { Width = width, Height = height, Samples = 3, BitsPerSample = 8, Data = data, RowsPerStrip = 64 };
        using var decoded = Decode(image, 512);
        Assert.Equal((512, 341), (decoded.Frame.Width, decoded.Frame.Height));
        AssertDownscaled(image, decoded.Frame);
    }

    [Fact]
    public void UnsupportedTiffVariantFails()
    {
        var bytes = TiffWriter.Write(Rgb8(32, 32));
        PatchCompression(bytes, 4);
        var path = directory.Write("ccitt.tif", bytes);
        var outcome = Decoding.Run(path, 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
        Assert.Contains("compression", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedStripsFail()
    {
        var bytes = TiffWriter.Write(Rgb8(400, 300) with { Compression = TiffCompression.Lzw });
        var path = directory.Write("cut.tif", bytes.AsSpan(0, bytes.Length / 2).ToArray());
        var outcome = Decoding.Run(path, 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
    }

    [Fact]
    public void CorruptCompressedDataFailsWithoutThrowing()
    {
        foreach (var compression in new[] { TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits })
        {
            var image = Rgb8(200, 150) with { Compression = compression };
            var bytes = TiffWriter.Write(image);
            var random = new Random((int)compression);
            for (var i = 16; i < bytes.Length / 2; i += 7)
            {
                bytes[i] = (byte)random.Next(256);
            }

            var path = directory.Write($"corrupt-{compression}.tif", bytes);
            var outcome = Decoding.Run(path, 128);
            outcome.Frame?.Dispose();
            Assert.NotEqual(DecodeStatus.Unsupported, outcome.Status);
        }
    }

    private DecodedFrame Decode(TiffImage image, int maxEdge, string? name = null, int? orientation = null)
    {
        name ??= $"{image.Compression}-{image.BitsPerSample}-{image.Samples}-{maxEdge}-{Guid.NewGuid():N}.tif";
        var path = directory.Write(name, TiffWriter.Write(image));
        return Decoding.Ok(path, maxEdge, orientation);
    }

    private static TiffImage Rgb8(int width, int height)
    {
        var data = new ushort[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                data[i] = (ushort)(x * 255 / (width - 1));
                data[i + 1] = (ushort)(y * 255 / (height - 1));
                data[i + 2] = (ushort)((((x / 8) + (y / 8)) % 2 * 200) + 20);
            }
        }

        return new TiffImage { Width = width, Height = height, Samples = 3, BitsPerSample = 8, Data = data };
    }

    private static TiffImage Gray8(int width, int height)
    {
        var data = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[(y * width) + x] = (ushort)(((x * 7) + (y * 13)) % 256);
            }
        }

        return new TiffImage { Width = width, Height = height, Samples = 1, BitsPerSample = 8, Data = data, Photometric = 1 };
    }

    private static int To8(TiffImage image, int value) => image.BitsPerSample == 16 ? ((value * 255) + 32767) / 65535 : value;

    private static void AssertExact(TiffImage image, Frame frame)
    {
        Assert.Equal((image.Width, image.Height), (frame.Width, frame.Height));
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = TestImages.Pixel(frame, x, y);
                if (image.Samples >= 3)
                {
                    var expected = new Rgb(To8(image, image[x, y, 0]), To8(image, image[x, y, 1]), To8(image, image[x, y, 2]));
                    Assert.True(Math.Abs(expected.R - pixel.R) <= 1 && Math.Abs(expected.G - pixel.G) <= 1 && Math.Abs(expected.B - pixel.B) <= 1, $"({x},{y}) expected {expected}, got {pixel}");
                }
                else
                {
                    var expected = To8(image, image[x, y, 0]);
                    Assert.True(Math.Abs(expected - pixel.R) <= 1 && pixel.R == pixel.G && pixel.G == pixel.B, $"({x},{y}) expected {expected}, got {pixel}");
                }
            }
        }
    }

    private static void AssertDownscaled(TiffImage image, Frame frame)
    {
        for (var oy = 0; oy < frame.Height; oy += 7)
        {
            for (var ox = 0; ox < frame.Width; ox += 7)
            {
                var xc = ((ox + 0.5) * image.Width / frame.Width) - 0.5;
                var yc = ((oy + 0.5) * image.Height / frame.Height) - 0.5;
                var expectedR = xc * 255 / (image.Width - 1);
                var expectedG = yc * 255 / (image.Height - 1);
                var pixel = TestImages.Pixel(frame, ox, oy);
                Assert.True(Math.Abs(expectedR - pixel.R) <= 4 && Math.Abs(expectedG - pixel.G) <= 4, $"({ox},{oy}) expected ~({expectedR:F1},{expectedG:F1}), got {pixel}");
            }
        }
    }

    private static void PatchCompression(byte[] tiff, ushort compression)
    {
        var ifd = BitConverter.ToInt32(tiff, 4);
        var count = BitConverter.ToUInt16(tiff, ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (BitConverter.ToUInt16(tiff, entry) == 259)
            {
                BitConverter.TryWriteBytes(tiff.AsSpan(entry + 8), compression);
            }
        }
    }

    public void Dispose() => directory.Dispose();
}
