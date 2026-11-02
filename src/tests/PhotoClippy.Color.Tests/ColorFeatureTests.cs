using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Color;
using static PhotoClippy.Color.Tests.TestImages;

namespace PhotoClippy.Color.Tests;

public sealed class ColorFeatureTests
{
    private static readonly AnalysisContext Context = new() { CaptureId = "test", SourcePath = "test.jpg" };

    private static ColorFeatures Analyze(byte[] rgb, int width = W, int height = H, ColorSettings? settings = null) =>
        new ColorAnalyzer(settings).AnalyzeAsync(ToFrame(rgb, width, height), Context).AsTask().GetAwaiter().GetResult();

    [Fact]
    public void SolidGrey_IsMonochromeFlatAndHasNoHueHistogram()
    {
        var f = Analyze(Solid(W, H, 128, 128, 128));

        Assert.True(f.Monochrome);
        Assert.Null(f.HueHistogram);
        Assert.Equal(0.0, f.Contrast!.Value, 9);
        Assert.Equal(0.5999, f.LumaP50!.Value, 3);
        Assert.Equal(f.LumaP50!.Value, f.LumaP05!.Value, 9);
        Assert.Equal(f.LumaP50!.Value, f.LumaP95!.Value, 9);
        Assert.Equal(f.LumaP50!.Value, f.MeanLuma!.Value, 1e-5);
        Assert.Equal("mid", f.Key);
        Assert.Equal(0.0, f.Colorfulness!.Value, 9);
        Assert.True(f.MeanChroma < 1e-3);
        Assert.Equal(0.0, f.Saturation!.Value, 9);
        Assert.Equal(0.0, f.Sharpness!.Value, 9);
        Assert.Equal(0.0, f.ShadowClip!.Value);
        Assert.Equal(0.0, f.HighlightClip!.Value);
        Assert.InRange(f.Warmth!.Value, -0.01, 0.01);
        Assert.InRange(f.Tint!.Value, -0.01, 0.01);

        Assert.Equal(16, f.LumaHistogram!.Count);
        Assert.Equal(1.0, f.LumaHistogram[9], 9);

        var palette = Assert.Single(f.Palette!);
        Assert.Equal("#808080", palette.Hex);
        Assert.Equal(1.0, palette.Weight, 9);

        Assert.Equal(1.0, f.Extras["color.neutralShare"], 9);
        Assert.Equal(0.0, f.Extras["color.skyBlueShare"]);
        Assert.False(f.Extras.ContainsKey("color.hueConcentration"));
    }

    [Theory]
    [InlineData(255, 0, 0, 1, "#FF0000")]
    [InlineData(0, 255, 0, 5, "#00FF00")]
    [InlineData(0, 0, 255, 9, "#0000FF")]
    [InlineData(255, 255, 0, 4, "#FFFF00")]
    [InlineData(0, 255, 255, 6, "#00FFFF")]
    [InlineData(255, 0, 255, 11, "#FF00FF")]
    public void SolidSaturatedColour_LandsInExpectedHueBin(byte r, byte g, byte b, int expectedBin, string hex)
    {
        var f = Analyze(Solid(W, H, r, g, b));

        Assert.NotNull(f.HueHistogram);
        Assert.Equal(12, f.HueHistogram!.Count);
        Assert.Equal(1.0, f.HueHistogram[expectedBin], 6);
        Assert.Equal(1.0, f.HueHistogram.Sum(), 9);
        Assert.False(f.Monochrome);
        Assert.Equal(1.0, f.Saturation!.Value, 6);
        Assert.True(f.MeanChroma > 0.15, $"mean chroma {f.MeanChroma}");
        Assert.Equal(1.0, f.Extras["color.hueConcentration"], 6);
        Assert.Equal(hex, Assert.Single(f.Palette!).Hex);
    }

    [Theory]
    [InlineData(255, 0, 0, 0.75)]
    [InlineData(0, 255, 0, 0.75)]
    [InlineData(0, 0, 255, 0.65)]
    public void SolidPrimary_HasHighColorfulness(byte r, byte g, byte b, double atLeast)
    {
        var f = Analyze(Solid(W, H, r, g, b));
        Assert.InRange(f.Colorfulness!.Value, atLeast, 1.0);
    }

    [Fact]
    public void Colorfulness_OrdersMutedBelowVivid()
    {
        var vivid = Analyze(HalfHalf(W, H, (230, 30, 30), (30, 60, 230)));
        var muted = Analyze(HalfHalf(W, H, (140, 125, 120), (120, 125, 140)));
        Assert.True(vivid.Colorfulness > 0.6, $"vivid {vivid.Colorfulness}");
        Assert.True(muted.Colorfulness < 0.3, $"muted {muted.Colorfulness}");
    }

    [Fact]
    public void BlackWhiteHalves_ClipShares()
    {
        var f = Analyze(HalfHalf(W, H, (0, 0, 0), (255, 255, 255)));

        Assert.Equal(0.5, f.ShadowClip!.Value, 9);
        Assert.Equal(0.5, f.HighlightClip!.Value, 9);
        Assert.Equal(1.0, f.Contrast!.Value, 3);
        Assert.True(f.Monochrome);
        Assert.Equal(0.5, f.LumaHistogram![0], 9);
        Assert.Equal(0.5, f.LumaHistogram[15], 9);
    }

    [Fact]
    public void ClipThresholds_AreInclusiveAtThreeAnd252()
    {
        Assert.Equal(1.0, Analyze(Solid(W, H, 3, 3, 3)).ShadowClip);
        Assert.Equal(0.0, Analyze(Solid(W, H, 4, 0, 0)).ShadowClip);
        Assert.Equal(1.0, Analyze(Solid(W, H, 252, 10, 10)).HighlightClip);
        Assert.Equal(0.0, Analyze(Solid(W, H, 251, 251, 251)).HighlightClip);
    }

    [Fact]
    public void Gradients_ClassifyKey()
    {
        var low = Analyze(GreyRamp(W, H, 0, 70));
        var mid = Analyze(GreyRamp(W, H, 0, 255));
        var high = Analyze(GreyRamp(W, H, 185, 255));

        Assert.Equal("low", low.Key);
        Assert.Equal("mid", mid.Key);
        Assert.Equal("high", high.Key);
        Assert.True(low.MeanLuma < mid.MeanLuma && mid.MeanLuma < high.MeanLuma);
        Assert.True(mid.Contrast > 0.8, $"full ramp contrast {mid.Contrast}");
        Assert.True(high.Contrast < low.Contrast);
        Assert.True(low.LumaP05 < low.LumaP50 && low.LumaP50 < low.LumaP95);
        Assert.Equal(1.0, mid.LumaHistogram!.Sum(), 9);
    }

    [Fact]
    public void TintedGreys_WarmthAndTintSigns()
    {
        var warm = Analyze(Solid(W, H, 150, 125, 100));
        var cool = Analyze(Solid(W, H, 100, 125, 150));
        var magenta = Analyze(Solid(W, H, 140, 115, 140));
        var green = Analyze(Solid(W, H, 115, 140, 115));

        Assert.True(warm.Warmth > 0.1, $"warm {warm.Warmth}");
        Assert.True(cool.Warmth < -0.1, $"cool {cool.Warmth}");
        Assert.True(magenta.Tint > 0.1, $"magenta {magenta.Tint}");
        Assert.True(green.Tint < -0.1, $"green {green.Tint}");
        Assert.InRange(warm.Warmth!.Value, -1.0, 1.0);
    }

    [Fact]
    public void SepiaToned_IsMonochrome()
    {
        var f = Analyze(Sepia(W, H));
        Assert.True(f.Monochrome, $"meanChroma {f.MeanChroma}, concentration {f.Extras.GetValueOrDefault("color.hueConcentration")}");
        Assert.True(f.Warmth > 0, $"sepia warmth {f.Warmth}");
        Assert.NotNull(f.HueHistogram);
    }

    [Fact]
    public void CyanotypeToned_IsMonochrome()
    {
        var px = GreyRamp(W, H, 0, 255);
        for (var i = 0; i < px.Length; i += 3)
        {
            var v = px[i] / 255.0;
            px[i] = (byte)Math.Round(255 * Math.Pow(v, 1.25));
            px[i + 1] = (byte)Math.Round(255 * Math.Pow(v, 1.05));
            px[i + 2] = (byte)Math.Round(255 * Math.Pow(v, 0.8));
        }

        var f = Analyze(px);
        Assert.True(f.Monochrome, $"meanChroma {f.MeanChroma}");
        Assert.True(f.Warmth < 0, $"cyanotype warmth {f.Warmth}");
    }

    [Fact]
    public void ColourfulScene_IsNotMonochrome()
    {
        var f = Analyze(Scene(W, H));
        Assert.False(f.Monochrome);
        Assert.InRange(f.Colorfulness!.Value, 0.15, 1.0);
    }

    [Fact]
    public void TwoColourHalves_PaletteHasTwoEqualEntries()
    {
        var f = Analyze(HalfHalf(W, H, (200, 40, 40), (30, 90, 200)));

        Assert.Equal(2, f.Palette!.Count);
        Assert.Equal(1.0, f.Palette.Sum(p => p.Weight), 9);
        var red = f.Palette.Single(p => ParseHex(p.Hex).R > 100);
        var blue = f.Palette.Single(p => ParseHex(p.Hex).B > 100);
        Assert.InRange(red.Weight, 0.49, 0.51);
        Assert.InRange(blue.Weight, 0.49, 0.51);
        AssertHexNear((200, 40, 40), red.Hex);
        AssertHexNear((30, 90, 200), blue.Hex);
    }

    [Fact]
    public void UnequalColours_PaletteIsSortedHeaviestFirstAndDropsTinyClusters()
    {
        var px = Solid(W, H, 20, 120, 40);
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var o = (y * W + x) * 3;
                if (x < W / 4)
                {
                    px[o] = 230;
                    px[o + 1] = 200;
                    px[o + 2] = 40;
                }
                else if (x == W - 1)
                {
                    px[o] = 255;
                    px[o + 1] = 0;
                    px[o + 2] = 255;
                }
            }
        }

        var f = Analyze(px);

        Assert.Equal(2, f.Palette!.Count);
        AssertHexNear((20, 120, 40), f.Palette[0].Hex);
        AssertHexNear((230, 200, 40), f.Palette[1].Hex);
        Assert.InRange(f.Palette[0].Weight, 0.72, 0.78);
        Assert.Equal(1.0, f.Palette.Sum(p => p.Weight), 9);
    }

    [Fact]
    public void NearIdenticalColours_AreMergedIntoOneSwatch()
    {
        var f = Analyze(HalfHalf(W, H, (120, 120, 120), (124, 122, 120)));
        var swatch = Assert.Single(f.Palette!);
        Assert.Equal(1.0, swatch.Weight, 9);
    }

    [Fact]
    public void Scene_PaletteIsWellFormed()
    {
        var settings = new ColorSettings { PaletteSize = 6 };
        var f = Analyze(Scene(W, H), settings: settings);

        Assert.InRange(f.Palette!.Count, 2, 6);
        Assert.Equal(1.0, f.Palette.Sum(p => p.Weight), 9);
        for (var i = 0; i < f.Palette.Count; i++)
        {
            Assert.Matches("^#[0-9A-F]{6}$", f.Palette[i].Hex);
            Assert.True(f.Palette[i].Weight >= 0.02);
            if (i > 0)
            {
                Assert.True(f.Palette[i - 1].Weight >= f.Palette[i].Weight);
            }
        }
    }

    [Fact]
    public void Sharpness_CrispCheckerboardAboveBlurredCopy()
    {
        var crisp = Checkerboard(W, H, 32, 40, 215);
        var blurred = BoxBlur(crisp, W, H, 4);

        var sharp = Analyze(crisp).Sharpness!.Value;
        var soft = Analyze(blurred).Sharpness!.Value;

        Assert.True(sharp > 0.6, $"crisp {sharp}");
        Assert.True(soft < 0.3, $"blurred {soft}");
    }

    [Fact]
    public void Sharpness_DecreasesWithBlurRadius()
    {
        var scene = Scene(W, H, noise: 0);
        var s0 = Analyze(scene).Sharpness!.Value;
        var s1 = Analyze(BoxBlur(scene, W, H, 1)).Sharpness!.Value;
        var s3 = Analyze(BoxBlur(scene, W, H, 3)).Sharpness!.Value;
        Assert.True(s0 > s1 && s1 > s3, $"{s0} {s1} {s3}");
    }

    [Fact]
    public void Scene_Extras_DetectSkyFoliageAndSkin()
    {
        var f = Analyze(Scene(W, H));

        Assert.InRange(f.Extras["color.skyBlueShare"], 0.25, 0.45);
        Assert.True(f.Extras["color.skyBlueTopShare"] > 0.9, $"top {f.Extras["color.skyBlueTopShare"]}");
        Assert.InRange(f.Extras["color.greenShare"], 0.3, 0.6);
        Assert.InRange(f.Extras["color.skinShare"], 0.01, 0.06);
        Assert.True(f.Extras["color.neutralShare"] < 0.1);
        Assert.True(f.Extras["color.lumaStdDev"] > 0.05);
        Assert.All(f.Extras.Keys, k => Assert.StartsWith("color.", k, StringComparison.Ordinal));
    }

    [Fact]
    public void SkinBand_IsConservative()
    {
        Assert.Equal(1.0, Analyze(Solid(W, H, 224, 172, 140)).Extras["color.skinShare"], 9);
        Assert.Equal(1.0, Analyze(Solid(W, H, 141, 85, 36)).Extras["color.skinShare"], 9);
        Assert.Equal(0.0, Analyze(Solid(W, H, 255, 128, 0)).Extras["color.skinShare"]);
        Assert.Equal(0.0, Analyze(Solid(W, H, 230, 200, 40)).Extras["color.skinShare"]);
        Assert.Equal(0.0, Analyze(Solid(W, H, 128, 128, 128)).Extras["color.skinShare"]);
    }

    [Fact]
    public void LargerFrame_ReplicatedTwice_MatchesWorkingSize()
    {
        var small = Scene(W, H, seed: 3);
        var large = Replicate(small, W, H, 2);

        var a = Analyze(small);
        var b = Analyze(large, 2 * W, 2 * H);

        Assert.Equal(TestJson.Serialize(a), TestJson.Serialize(b));
    }

    [Fact]
    public void LargerFrame_SmoothScene_NearlyMatchesWorkingSize()
    {
        var a = Analyze(Scene(W, H, noise: 0));
        var b = Analyze(Scene(1600, 1066, noise: 0), 1600, 1066);

        Assert.Equal(a.MeanLuma!.Value, b.MeanLuma!.Value, 0.01);
        Assert.Equal(a.LumaP05!.Value, b.LumaP05!.Value, 0.01);
        Assert.Equal(a.LumaP50!.Value, b.LumaP50!.Value, 0.01);
        Assert.Equal(a.LumaP95!.Value, b.LumaP95!.Value, 0.01);
        Assert.Equal(a.Contrast!.Value, b.Contrast!.Value, 0.02);
        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.Colorfulness!.Value, b.Colorfulness!.Value, 0.02);
        Assert.Equal(a.MeanChroma!.Value, b.MeanChroma!.Value, 0.005);
        Assert.Equal(a.Saturation!.Value, b.Saturation!.Value, 0.02);
        Assert.Equal(a.Warmth!.Value, b.Warmth!.Value, 0.03);
        Assert.Equal(a.Tint!.Value, b.Tint!.Value, 0.03);
        Assert.Equal(a.Monochrome, b.Monochrome);
        Assert.InRange(Math.Abs(a.Sharpness!.Value - b.Sharpness!.Value), 0.0, 0.2);
        for (var i = 0; i < 12; i++)
        {
            Assert.InRange(Math.Abs(a.HueHistogram![i] - b.HueHistogram![i]), 0.0, 0.03);
        }

        for (var i = 0; i < 16; i++)
        {
            Assert.InRange(Math.Abs(a.LumaHistogram![i] - b.LumaHistogram![i]), 0.0, 0.03);
        }

        Assert.Equal(a.Palette!.Count, b.Palette!.Count);
        for (var i = 0; i < a.Palette.Count; i++)
        {
            AssertHexNear(ParseHex(a.Palette[i].Hex), b.Palette[i].Hex, 8);
            Assert.InRange(Math.Abs(a.Palette[i].Weight - b.Palette[i].Weight), 0.0, 0.05);
        }

        foreach (var (key, value) in a.Extras)
        {
            Assert.InRange(Math.Abs(value - b.Extras[key]), 0.0, 0.03);
        }
    }

    [Fact]
    public void TinyFrames_AreHandled()
    {
        var one = Analyze(Solid(1, 1, 10, 200, 30), 1, 1);
        Assert.Null(one.Sharpness);
        Assert.Equal("#0AC81E", Assert.Single(one.Palette!).Hex);

        var tall = Analyze(Scene(30, 900), 30, 900);
        Assert.NotNull(tall.Sharpness);
        Assert.Equal(1.0, tall.LumaHistogram!.Sum(), 9);
    }

    private static void AssertHexNear((int R, int G, int B) expected, string hex, int tolerance = 2)
    {
        var (r, g, b) = ParseHex(hex);
        Assert.True(
            Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance && Math.Abs(b - expected.B) <= tolerance,
            $"expected ~({expected.R},{expected.G},{expected.B}) got {hex}");
    }
}
