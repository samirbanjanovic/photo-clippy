using PhotoClippy.Testing;
using SkiaSharp;

namespace PhotoClippy.Imaging.Tests;

public sealed class JpegDecodeTests : IDisposable
{
    private readonly TestDirectory directory = new();

    public static TheoryData<int, Corner, Corner> Orientations => new()
    {
        { 1, Corner.TopLeft, Corner.TopRight },
        { 2, Corner.TopRight, Corner.TopLeft },
        { 3, Corner.BottomRight, Corner.BottomLeft },
        { 4, Corner.BottomLeft, Corner.BottomRight },
        { 5, Corner.TopLeft, Corner.BottomLeft },
        { 6, Corner.TopRight, Corner.BottomRight },
        { 7, Corner.BottomRight, Corner.TopRight },
        { 8, Corner.BottomLeft, Corner.TopLeft },
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ExifOrientationIsApplied(int orientation, Corner red, Corner green)
    {
        var jpeg = ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(400, 240), new ExifSpec { Orientation = orientation });
        var path = directory.Write($"o{orientation}.jpg", jpeg);

        using var decoded = Decoding.Ok(path, 200);
        var frame = decoded.Frame;
        var swapped = orientation >= 5;
        Assert.Equal("jpeg", decoded.Method);
        Assert.Equal(swapped ? (120, 200) : (200, 120), (frame.Width, frame.Height));
        Assert.Equal(swapped ? (240, 400) : (400, 240), (frame.SourceWidth, frame.SourceHeight));
        TestImages.AssertMarkers(frame, red, green);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ExifOrientationIsAppliedAtFullResolution(int orientation, Corner red, Corner green)
    {
        var jpeg = ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(160, 96), new ExifSpec { Orientation = orientation });
        var path = directory.Write($"full{orientation}.jpg", jpeg);

        using var decoded = Decoding.Ok(path, 4096);
        var frame = decoded.Frame;
        Assert.Equal(orientation >= 5 ? (96, 160) : (160, 96), (frame.Width, frame.Height));
        TestImages.AssertMarkers(frame, red, green);
    }

    [Fact]
    public void RequestOrientationOverridesEmbeddedExif()
    {
        var withExif = directory.Write("exif6.jpg", ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(400, 240), new ExifSpec { Orientation = 6 }));
        using (var decoded = Decoding.Ok(withExif, 200, orientation: 1))
        {
            Assert.Equal((200, 120), (decoded.Frame.Width, decoded.Frame.Height));
            TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
        }

        var plain = directory.Write("plain.jpg", TestImages.MarkerImage(400, 240));
        using (var decoded = Decoding.Ok(plain, 200, orientation: 8))
        {
            Assert.Equal((120, 200), (decoded.Frame.Width, decoded.Frame.Height));
            TestImages.AssertMarkers(decoded.Frame, Corner.BottomLeft, Corner.TopLeft);
        }
    }

    [Fact]
    public void InvalidRequestOrientationFallsBackToEmbeddedExif()
    {
        var path = directory.Write("exif3.jpg", ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(400, 240), new ExifSpec { Orientation = 3 }));
        using var decoded = Decoding.Ok(path, 200, orientation: 0);
        TestImages.AssertMarkers(decoded.Frame, Corner.BottomRight, Corner.BottomLeft);
    }

    [Theory]
    [InlineData(1200, 800, 300, 300, 200)]
    [InlineData(1200, 800, 1199, 1199, 799)]
    [InlineData(1200, 800, 600, 600, 400)]
    [InlineData(1200, 800, 151, 151, 101)]
    [InlineData(1000, 333, 500, 500, 167)]
    [InlineData(333, 1000, 256, 85, 256)]
    [InlineData(640, 480, 4096, 640, 480)]
    [InlineData(640, 480, 640, 640, 480)]
    [InlineData(17, 9, 8, 8, 4)]
    public void MaxEdgeIsRespectedWithoutUpscaling(int width, int height, int maxEdge, int expectedWidth, int expectedHeight)
    {
        var path = directory.Write($"size-{width}x{height}-{maxEdge}.jpg", TestImages.MarkerImage(width, height));
        using var decoded = Decoding.Ok(path, maxEdge);
        var frame = decoded.Frame;
        Assert.Equal((expectedWidth, expectedHeight), (frame.Width, frame.Height));
        Assert.Equal((width, height), (frame.SourceWidth, frame.SourceHeight));
    }

    [Fact]
    public void SourceDimensionsFollowDisplayedOrientation()
    {
        var path = directory.Write("rot.jpg", ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(1200, 800), new ExifSpec { Orientation = 6 }));
        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((800, 1200), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(500)]
    [InlineData(97)]
    public void SolidColorsSurviveDownscaling(int maxEdge)
    {
        var color = new Rgb(200, 120, 40);
        var path = directory.Write($"solid{maxEdge}.jpg", TestImages.SolidJpeg(1000, 640, color));
        using var decoded = Decoding.Ok(path, maxEdge);
        var mean = TestImages.Mean(decoded.Frame, 0, 0, 1, 1);
        Assert.InRange(mean.R, color.R - 6, color.R + 6);
        Assert.InRange(mean.G, color.G - 6, color.G + 6);
        Assert.InRange(mean.B, color.B - 6, color.B + 6);
        var corner = TestImages.Pixel(decoded.Frame, decoded.Frame.Width - 1, decoded.Frame.Height - 1);
        Assert.InRange(corner.R, color.R - 10, color.R + 10);
    }

    [Fact]
    public void DownscaledGradientKeepsItsShape()
    {
        const int width = 2400;
        const int height = 1600;
        var rgba = TestImages.Rgba(width, height, (x, y) => new Rgb(x * 255 / (width - 1), y * 255 / (height - 1), 128));
        var path = directory.Write("gradient.jpg", TestImages.Encode(width, height, rgba, quality: 95));
        using var decoded = Decoding.Ok(path, 240);
        var frame = decoded.Frame;
        Assert.Equal((240, 160), (frame.Width, frame.Height));
        for (var x = 10; x < frame.Width; x += 50)
        {
            var expected = ((x + 0.5) * width / frame.Width) * 255 / (width - 1);
            Assert.InRange(TestImages.Pixel(frame, x, 80).R, expected - 5, expected + 5);
        }

        for (var y = 10; y < frame.Height; y += 40)
        {
            var expected = ((y + 0.5) * height / frame.Height) * 255 / (height - 1);
            Assert.InRange(TestImages.Pixel(frame, 120, y).G, expected - 5, expected + 5);
        }
    }

    [Fact]
    public void GrayscaleJpegDecodesToNeutralRgb()
    {
        const int width = 300;
        const int height = 200;
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)(i % width * 255 / width);
        }

        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, gray);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = directory.Write("gray.jpg", data.ToArray());

        using var decoded = Decoding.Ok(path, 150);
        Assert.Equal((150, 100), (decoded.Frame.Width, decoded.Frame.Height));
        var pixel = TestImages.Pixel(decoded.Frame, 120, 50);
        Assert.True(pixel.IsNeutral && pixel.R > 150, pixel.ToString());
    }

    [Fact]
    public void JpegWithWrongExtensionIsStillDecoded()
    {
        var path = directory.Write("actually-jpeg.png", TestImages.MarkerImage(320, 200));
        using var decoded = Decoding.Ok(path, 160);
        Assert.Equal((160, 100), (decoded.Frame.Width, decoded.Frame.Height));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "png")]
    [InlineData(SKEncodedImageFormat.Webp, "webp")]
    public void OtherFormatsUseTheGenericCodecPath(SKEncodedImageFormat format, string extension)
    {
        var path = directory.Write("marker." + extension, TestImages.MarkerImage(400, 240, format, 100));
        using var decoded = Decoding.Ok(path, 200);
        Assert.Equal("codec", decoded.Method);
        Assert.Equal((200, 120), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((400, 240), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
    }

    [Fact]
    public void RequestOrientationAppliesToPng()
    {
        var path = directory.Write("rotated.png", TestImages.MarkerImage(400, 240, SKEncodedImageFormat.Png, 100));
        using var decoded = Decoding.Ok(path, 200, orientation: 6);
        Assert.Equal((120, 200), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopRight, Corner.BottomRight);
    }

    [Fact]
    public void TransparentPngIsDecoded()
    {
        const int width = 64;
        const int height = 48;
        var rgba = TestImages.Rgba(width, height, (_, _) => new Rgb(255, 255, 255));
        for (var i = 3; i < rgba.Length; i += 8)
        {
            rgba[i] = 0;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, rgba);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = directory.Write("alpha.png", data.ToArray());
        using var decoded = Decoding.Ok(path, 32);
        Assert.Equal((32, 24), (decoded.Frame.Width, decoded.Frame.Height));
    }

    public void Dispose() => directory.Dispose();
}
