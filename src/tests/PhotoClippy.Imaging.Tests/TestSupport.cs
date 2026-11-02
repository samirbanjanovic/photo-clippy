using PhotoClippy.Contracts;
using SkiaSharp;

namespace PhotoClippy.Imaging.Tests;

public readonly record struct Rgb(int R, int G, int B)
{
    public bool IsRed => R > 150 && G < 100 && B < 100;

    public bool IsGreen => G > 130 && R < 100 && B < 100;

    public bool IsNeutral => Math.Abs(R - G) < 40 && Math.Abs(G - B) < 40 && Math.Abs(R - B) < 40;

    public override string ToString() => $"({R},{G},{B})";
}

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

internal static class TestImages
{
    public static readonly Rgb Red = new(230, 20, 20);
    public static readonly Rgb Green = new(20, 200, 20);

    /// <summary>Gray gradient with a red block in the stored top-left and a green block in the stored top-right quarter.</summary>
    public static Rgb Marker(int x, int y, int width, int height)
    {
        if (y < height / 4)
        {
            if (x < width / 4)
            {
                return Red;
            }

            if (x >= width - (width / 4))
            {
                return Green;
            }
        }

        var gray = 60 + (120 * (x + y) / (width + height));
        return new Rgb(gray, gray, gray);
    }

    public static byte[] Rgba(int width, int height, Func<int, int, Rgb> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = pixel(x, y);
                var i = ((y * width) + x) * 4;
                rgba[i] = (byte)color.R;
                rgba[i + 1] = (byte)color.G;
                rgba[i + 2] = (byte)color.B;
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }

    public static byte[] Encode(int width, int height, byte[] rgba, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int quality = 92)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, rgba);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] MarkerImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int quality = 92)
        => Encode(width, height, Rgba(width, height, (x, y) => Marker(x, y, width, height)), format, quality);

    public static byte[] SolidJpeg(int width, int height, Rgb color)
        => Encode(width, height, Rgba(width, height, (_, _) => color));

    public static Rgb Pixel(Frame frame, int x, int y)
    {
        var span = frame.Rgb.Span;
        var i = ((y * frame.Width) + x) * 3;
        return new Rgb(span[i], span[i + 1], span[i + 2]);
    }

    public static Rgb Mean(Frame frame, double fx0, double fy0, double fx1, double fy1)
    {
        var x0 = (int)(fx0 * frame.Width);
        var y0 = (int)(fy0 * frame.Height);
        var x1 = Math.Max(x0 + 1, (int)(fx1 * frame.Width));
        var y1 = Math.Max(y0 + 1, (int)(fy1 * frame.Height));
        long r = 0, g = 0, b = 0, n = 0;
        var span = frame.Rgb.Span;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * frame.Width) + x) * 3;
                r += span[i];
                g += span[i + 1];
                b += span[i + 2];
                n++;
            }
        }

        return new Rgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    public static Rgb Sample(Frame frame, Corner corner)
    {
        var (x0, y0) = corner switch
        {
            Corner.TopLeft => (0.04, 0.04),
            Corner.TopRight => (0.86, 0.04),
            Corner.BottomLeft => (0.04, 0.86),
            _ => (0.86, 0.86),
        };
        return Mean(frame, x0, y0, x0 + 0.10, y0 + 0.10);
    }

    public static void AssertMarkers(Frame frame, Corner red, Corner green)
    {
        foreach (var corner in Enum.GetValues<Corner>())
        {
            var color = Sample(frame, corner);
            if (corner == red)
            {
                Assert.True(color.IsRed, $"expected red at {corner}, got {color}");
            }
            else if (corner == green)
            {
                Assert.True(color.IsGreen, $"expected green at {corner}, got {color}");
            }
            else
            {
                Assert.True(color.IsNeutral, $"expected gray at {corner}, got {color}");
            }
        }
    }

    public static long Checksum(Frame frame)
    {
        var hash = 1469598103934665603L;
        foreach (var value in frame.Rgb.Span)
        {
            hash = (hash ^ value) * 1099511628211L;
        }

        return hash ^ ((long)frame.Width << 32) ^ frame.Height;
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "imaging-fixtures", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Decoding
{
    public static readonly FrameDecoder Decoder = new();

    public static DecodeOutcome Run(string path, int maxEdge, int? orientation = null)
        => Decoder.Decode(new DecodeRequest { Path = path, MaxEdge = maxEdge, Orientation = orientation });

    public static DecodedFrame Ok(string path, int maxEdge, int? orientation = null)
    {
        var outcome = Run(path, maxEdge, orientation);
        Assert.True(outcome.Status == DecodeStatus.Ok, $"{outcome.Status}: {outcome.Error}");
        Assert.NotNull(outcome.Frame);
        Assert.Null(outcome.Error);
        var frame = outcome.Frame!.Frame;
        Assert.Equal(frame.Width * frame.Height * 3, frame.Rgb.Length);
        Assert.True(Math.Max(frame.Width, frame.Height) <= maxEdge);
        return outcome.Frame;
    }
}

/// <summary>A fact that is skipped unless the environment variable points at an existing directory.</summary>
public sealed class DirectoryFactAttribute : FactAttribute
{
    public DirectoryFactAttribute(string environmentVariable)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            Skip = $"set {environmentVariable} to a directory of sample files to run this test";
        }
    }
}
