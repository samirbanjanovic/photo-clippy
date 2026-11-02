using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Composition;
using Xunit.Abstractions;
using static PhotoClippy.Composition.Tests.TestScenes;

namespace PhotoClippy.Composition.Tests;

public sealed class CompositionFeatureTests(ITestOutputHelper output)
{
    private const int W = 384;
    private const int H = 256;
    private const double Aspect = (double)W / H;

    [Fact]
    public void CentredDisc_IsCentredSubject()
    {
        CompositionFeatures f = Analyze(Render(W, H, DiscScene(0.5, 0.5, 0.15, Aspect)));
        output.WriteLine(Describe(f));

        Assert.NotNull(f.SubjectX);
        Assert.InRange(f.SubjectX!.Value, 0.48, 0.52);
        Assert.InRange(f.SubjectY!.Value, 0.48, 0.52);
        Assert.InRange(f.SubjectArea!.Value, 0.03, 0.10);
        Assert.True(f.CenterScore > 0.8, $"CenterScore {f.CenterScore}");
        Assert.True(f.ThirdsScore < 0.3, $"ThirdsScore {f.ThirdsScore}");
        Assert.Null(f.HorizonTiltDeg);
        Assert.Null(f.HorizonY);
        Assert.Equal(1.0, f.Extras["composition.subjectCount"]);
    }

    [Theory]
    [InlineData(1.0 / 3.0, 1.0 / 3.0)]
    [InlineData(2.0 / 3.0, 2.0 / 3.0)]
    [InlineData(1.0 / 3.0, 2.0 / 3.0)]
    public void DiscAtPowerPoint_ScoresThirds(double cu, double cv)
    {
        CompositionFeatures f = Analyze(Render(W, H, DiscScene(cu, cv, 0.1, Aspect)));
        output.WriteLine(Describe(f));

        Assert.InRange(f.SubjectX!.Value, cu - 0.02, cu + 0.02);
        Assert.InRange(f.SubjectY!.Value, cv - 0.02, cv + 0.02);
        Assert.True(f.ThirdsScore > 0.8, $"ThirdsScore {f.ThirdsScore}");
        Assert.True(f.CenterScore < 0.5, $"CenterScore {f.CenterScore}");
    }

    [Fact]
    public void MirrorSymmetricScene_IsSymmetric_RandomSceneIsNot()
    {
        CompositionFeatures mirror = Analyze(Render(W, H, MirrorScene(Aspect)));
        CompositionFeatures noise = Analyze(Render(W, H, NoiseScene(Aspect)));
        output.WriteLine($"mirror {mirror.SymmetryScore}, noise {noise.SymmetryScore}");

        Assert.True(mirror.SymmetryScore > 0.85, $"mirror {mirror.SymmetryScore}");
        Assert.True(noise.SymmetryScore < 0.6, $"noise {noise.SymmetryScore}");
    }

    [Theory]
    [InlineData(0.6, 4.0)]
    [InlineData(0.45, 0.0)]
    [InlineData(0.55, -6.0)]
    [InlineData(0.35, 2.0)]
    public void TiltedHorizon_IsMeasured(double splitY, double tilt)
    {
        CompositionFeatures f = Analyze(Render(W, H, HorizonScene(splitY, tilt, Aspect)));
        output.WriteLine(Describe(f));

        Assert.NotNull(f.HorizonTiltDeg);
        Assert.InRange(f.HorizonTiltDeg!.Value, tilt - 1.5, tilt + 1.5);
        Assert.InRange(f.HorizonY!.Value, splitY - 0.03, splitY + 0.03);
        Assert.True(f.Extras["composition.horizonSpan"] >= 0.55);
        Assert.True(f.Extras["composition.brightSky"] > 0.3, $"brightSky {f.Extras["composition.brightSky"]}");
    }

    [Fact]
    public void NoHorizon_InDiscNoiseOrStripes()
    {
        Assert.Null(Analyze(Render(W, H, DiscScene(0.5, 0.5, 0.15, Aspect))).HorizonTiltDeg);
        Assert.Null(Analyze(Render(W, H, NoiseScene(Aspect))).HorizonTiltDeg);
        Assert.Null(Analyze(Render(W, H, VerticalStripes())).HorizonTiltDeg);
        Assert.Null(Analyze(Render(W, H, Flat(0.5, 0.5, 0.5))).HorizonTiltDeg);
    }

    [Fact]
    public void SharpSubjectOverBlurredBackground_IsSeparated()
    {
        CompositionFeatures blurred = Analyze(Render(W, H, SeparationScene(Aspect, sharpBackground: false)));
        CompositionFeatures sharp = Analyze(Render(W, H, SeparationScene(Aspect, sharpBackground: true)));
        output.WriteLine($"blurred bg: {Describe(blurred)}");
        output.WriteLine($"sharp bg:   {Describe(sharp)}");

        Assert.NotNull(blurred.SeparationScore);
        Assert.True(blurred.SeparationScore >= 0.7, $"blurred {blurred.SeparationScore}");
        Assert.NotNull(sharp.SubjectX);
        Assert.True(sharp.SeparationScore <= 0.3, $"sharp {sharp.SeparationScore}");
    }

    [Fact]
    public void MostlyEmptyFrame_HasHighNegativeSpace()
    {
        CompositionFeatures empty = Analyze(Render(W, H, EmptyScene(Aspect)));
        CompositionFeatures busy = Analyze(Render(W, H, NoiseScene(Aspect)));
        output.WriteLine($"empty {empty.NegativeSpace}, busy {busy.NegativeSpace}");

        Assert.True(empty.NegativeSpace >= 0.85, $"empty {empty.NegativeSpace}");
        Assert.True(busy.NegativeSpace <= 0.2, $"busy {busy.NegativeSpace}");
        Assert.NotNull(empty.SubjectX);
        Assert.InRange(empty.SubjectX!.Value, 0.64, 0.69);
    }

    [Fact]
    public void Stripes_EdgeOrientation()
    {
        CompositionFeatures vertical = Analyze(Render(W, H, VerticalStripes()));
        CompositionFeatures horizontal = Analyze(Render(W, H, HorizontalStripes()));
        output.WriteLine($"vertical: {Describe(vertical)}");
        output.WriteLine($"horizontal: {Describe(horizontal)}");

        Assert.True(vertical.VerticalEdges > 0.8, $"vertical {vertical.VerticalEdges}");
        Assert.True(horizontal.HorizontalEdges > 0.8, $"horizontal {horizontal.HorizontalEdges}");
        foreach (CompositionFeatures f in new[] { vertical, horizontal, Analyze(Render(W, H, NoiseScene(Aspect))) })
        {
            Assert.InRange(f.HorizontalEdges!.Value + f.VerticalEdges!.Value + f.DiagonalEdges!.Value, 0.999, 1.001);
        }
    }

    [Fact]
    public void FlatOrUniformlyTexturedFrame_HasNoSubject()
    {
        CompositionFeatures flat = Analyze(Render(W, H, Flat(0.4, 0.5, 0.6)));
        CompositionFeatures texture = Analyze(Render(W, H, (u, v) =>
        {
            double t = Blocky(u, v, Aspect, 40, 13);
            return (0.3 + (0.4 * t), 0.3 + (0.4 * t), 0.3 + (0.4 * t));
        }));
        output.WriteLine($"flat: {Describe(flat)}");
        output.WriteLine($"texture: {Describe(texture)}");

        foreach (CompositionFeatures f in new[] { flat, texture })
        {
            Assert.Null(f.SubjectX);
            Assert.Null(f.SubjectY);
            Assert.Null(f.SubjectArea);
            Assert.Null(f.ThirdsScore);
            Assert.Null(f.CenterScore);
            Assert.Null(f.SeparationScore);
            Assert.Equal(0.0, f.Extras["composition.subjectCount"]);
        }

        Assert.Null(flat.HorizontalEdges);
        Assert.True(flat.NegativeSpace > 0.99);
        Assert.True(flat.SymmetryScore > 0.99);
    }

    [Fact]
    public void ScoresAreInRange_AndExtrasArePrefixed()
    {
        var scenes = new[]
        {
            DiscScene(0.3, 0.6, 0.12, Aspect), HorizonScene(0.5, 3, Aspect), MirrorScene(Aspect), NoiseScene(Aspect),
            SeparationScene(Aspect, false), EmptyScene(Aspect), VerticalStripes(), ScreenshotScene(), StarScene(Aspect),
        };
        foreach (Shader scene in scenes)
        {
            CompositionFeatures f = Analyze(Render(W, H, scene));
            foreach ((string name, double? value) in Fields(f))
            {
                if (value is null || name == nameof(CompositionFeatures.HorizonTiltDeg))
                {
                    continue;
                }

                Assert.InRange(value.Value, 0.0, 1.0);
            }

            if (f.HorizonTiltDeg is double tilt)
            {
                Assert.InRange(tilt, -20.0, 20.0);
            }

            foreach (string key in f.Extras.Keys)
            {
                Assert.StartsWith("composition.", key, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void LargerFrame_GivesSameResults()
    {
        Shader[] scenes =
        [
            DiscScene(1.0 / 3.0, 2.0 / 3.0, 0.12, Aspect),
            HorizonScene(0.6, 4.0, Aspect),
            SeparationScene(Aspect, false),
            EmptyScene(Aspect),
            MirrorScene(Aspect),
        ];
        foreach (Shader scene in scenes)
        {
            CompositionFeatures small = Analyze(Render(W, H, scene));
            CompositionFeatures large = Analyze(Render(1536, 1024, scene, supersample: 1));
            output.WriteLine($"384:  {Describe(small)}");
            output.WriteLine($"1536: {Describe(large)}");

            foreach (((string name, double? a), (_, double? b)) in Fields(small).Zip(Fields(large)))
            {
                Assert.True(a.HasValue == b.HasValue, $"{name}: {a} vs {b}");
                if (a is double x && b is double y)
                {
                    double tol = name == nameof(CompositionFeatures.HorizonTiltDeg) ? 0.5 : 0.08;
                    Assert.True(Math.Abs(x - y) <= tol, $"{name}: {x} vs {y}");
                }
            }
        }
    }

    [Fact]
    public void SmallWorkingEdge_StillWorks()
    {
        var analyzer = Analyzer(new HeuristicCompositionSettings { WorkingEdge = 128 });
        CompositionFeatures f = Analyze(Render(W, H, DiscScene(0.5, 0.5, 0.15, Aspect)), analyzer: analyzer);
        Assert.True(f.CenterScore > 0.8);
        CompositionFeatures h = Analyze(Render(W, H, HorizonScene(0.6, 4.0, Aspect)), analyzer: analyzer);
        Assert.InRange(h.HorizonTiltDeg!.Value, 2.5, 5.5);
    }

    [Fact]
    public void TinyFrame_ReturnsEmptyFeatures()
    {
        CompositionFeatures f = Analyze(Render(8, 8, DiscScene(0.5, 0.5, 0.2, 1)));
        Assert.Null(f.SubjectX);
        Assert.Null(f.SymmetryScore);
        Assert.Null(f.HorizontalEdges);
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Analyzer().AnalyzeAsync(Render(64, 64, Flat(0.5, 0.5, 0.5)), Context(), cts.Token));
    }

    internal static IEnumerable<(string Name, double? Value)> Fields(CompositionFeatures f)
    {
        yield return (nameof(f.SubjectX), f.SubjectX);
        yield return (nameof(f.SubjectY), f.SubjectY);
        yield return (nameof(f.SubjectArea), f.SubjectArea);
        yield return (nameof(f.ThirdsScore), f.ThirdsScore);
        yield return (nameof(f.CenterScore), f.CenterScore);
        yield return (nameof(f.SymmetryScore), f.SymmetryScore);
        yield return (nameof(f.NegativeSpace), f.NegativeSpace);
        yield return (nameof(f.SeparationScore), f.SeparationScore);
        yield return (nameof(f.HorizonTiltDeg), f.HorizonTiltDeg);
        yield return (nameof(f.HorizonY), f.HorizonY);
        yield return (nameof(f.HorizontalEdges), f.HorizontalEdges);
        yield return (nameof(f.VerticalEdges), f.VerticalEdges);
        yield return (nameof(f.DiagonalEdges), f.DiagonalEdges);
    }

    internal static string Describe(CompositionFeatures f)
    {
        var parts = new List<string>();
        foreach ((string name, double? value) in Fields(f))
        {
            parts.Add($"{name}={value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        }

        foreach (KeyValuePair<string, double> kv in f.Extras)
        {
            parts.Add($"{kv.Key["composition.".Length..]}={kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return string.Join(' ', parts);
    }
}
