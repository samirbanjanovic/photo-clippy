using PhotoClippy.Plugins.Clip;

namespace PhotoClippy.Clip.Tests;

public sealed class ClipImagePreprocessorTests
{
    private const int Size = ClipImagePreprocessor.DefaultSize;
    private const int Plane = Size * Size;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    [Theory]
    [InlineData(448, 299, 335, 224)]
    [InlineData(299, 448, 224, 335)]
    [InlineData(100, 50, 448, 224)]
    [InlineData(224, 224, 224, 224)]
    [InlineData(448, 448, 224, 224)]
    [InlineData(1000, 224, 1000, 224)]
    public void ResizedSize_ShortestSideBecomesSize(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ClipImagePreprocessor.ResizedSize(width, height, Size));

    [Theory]
    [InlineData(640, 480)]
    [InlineData(448, 299)]
    [InlineData(50, 120)]
    [InlineData(224, 224)]
    public void Preprocess_UniformColor_ProducesExactNormalizedValues(int width, int height)
    {
        var frame = ClipTestAssets.Solid(width, height, 200, 100, 30);

        var tensor = Run(frame.Rgb.Span, width, height);

        byte[] rgb = [200, 100, 30];
        for (var c = 0; c < 3; c++)
        {
            var expected = ((rgb[c] / 255f) - Mean[c]) / Std[c];
            var channel = tensor.AsSpan(c * Plane, Plane);
            foreach (var value in channel)
            {
                Assert.Equal(expected, value, 1e-5f);
            }
        }
    }

    [Fact]
    public void Preprocess_SquareInputAtTargetSize_IsIdentityInChwLayout()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var o = ((y * Size) + x) * 3;
                rgb[o] = (byte)x;
                rgb[o + 1] = (byte)y;
                rgb[o + 2] = (byte)((x + y) % 256);
            }
        }

        var tensor = Run(rgb, Size, Size);

        for (var y = 0; y < Size; y += 7)
        {
            for (var x = 0; x < Size; x += 5)
            {
                var o = (y * Size) + x;
                Assert.Equal(((x / 255f) - Mean[0]) / Std[0], tensor[o], 1e-5f);
                Assert.Equal(((y / 255f) - Mean[1]) / Std[1], tensor[Plane + o], 1e-5f);
                Assert.Equal((((x + y) % 256 / 255f) - Mean[2]) / Std[2], tensor[(2 * Plane) + o], 1e-5f);
            }
        }
    }

    [Fact]
    public void Preprocess_TallFrame_IsCenterCropped()
    {
        // 224×448: no resize, the centre crop keeps rows 112..335, which are pure green.
        const int width = 224, height = 448;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var green = y is >= 112 and < 336;
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 3;
                rgb[o] = green ? (byte)0 : (byte)255;
                rgb[o + 1] = green ? (byte)255 : (byte)0;
            }
        }

        var tensor = Run(rgb, width, height);

        AssertChannel(tensor, 0, 0);
        AssertChannel(tensor, 1, 255);
        AssertChannel(tensor, 2, 0);
    }

    [Fact]
    public void Preprocess_WideFrame_IsDownscaledAndCenterCropped()
    {
        // 896×448 → 448 wide after resize to 224 rows; crop keeps the middle half (columns 224..671 of the source).
        const int width = 896, height = 448;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                rgb[(((y * width) + x) * 3) + 2] = x is >= 200 and < 696 ? (byte)255 : (byte)0;
            }
        }

        var tensor = Run(rgb, width, height);

        AssertChannel(tensor, 0, 0);
        AssertChannel(tensor, 1, 0);
        AssertChannel(tensor, 2, 255);
    }

    [Fact]
    public void Preprocess_HalfBlackHalfWhite_HasSymmetricStatistics()
    {
        const int width = 448, height = 448;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = width / 2; x < width; x++)
            {
                rgb.AsSpan((((y * width) + x) * 3), 3).Fill(255);
            }
        }

        var tensor = Run(rgb, width, height);

        for (var c = 0; c < 3; c++)
        {
            var black = -Mean[c] / Std[c];
            var white = (1 - Mean[c]) / Std[c];
            var channel = tensor.AsSpan(c * Plane, Plane);
            Assert.Equal(black, channel[0], 1e-5f);
            Assert.Equal(white, channel[Size - 1], 1e-5f);
            Assert.Equal((black + white) / 2, Average(channel), 1e-3f);
            Assert.True(channel.ToArray().Min() >= black - 1e-5f && channel.ToArray().Max() <= white + 1e-5f);
            for (var x = 1; x < Size; x++)
            {
                Assert.True(channel[x] >= channel[x - 1], "Row must be monotonic.");
            }
        }
    }

    [Fact]
    public void Preprocess_SmallFrame_IsUpscaledSmoothly()
    {
        const int width = 32, height = 24;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                rgb[((y * width) + x) * 3] = (byte)(x * 8);
            }
        }

        var tensor = Run(rgb, width, height);

        var red = tensor.AsSpan(0, Plane);
        // The centre crop keeps source columns ~4..28 (values ~32..223), i.e. about 2.8 normalized units.
        Assert.True(red[Size - 1] > red[0] + 2.5f, "Gradient must survive upscaling.");
        for (var x = 1; x < Size; x++)
        {
            Assert.True(red[x] >= red[x - 1] - 1e-5f, "Upscaled gradient must stay monotonic.");
        }
    }

    [Fact]
    public void Preprocess_RejectsShortBuffers()
    {
        Assert.Throws<ArgumentException>(() => ClipImagePreprocessor.Preprocess(new byte[10], 4, 4, Size, new float[3 * Plane]));
        Assert.Throws<ArgumentException>(() => ClipImagePreprocessor.Preprocess(new byte[48], 4, 4, Size, new float[Plane]));
    }

    private static float[] Run(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var tensor = new float[3 * Plane];
        ClipImagePreprocessor.Preprocess(rgb, width, height, Size, tensor);
        return tensor;
    }

    private static void AssertChannel(float[] tensor, int channel, byte value)
    {
        var expected = ((value / 255f) - Mean[channel]) / Std[channel];
        foreach (var v in tensor.AsSpan(channel * Plane, Plane))
        {
            Assert.Equal(expected, v, 1e-5f);
        }
    }

    private static float Average(ReadOnlySpan<float> values)
    {
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        return (float)(sum / values.Length);
    }
}
