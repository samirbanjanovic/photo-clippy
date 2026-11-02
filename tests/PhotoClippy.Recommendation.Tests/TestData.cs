using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Recommendation;

namespace PhotoClippy.Recommendation.Tests;

internal static class TestData
{
    public const string UserCandidates = "24/1.4, 14-24/2.8, 24-70/2.8, 35/1.2, 50/1.2";

    private static readonly Lazy<GenreTaxonomy> LazyTaxonomy = new(LoadTaxonomy);

    public static GenreTaxonomy Taxonomy => LazyTaxonomy.Value;

    public static TargetSystem FullFrame { get; } = new() { Format = "FF", CropFactor = 1, BodyKey = "nikon|z 6ii" };

    public static TargetSystem ApsC { get; } = new() { Format = "APS-C", CropFactor = 1.5, BodyKey = "fujifilm|x-t5" };

    public static LensSpec Lens(string text)
    {
        Assert.True(LensSpec.TryParse(text, out var spec, out var error), error);
        return spec;
    }

    public static IReadOnlyList<LensSpec> Candidates(string text)
    {
        var list = LensSpec.ParseList(text, out var errors);
        Assert.Empty(errors);
        return list;
    }

    public static OwnedLens Owned(string text, string display, int captures = 1000) => new()
    {
        Lens = Lens(text) with { Name = display, Source = LensSpecSource.Exif },
        Captures = captures,
        LensKey = display.ToLowerInvariant(),
    };

    public static CaptureSample Frame(
        double f35,
        double fnum,
        double? ceiling,
        int iso = 200,
        double exposureS = 1.0 / 250,
        bool wideEnd = false,
        bool teleEnd = false,
        string? genre = null,
        double? ev100 = null,
        double crop = 1,
        bool zoom = true,
        double? separation = null) => new()
    {
        Focal35 = f35,
        FocalMm = f35 / crop,
        FNumber = fnum,
        LensMaxApertureAtFocal = ceiling,
        Iso = iso,
        ExposureS = exposureS,
        Ev100 = ev100,
        LensIsZoom = zoom,
        AtWideEnd = wideEnd,
        AtTeleEnd = teleEnd,
        Genre = genre,
        GenreConfidence = genre is null ? 0 : 0.9,
        Separation = separation,
    };

    public static IEnumerable<CaptureSample> Times(int count, CaptureSample sample) => Enumerable.Repeat(sample, count);

    public static ShootingProfile Profile(params FocalPeak[] peaks) => new()
    {
        Library = new LibrarySummary(),
        FocalPeaks = peaks,
    };

    public static RecommendationRequest Request(
        IEnumerable<CaptureSample> samples,
        string? candidates,
        IEnumerable<OwnedLens>? owned = null,
        TargetSystem? target = null,
        RecommendationPreferences? preferences = null,
        ShootingProfile? profile = null) => new()
    {
        Profile = profile ?? Profile(),
        Target = target ?? FullFrame,
        Samples = samples.ToList(),
        Candidates = candidates is null ? [] : Candidates(candidates),
        Owned = owned?.ToList() ?? [],
        Taxonomy = Taxonomy,
        Preferences = preferences ?? new RecommendationPreferences(),
    };

    public static RecommendationPreferences Prefs(string preset = "balanced", int replicates = 200, int seed = 1729) => new()
    {
        Preset = preset,
        BootstrapReplicates = replicates,
        Seed = seed,
    };

    public static Task<RecommendationSet> Run(RecommendationRequest request, McdaSettings? settings = null) =>
        new McdaRecommender(settings, Taxonomy).RecommendAsync(request);

    public static LensRecommendation Find(RecommendationSet set, string id) =>
        set.Ranked.Single(r => r.Lens.Id == id);

    public static string Json(RecommendationSet set) => JsonSerializer.Serialize(set, ContractJson.Options);

    /// <summary>Scenario (a): most frames at 35 mm f/4 on a 24-70/4 wide open, ISO 6400 at 1/30 s.</summary>
    public static List<CaptureSample> LowLightAt35() =>
    [
        .. Times(80, Frame(35, 4, 4, iso: 6400, exposureS: 1.0 / 30, ev100: 4, genre: "night")),
        .. Times(7, Frame(24, 8, 4, iso: 400, wideEnd: true, genre: "landscape")),
        .. Times(7, Frame(50, 8, 4, iso: 400, genre: "street")),
        .. Times(6, Frame(70, 8, 4, iso: 400, teleEnd: true, genre: "people")),
    ];

    /// <summary>Scenario (b): frames spread over 24–70 mm at f/4 wide open in good light.</summary>
    public static List<CaptureSample> SpreadZoom() =>
    [
        .. Times(20, Frame(24, 4, 4, iso: 200, ev100: 12, wideEnd: true)),
        .. Times(20, Frame(28, 4, 4, iso: 200, ev100: 12)),
        .. Times(20, Frame(35, 4, 4, iso: 200, ev100: 12)),
        .. Times(20, Frame(50, 4, 4, iso: 200, ev100: 12)),
        .. Times(20, Frame(70, 4, 4, iso: 200, ev100: 12, teleEnd: true)),
    ];

    /// <summary>Scenario (c): frames piled at the wide end-stop of a 16-35/4, some at 14 mm from a borrowed lens.</summary>
    public static List<CaptureSample> WideEndStop() =>
    [
        .. Times(70, Frame(16, 8, 4, iso: 100, wideEnd: true, ev100: 13)),
        .. Times(10, Frame(14, 8, 2.8, iso: 100, ev100: 13)),
        .. Times(10, Frame(20, 8, 4, iso: 100, ev100: 13)),
        .. Times(10, Frame(35, 8, 4, iso: 100, teleEnd: true, ev100: 13)),
    ];

    private static GenreTaxonomy LoadTaxonomy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "data", "taxonomy", "genres.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<GenreTaxonomy>(File.ReadAllText(path), ContractJson.Options)
                    ?? throw new InvalidOperationException("genres.json deserialized to null");
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("data/taxonomy/genres.json not found above " + AppContext.BaseDirectory);
    }
}
