using PhotoClippy.Plugins.Color;

namespace PhotoClippy.Color.Tests;

public sealed class OkLabTests
{
    [Fact]
    public void Cbrt_MatchesMathCbrt()
    {
        var worst = 0.0;
        for (var i = 1; i <= 200_000; i++)
        {
            var x = i / 200_000f;
            var rel = Math.Abs(OkLab.Cbrt(x) - Math.Cbrt(x)) / Math.Cbrt(x);
            worst = Math.Max(worst, rel);
        }

        Assert.True(worst < 5e-7, $"worst relative error {worst}");
        Assert.Equal(0f, OkLab.Cbrt(0f));
        Assert.InRange(OkLab.Cbrt(1e-9f), 0.000999f, 0.001001f);
    }

    [Fact]
    public void HueDegrees_MatchesAtan2()
    {
        var worst = 0.0;
        for (var deg = 0; deg < 3600; deg++)
        {
            var angle = deg * Math.PI / 1800.0;
            var a = (float)(0.1 * Math.Cos(angle));
            var b = (float)(0.1 * Math.Sin(angle));
            var expected = Math.Atan2(b, a) * 180.0 / Math.PI;
            if (expected < 0)
            {
                expected += 360;
            }

            var diff = Math.Abs(OkLab.HueDegrees(a, b) - expected);
            worst = Math.Max(worst, Math.Min(diff, 360 - diff));
        }

        Assert.True(worst < 0.01, $"worst error {worst}°");
    }

    [Theory]
    [InlineData(255, 0, 0, 0.62796, 0.22486, 0.12585)]
    [InlineData(0, 255, 0, 0.86644, -0.23389, 0.17950)]
    [InlineData(0, 0, 255, 0.45201, -0.03246, -0.31153)]
    [InlineData(255, 255, 255, 1.0, 0.0, 0.0)]
    public void FromSrgb_MatchesReferenceValues(byte r, byte g, byte b, double l, double a, double bb)
    {
        OkLab.FromSrgb(r, g, b, out var ol, out var oa, out var ob);
        Assert.Equal(l, ol, 1e-4);
        Assert.Equal(a, oa, 1e-4);
        Assert.Equal(bb, ob, 1e-4);
    }

    [Fact]
    public void ToHex_RoundTripsEverySrgbGreyAndPrimaries()
    {
        for (var v = 0; v < 256; v += 5)
        {
            OkLab.FromSrgb((byte)v, (byte)(255 - v), (byte)(v / 2), out var l, out var a, out var b);
            Assert.Equal($"#{v:X2}{255 - v:X2}{v / 2:X2}", OkLab.ToHex(l, a, b));
        }
    }

    [Fact]
    public void ConvertRow_MatchesScalarConversion()
    {
        // 32^3 sRGB grid plus pure black/white; odd length exercises the scalar tail too.
        var n = 32 * 32 * 32 + 3;
        var r = new float[n];
        var g = new float[n];
        var b = new float[n];
        var src = new (byte R, byte G, byte B)[n];
        var i = 0;
        for (var ri = 0; ri < 32; ri++)
        {
            for (var gi = 0; gi < 32; gi++)
            {
                for (var bi = 0; bi < 32; bi++)
                {
                    src[i++] = ((byte)(ri * 255 / 31), (byte)(gi * 255 / 31), (byte)(bi * 255 / 31));
                }
            }
        }

        src[i++] = (0, 0, 0);
        src[i++] = (255, 255, 255);
        src[i] = (1, 2, 3);
        for (var k = 0; k < n; k++)
        {
            r[k] = OkLab.SrgbToLinear[src[k].R];
            g[k] = OkLab.SrgbToLinear[src[k].G];
            b[k] = OkLab.SrgbToLinear[src[k].B];
        }

        var l = new float[n];
        var a = new float[n];
        var bb = new float[n];
        var c = new float[n];
        var h = new float[n];
        OkLab.ConvertRow(r, g, b, l, a, bb, c, h);

        for (var k = 0; k < n; k++)
        {
            OkLab.FromSrgb(src[k].R, src[k].G, src[k].B, out var el, out var ea, out var eb);
            Assert.Equal(Math.Clamp(el, 0f, 1f), l[k], 5e-6f);
            Assert.Equal(ea, a[k], 5e-6f);
            Assert.Equal(eb, bb[k], 5e-6f);
            Assert.Equal(MathF.Sqrt(ea * ea + eb * eb), c[k], 5e-6f);
            if (c[k] > 1e-3f)
            {
                var diff = Math.Abs(OkLab.HueDegrees(ea, eb) - h[k]);
                Assert.True(Math.Min(diff, 360f - diff) < 0.05f, $"hue mismatch at {src[k]}: {h[k]}");
            }

            Assert.InRange(h[k], 0f, 360f);
            Assert.True(h[k] < 360f);
        }
    }

    [Fact]
    public void AreaResampler_ExactForIntegerReplication()
    {
        var small = TestImages.Scene(40, 30, seed: 5);
        var large = TestImages.Replicate(small, 40, 30, 3);
        var (w, h) = AreaResampler.TargetSize(120, 90, 40);
        Assert.Equal((40, 30), (w, h));

        var dst = new byte[w * h * 3];
        AreaResampler.Resize(large, 120, 90, dst, w, h);
        Assert.Equal(small, dst);
    }

    [Theory]
    [InlineData(6000, 4000, 320, 320, 213)]
    [InlineData(4000, 6000, 320, 213, 320)]
    [InlineData(320, 213, 320, 320, 213)]
    [InlineData(100, 50, 320, 100, 50)]
    [InlineData(5000, 10, 320, 320, 1)]
    public void AreaResampler_TargetSize(int sw, int sh, int edge, int ew, int eh) =>
        Assert.Equal((ew, eh), AreaResampler.TargetSize(sw, sh, edge));

    [Fact]
    public void AreaResampler_AveragesNonIntegerRatios()
    {
        var src = TestImages.GreyRamp(1000, 10, 0, 255);
        var dst = new byte[320 * 3 * 3];
        AreaResampler.Resize(src, 1000, 10, dst, 320, 3);
        for (var x = 1; x < 320; x++)
        {
            Assert.True(dst[x * 3] >= dst[(x - 1) * 3]);
        }

        Assert.InRange(dst[0], 0, 2);
        Assert.InRange(dst[319 * 3], 253, 255);
        Assert.InRange(dst[160 * 3], 125, 131);
    }
}
