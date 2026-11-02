using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Composition;
using Xunit.Abstractions;
using static PhotoClippy.Composition.Tests.TestScenes;

namespace PhotoClippy.Composition.Tests;

public sealed class GenrePriorTests(ITestOutputHelper output)
{
    private const int W = 384;
    private const int H = 256;
    private const double Aspect = (double)W / H;

    private static readonly Frame Neutral = Render(W, H, (u, v) =>
    {
        double n = Smooth(u, v, Aspect, 4, 1);
        return (0.35 + (0.3 * n), 0.4 + (0.25 * n), 0.3 + (0.2 * n));
    });

    [Fact]
    public void SuperTelephoto_FavoursWildlifeAndBirds()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Neutral, Facts(focal35: 600, fNumber: 6.3, exposureS: 1.0 / 2000, iso: 800, ev100: 12));
        string[] top = Top(genres, 2);
        Assert.Contains("wildlife", top);
        Assert.Contains("birds", top);
    }

    [Fact]
    public void SoftwareDevice_FavoursScreenshots()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Render(W, H, ScreenshotScene()), Facts(DeviceClass.Software));
        Assert.Equal("screenshots", Top(genres, 1)[0]);
        Assert.True(genres["screenshots"] >= 0.3);
    }

    [Fact]
    public void NoCameraData_FlatColourlessFrame_FavoursScreenshots()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Render(W, H, ScreenshotScene()), facts: null);
        Assert.Equal("screenshots", Top(genres, 1)[0]);
    }

    [Fact]
    public void Scanner_FavoursDocuments()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Render(W, H, ScreenshotScene()), Facts(DeviceClass.Scanner));
        Assert.Equal("documents", Top(genres, 1)[0]);
    }

    [Fact]
    public void LongDarkWideExposure_FavoursAstro()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Render(W, H, StarScene(Aspect)), Facts(focal35: 20, fNumber: 2.0, exposureS: 20, iso: 3200, ev100: -2.4));
        Assert.Equal("astro", Top(genres, 1)[0]);
        Assert.True(genres.ContainsKey("night"));
    }

    [Fact]
    public void LowLight_FavoursNight()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Neutral, Facts(focal35: 50, fNumber: 4, exposureS: 0.5, iso: 1600, ev100: 2.5));
        Assert.Equal("night", Top(genres, 1)[0]);
    }

    [Fact]
    public void FastPortraitLens_WithIsolatedSubject_FavoursPortrait()
    {
        double aspect = 256.0 / 384.0;
        Frame frame = Render(256, 384, (u, v) =>
        {
            if (Dist(u, v, 0.5, 0.42, aspect) <= 0.16)
            {
                double t = Blocky(u, v, aspect, 80, 5);
                return (0.75 + (0.2 * t), 0.5 + (0.2 * t), 0.4 + (0.15 * t));
            }

            double n = Smooth(u, v, aspect, 4, 6);
            return (0.15 + (0.2 * n), 0.25 + (0.25 * n), 0.2 + (0.2 * n));
        });
        CompositionFeatures f = Analyze(frame, Facts(focal35: 85, fNumber: 1.8, exposureS: 1.0 / 250, iso: 200, ev100: 11));
        output.WriteLine(CompositionFeatureTests.Describe(f));
        Assert.NotNull(f.Genres);
        Assert.Equal("portrait", Top(f.Genres!, 1)[0]);
        Assert.True(f.Genres!["portrait"] >= 0.25, $"portrait {f.Genres["portrait"]}");
    }

    [Fact]
    public void UltraWideWithHorizon_FavoursLandscape()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Render(W, H, HorizonScene(0.55, 1, Aspect)), Facts(focal35: 16, fNumber: 8, exposureS: 1.0 / 125, iso: 100, ev100: 13));
        Assert.Equal("landscape", Top(genres, 1)[0]);
    }

    [Fact]
    public void MacroLens_FavoursMacro()
    {
        IReadOnlyDictionary<string, double> genres = Priors(Neutral, Facts(focal35: 100, fNumber: 8, ev100: 10, lens: "FE 90mm F2.8 Macro G OSS"));
        Assert.Equal("macro", Top(genres, 1)[0]);
    }

    [Fact]
    public void NoMatchingRule_EmitsNoPriors()
    {
        CompositionFeatures f = Analyze(Neutral, Facts(focal35: 50, fNumber: 5.6, exposureS: 1.0 / 200, iso: 100, ev100: 12));
        Assert.Null(f.Genres);
        Assert.False(f.Extras.ContainsKey("composition.priorMass"));
    }

    [Fact]
    public void Priors_RespectTaxonomyIds()
    {
        var reduced = new GenreTaxonomy
        {
            Version = "test",
            Genres = TaxonomyWithout("birds", "sports"),
        };
        var analyzer = new HeuristicCompositionAnalyzer(taxonomy: reduced);
        CompositionFeatures f = Analyze(Neutral, Facts(focal35: 600, exposureS: 1.0 / 4000, ev100: 12), analyzer);

        Assert.NotNull(f.Genres);
        Assert.False(f.Genres!.ContainsKey("birds"));
        Assert.False(f.Genres.ContainsKey("sports"));
        Assert.Equal("wildlife", Top(f.Genres, 1)[0]);

        var empty = new HeuristicCompositionAnalyzer(taxonomy: new GenreTaxonomy { Version = "empty" });
        Assert.Null(Analyze(Neutral, Facts(focal35: 600), empty).Genres);
        Assert.Null(Analyze(Neutral, Facts(focal35: 600), new HeuristicCompositionAnalyzer()).Genres);
    }

    [Fact]
    public void Priors_AreBoundedAndUseKnownIds_ForManyFactCombinations()
    {
        // Small frames keep ~1700 analyses fast; the bounds do not depend on resolution.
        const int w = 120, h = 80;
        Frame[] frames =
        [
            Render(w, h, ScreenshotScene(), 2), Render(w, h, StarScene(Aspect), 2), Render(w, h, HorizonScene(0.5, 0, Aspect), 2),
            Render(w, h, VerticalStripes(), 2), Render(w, h, SeparationScene(Aspect, false), 2), Render(w, h, DiscScene(0.5, 0.5, 0.15, Aspect), 2),
        ];
        DeviceClass[] devices = Enum.GetValues<DeviceClass>();
        double?[] focals = [null, 14, 24, 35, 85, 200, 400, 800];
        double?[] evs = [null, -3, 2, 6, 12];
        int checkedCount = 0;
        foreach (Frame frame in frames)
        {
            foreach (DeviceClass device in devices)
            {
                foreach (double? focal in focals)
                {
                    foreach (double? ev in evs)
                    {
                        CaptureFacts facts = Facts(device, focal, fNumber: 1.8, exposureS: ev < 0 ? 30 : 1.0 / 2000, iso: 6400, ev100: ev, flash: ev is 6, lens: focal is 85 ? "Macro" : null);
                        CompositionFeatures f = Analyze(frame, facts);
                        if (f.Genres is null)
                        {
                            continue;
                        }

                        double sum = 0;
                        foreach (KeyValuePair<string, double> kv in f.Genres)
                        {
                            Assert.NotNull(Taxonomy.Find(kv.Key));
                            Assert.InRange(kv.Value, 0.0, 0.45);
                            sum += kv.Value;
                        }

                        Assert.True(sum <= 0.6 + 1e-9, $"sum {sum}");
                        Assert.Equal(Math.Round(sum, 4), f.Extras["composition.priorMass"], 3);
                        checkedCount++;
                    }
                }
            }
        }

        output.WriteLine($"{checkedCount} prior sets checked");
        Assert.True(checkedCount > 100);
    }

    [Fact]
    public void EmitGenrePriorsFalse_GivesNullGenres()
    {
        var analyzer = Analyzer(new HeuristicCompositionSettings { EmitGenrePriors = false });
        CompositionFeatures f = Analyze(Neutral, Facts(focal35: 600), analyzer);
        Assert.Null(f.Genres);
        Assert.DoesNotContain("genre-priors", analyzer.Manifest.Capabilities);
        Assert.Contains("genre-priors", Analyzer().Manifest.Capabilities);
    }

    private static IReadOnlyDictionary<string, double> Priors(Frame frame, CaptureFacts? facts)
    {
        CompositionFeatures f = Analyze(frame, facts);
        Assert.NotNull(f.Genres);
        return f.Genres!;
    }

    private static string[] Top(IReadOnlyDictionary<string, double> genres, int n)
    {
        var list = new List<KeyValuePair<string, double>>(genres);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var top = new string[Math.Min(n, list.Count)];
        for (int i = 0; i < top.Length; i++)
        {
            top[i] = list[i].Key;
        }

        return top;
    }

    private static List<GenreDefinition> TaxonomyWithout(params string[] ids)
    {
        var list = new List<GenreDefinition>();
        foreach (GenreDefinition g in Taxonomy.Genres)
        {
            if (Array.IndexOf(ids, g.Id) < 0)
            {
                list.Add(g);
            }
        }

        return list;
    }
}
