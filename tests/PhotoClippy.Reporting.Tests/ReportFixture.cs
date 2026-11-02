using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Profiling;
using PhotoClippy.Plugins.Recommendation;

namespace PhotoClippy.Reporting.Tests;

/// <summary>Builds report models the same way the CLI does: synthetic captures → ProfileBuilder → MCDA recommender → insights.</summary>
internal static class ReportFixture
{
    public const string Candidates = "24/1.4 640g $1400, 14-24/2.8 650g $2000, 24-70/2.8 805g $2300, 35/1.2 1060g $2800, 50/1.2 1.1kg $2100";

    /// <summary>Lens name with characters that must be escaped in HTML and quoted in CSV.</summary>
    public const string HostileLensName = "Laowa 9mm \"W-Dreamer\", <b>FF</b> & co";

    private const string NikonKey = "nikon|z 6ii";
    private const string FujiKey = "fujifilm|x-t3";

    private static readonly Lazy<GenreTaxonomy> LazyTaxonomy = new(LoadTaxonomy);
    private static readonly Lazy<ReportModel> LazyFull = new(() => BuildAsync(Candidates).GetAwaiter().GetResult());

    public static GenreTaxonomy Taxonomy => LazyTaxonomy.Value;

    /// <summary>~400 captures over two bodies and four lenses, frame features for most, and a ranked recommendation.</summary>
    public static ReportModel Full => LazyFull.Value;

    /// <summary>No captures at all, no recommendation: what a run over an empty folder produces.</summary>
    public static ReportModel Empty()
    {
        var result = new ProfileBuilder(Taxonomy, new ProfileBuilderOptions()).Build(new LibraryInfo { Roots = ["empty"] });
        return new ReportModel
        {
            Run = Run(),
            Profile = result.Profile,
            Taxonomy = Taxonomy,
            Insights = InsightGenerator.Generate(result.Profile, null, Taxonomy),
        };
    }

    public static async Task<ReportModel> BuildAsync(string candidates, int count = 400, int seed = 7)
    {
        var (lensSpecs, bodies) = Gear();
        var builder = new ProfileBuilder(Taxonomy, new ProfileBuilderOptions { LensSpecs = lensSpecs, Bodies = bodies });
        builder.AddGenreSource("test");
        var rng = new Random(seed);
        var start = new DateTime(2021, 3, 1, 9, 0, 0, DateTimeKind.Unspecified);
        string[] genres = ["landscape", "cityscape", "street", "portrait", "food", "pets"];

        for (var i = 0; i < count; i++)
        {
            var nikon = i % 3 != 0;
            var (lensKey, display, minF, maxF, ap) = nikon
                ? (i % 11 == 0
                    ? ("laowa 9mm", HostileLensName, 9.0, 9.0, 5.6)
                    : i % 4 == 0 ? ("nikkor z 50mm f/1.8 s", "NIKKOR Z 50mm f/1.8 S", 50.0, 50.0, 1.8) : ("nikkor z 24-70mm f/4 s", "NIKKOR Z 24-70mm f/4 S", 24.0, 70.0, 4.0))
                : ("xf35mmf1.4 r", "XF35mmF1.4 R", 35.0, 35.0, 1.4);
            var crop = nikon ? 1.0 : 1.5;
            var focal = minF == maxF ? minF : rng.NextDouble() < 0.45 ? minF : minF + (rng.NextDouble() * (maxF - minF));
            var fNumber = Math.Round(ap * (rng.NextDouble() < 0.5 ? 1 : 1 + (rng.NextDouble() * 3)), 1);
            var iso = (int)(100 * Math.Pow(2, rng.Next(0, 7)));
            var exposure = 1.0 / (30 * Math.Pow(2, rng.Next(0, 6)));
            var genre = genres[rng.Next(genres.Length)];
            var facts = new CaptureFacts
            {
                CaptureId = "c" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                PrimaryPath = $"/lib/{i}.jpg",
                Kinds = [MediaKind.Jpeg],
                CaptureLocal = start.AddDays(i * 3.7).AddHours(rng.Next(0, 12)),
                Device = DeviceClass.InterchangeableLens,
                BodyKey = nikon ? NikonKey : FujiKey,
                BodyDisplay = nikon ? "Nikon Z 6II" : "Fujifilm X-T3",
                Make = nikon ? "Nikon" : "Fujifilm",
                SensorFormat = nikon ? "FF" : "APS-C",
                CropFactor = crop,
                LensKey = lensKey,
                LensDisplay = display,
                LensIsZoom = maxF > minF,
                LensMinFocalMm = minF,
                LensMaxFocalMm = maxF,
                LensMaxApertureWide = ap,
                LensMaxApertureTele = ap,
                FocalMm = focal,
                Focal35Mm = focal * crop,
                FNumber = fNumber,
                ExposureS = exposure,
                Iso = iso,
                Ev100 = Math.Log2(fNumber * fNumber / exposure) - Math.Log2(iso / 100.0),
                Width = 6000,
                Height = i % 5 == 0 ? 6000 : 4000,
            };

            var analysed = i % 7 != 0;
            builder.Add(new CaptureAnalysis
            {
                Facts = facts,
                InScope = true,
                Genre = analysed ? genre : null,
                GenreConfidence = analysed ? 0.6 + (rng.NextDouble() * 0.4) : 0,
                Composition = analysed ? Composition(rng, genre) : null,
                Color = analysed ? Color(rng) : null,
            });
        }

        var result = builder.Build(new LibraryInfo { Roots = ["photos"], FilesSeen = count + 10, FilesWithMetadata = count, RawPlusJpeg = 12 });
        var profile = result.Profile;
        RecommendationSet? set = null;
        if (!string.IsNullOrWhiteSpace(candidates))
        {
            var target = result.Systems.First(s => s.BodyKey == NikonKey);
            var list = LensSpec.ParseList(candidates, out var errors);
            Assert.Empty(errors);
            profile = profile with { Target = target };
            set = await new McdaRecommender(taxonomy: Taxonomy).RecommendAsync(new RecommendationRequest
            {
                Profile = profile,
                Target = target,
                Samples = result.Samples,
                Candidates = list,
                Owned = result.OwnedByBody.GetValueOrDefault(NikonKey) ?? [],
                Taxonomy = Taxonomy,
                Preferences = new RecommendationPreferences { BootstrapReplicates = 50, Seed = 1729 },
            });
        }

        return new ReportModel
        {
            Run = Run(),
            Profile = profile,
            Recommendation = set,
            Taxonomy = Taxonomy,
            Insights = InsightGenerator.Generate(profile, set, Taxonomy),
        };
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clippy-report-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RunInfo Run() => new()
    {
        RunId = "test-run",
        StartedUtc = new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero),
        FinishedUtc = new DateTimeOffset(2025, 10, 1, 12, 0, 9, TimeSpan.Zero),
        ToolVersion = "1.0.0-test",
    };

    private static (Dictionary<string, LensSpec> Specs, Dictionary<string, BodyInfo> Bodies) Gear()
    {
        static LensSpec Spec(string id, string name, double min, double max, double ap) =>
            new() { Id = id, Name = name, FocalMinMm = min, FocalMaxMm = max, MaxApertureWide = ap, MaxApertureTele = ap, Source = LensSpecSource.Exif };

        var specs = new Dictionary<string, LensSpec>(StringComparer.Ordinal)
        {
            ["nikkor z 24-70mm f/4 s"] = Spec("nikkor z 24-70mm f/4 s", "NIKKOR Z 24-70mm f/4 S", 24, 70, 4),
            ["nikkor z 50mm f/1.8 s"] = Spec("nikkor z 50mm f/1.8 s", "NIKKOR Z 50mm f/1.8 S", 50, 50, 1.8),
            ["laowa 9mm"] = Spec("laowa 9mm", HostileLensName, 9, 9, 5.6),
            ["xf35mmf1.4 r"] = Spec("xf35mmf1.4 r", "XF35mmF1.4 R", 35, 35, 1.4),
        };
        var bodies = new Dictionary<string, BodyInfo>(StringComparer.Ordinal)
        {
            [NikonKey] = new() { Key = NikonKey, Display = "Nikon Z 6II", Make = "Nikon", CropFactor = 1, Format = "FF", Device = DeviceClass.InterchangeableLens },
            [FujiKey] = new() { Key = FujiKey, Display = "Fujifilm X-T3", Make = "Fujifilm", CropFactor = 1.5, Format = "APS-C", Device = DeviceClass.InterchangeableLens },
        };
        return (specs, bodies);
    }

    private static CompositionFeatures Composition(Random rng, string genre) => new()
    {
        Genres = new Dictionary<string, double> { [genre] = 0.8, ["street"] = 0.1 },
        SubjectX = rng.NextDouble(),
        SubjectY = rng.NextDouble(),
        SubjectArea = rng.NextDouble() * 0.5,
        ThirdsScore = rng.NextDouble(),
        CenterScore = rng.NextDouble(),
        SymmetryScore = rng.NextDouble(),
        NegativeSpace = rng.NextDouble(),
        SeparationScore = rng.NextDouble(),
        HorizonTiltDeg = (rng.NextDouble() - 0.5) * 4,
        HorizonY = rng.NextDouble(),
        HorizontalEdges = rng.NextDouble(),
        VerticalEdges = rng.NextDouble(),
        DiagonalEdges = rng.NextDouble(),
        Extras = new Dictionary<string, double> { ["darkShare"] = rng.NextDouble(), ["clip.top1Prob"] = rng.NextDouble() },
    };

    private static ColorFeatures Color(Random rng)
    {
        var hue = new double[12];
        hue[rng.Next(12)] = 1;
        var luma = new double[16];
        for (var b = 0; b < luma.Length; b++)
        {
            luma[b] = rng.NextDouble();
        }

        return new ColorFeatures
        {
            MeanLuma = rng.NextDouble(),
            LumaP05 = 0.05,
            LumaP50 = 0.5,
            LumaP95 = 0.95,
            ShadowClip = rng.NextDouble() * 0.05,
            HighlightClip = rng.NextDouble() * 0.05,
            Contrast = rng.NextDouble(),
            Key = rng.NextDouble() < 0.5 ? "mid" : "low",
            Colorfulness = rng.NextDouble() * 80,
            MeanChroma = rng.NextDouble() * 0.2,
            Saturation = rng.NextDouble(),
            Monochrome = rng.NextDouble() < 0.05,
            Warmth = (rng.NextDouble() * 2) - 1,
            Tint = (rng.NextDouble() * 2) - 1,
            Sharpness = rng.NextDouble(),
            HueHistogram = hue,
            LumaHistogram = luma,
            Palette = [new PaletteColor("#4F6D8A", 0.5), new PaletteColor("#C27C5A", 0.3), new PaletteColor("#1E2226", 0.2)],
        };
    }

    private static GenreTaxonomy LoadTaxonomy()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "data", "taxonomy", "genres.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<GenreTaxonomy>(File.ReadAllText(path), ContractJson.Options)
                    ?? throw new InvalidOperationException("genres.json deserialized to null");
            }
        }

        throw new FileNotFoundException("data/taxonomy/genres.json not found above " + AppContext.BaseDirectory);
    }
}
