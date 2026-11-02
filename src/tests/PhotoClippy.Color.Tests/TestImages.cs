using PhotoClippy.Contracts;

namespace PhotoClippy.Color.Tests;

/// <summary>Deterministic synthetic RGB24 images.</summary>
internal static class TestImages
{
    public const int W = 320;
    public const int H = 213;

    public static Frame ToFrame(byte[] rgb, int width, int height) => new(width, height, rgb, width, height);

    public static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 3];
        for (var i = 0; i < px.Length; i += 3)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
        }

        return px;
    }

    /// <summary>Left half colour <paramref name="left"/>, right half <paramref name="right"/>.</summary>
    public static byte[] HalfHalf(int width, int height, (byte R, byte G, byte B) left, (byte R, byte G, byte B) right)
    {
        var px = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = x < width / 2 ? left : right;
                Set(px, width, x, y, c.R, c.G, c.B);
            }
        }

        return px;
    }

    /// <summary>Horizontal grey ramp from <paramref name="from"/> (left) to <paramref name="to"/> (right).</summary>
    public static byte[] GreyRamp(int width, int height, int from, int to)
    {
        var px = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (byte)Math.Round(from + (to - from) * (double)x / (width - 1));
                Set(px, width, x, y, v, v, v);
            }
        }

        return px;
    }

    public static byte[] Checkerboard(int width, int height, int square, byte dark, byte light)
    {
        var px = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = ((x / square) + (y / square)) % 2 == 0 ? dark : light;
                Set(px, width, x, y, v, v, v);
            }
        }

        return px;
    }

    /// <summary>Separable (2r+1)×(2r+1) box blur with edge clamping.</summary>
    public static byte[] BoxBlur(byte[] src, int width, int height, int radius)
    {
        var tmp = new double[src.Length];
        var dst = new byte[src.Length];
        var size = 2 * radius + 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var s = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        s += src[(y * width + Math.Clamp(x + k, 0, width - 1)) * 3 + c];
                    }

                    tmp[(y * width + x) * 3 + c] = s / size;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var s = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        s += tmp[(Math.Clamp(y + k, 0, height - 1) * width + x) * 3 + c];
                    }

                    dst[(y * width + x) * 3 + c] = (byte)Math.Round(s / size);
                }
            }
        }

        return dst;
    }

    /// <summary>
    /// Natural-ish landscape: sky gradient in the top 40 %, foliage greens below, a skin-tone ellipse and a dark
    /// trunk, plus deterministic noise. Rendered from a continuous function so it can be produced at any size.
    /// </summary>
    public static byte[] Scene(int width, int height, int seed = 1, double noise = 6.0)
    {
        var px = new byte[width * height * 3];
        var state = (uint)(seed * 2654435761u + 12345u);
        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) / height;
            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) / width;
                double r, g, b;
                if (v < 0.4)
                {
                    var t = v / 0.4;
                    r = 110 + 60 * t;
                    g = 170 + 40 * t;
                    b = 235 - 5 * t;
                }
                else
                {
                    var wave = 0.5 + 0.5 * Math.Sin(u * 25 + v * 9 + seed);
                    r = 40 + 30 * wave;
                    g = 110 + 50 * wave;
                    b = 35 + 15 * wave;
                }

                var dx = (u - 0.62) / 0.08;
                var dy = (v - 0.55) / 0.14;
                if (dx * dx + dy * dy < 1)
                {
                    r = 224;
                    g = 172;
                    b = 140;
                }

                if (Math.Abs(u - 0.25) < 0.02 && v > 0.35)
                {
                    r = 60;
                    g = 42;
                    b = 30;
                }

                if (noise > 0)
                {
                    state = state * 1664525u + 1013904223u;
                    var n = ((state >> 8) / (double)(1 << 24) - 0.5) * 2 * noise;
                    r += n;
                    g += n;
                    b += n;
                }

                Set(px, width, x, y, Clamp(r), Clamp(g), Clamp(b));
            }
        }

        return px;
    }

    /// <summary>Greyscale scene (ramps + shapes) toned with the classic sepia matrix.</summary>
    public static byte[] Sepia(int width, int height)
    {
        var px = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var grey = 255.0 * x / (width - 1) * 0.7 + 255.0 * y / (height - 1) * 0.3;
                if (Math.Abs(x - width / 2) < width / 8 && Math.Abs(y - height / 2) < height / 6)
                {
                    grey = 255 - grey;
                }

                Set(
                    px, width, x, y,
                    Clamp(grey * (0.393 + 0.769 + 0.189)),
                    Clamp(grey * (0.349 + 0.686 + 0.168)),
                    Clamp(grey * (0.272 + 0.534 + 0.131)));
            }
        }

        return px;
    }

    /// <summary>Nearest-neighbour integer upscale (every pixel replicated factor×factor).</summary>
    public static byte[] Replicate(byte[] src, int width, int height, int factor)
    {
        var dw = width * factor;
        var dst = new byte[dw * height * factor * 3];
        for (var y = 0; y < height * factor; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var s = ((y / factor) * width + x / factor) * 3;
                var d = (y * dw + x) * 3;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
            }
        }

        return dst;
    }

    public static (int R, int G, int B) ParseHex(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    private static void Set(byte[] px, int width, int x, int y, byte r, byte g, byte b)
    {
        var o = (y * width + x) * 3;
        px[o] = r;
        px[o + 1] = g;
        px[o + 2] = b;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
