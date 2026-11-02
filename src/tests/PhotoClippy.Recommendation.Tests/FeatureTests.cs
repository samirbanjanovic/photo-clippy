using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Recommendation;
using Xunit.Abstractions;
using static PhotoClippy.Recommendation.Tests.TestData;

namespace PhotoClippy.Recommendation.Tests;

public sealed class FeatureTests(ITestOutputHelper output)
{
    private const string Priced = "35/1.2 1060g $2800, 24-70/2.8 805g $2300, 24/1.4 650g $2400, 14-24/2.8 650g $2400, 50/1.2 1090g $2100";

    private static readonly OwnedLens Z2470F4 = Owned("24-70/4", "NIKKOR Z 24-70mm f/4 S");

    private static ShootingProfile LowLightProfile() => Profile(
        new FocalPeak(35, 0.8, 30, 40),
        new FocalPeak(70, 0.06, 60, 70),
        new FocalPeak(24, 0.07, 24, 26));

    [Fact]
    public async Task NoCandidates_ProposesDeterministicArchetypes()
    {
        RecommendationRequest Make() => Request(LowLightAt35(), null, [Z2470F4], profile: LowLightProfile());

        var set = await Run(Make());
        var again = await Run(Make());
        foreach (var r in set.Ranked)
        {
            output.WriteLine($"#{r.Rank} {r.Lens.Id} '{r.Lens.Name}' score={r.Score:0.000} — {r.Verdict}");
        }

        Assert.InRange(set.Ranked.Count, 2, 6);
        Assert.All(set.Ranked, r =>
        {
            Assert.Equal(LensSpecSource.Suggested, r.Lens.Source);
            Assert.Contains("archetype", r.Lens.Name, StringComparison.Ordinal);
        });
        Assert.Contains(set.Notes, n => n.Contains("spec archetypes", StringComparison.Ordinal));
        Assert.Contains(set.Ranked, r => r.Lens.Id == "35/1.4");
        Assert.Contains(set.Ranked, r => r.Lens.IsZoom);
        Assert.DoesNotContain(set.Ranked, r => r.Lens.Id == "24-70/4.0");
        Assert.Equal("35/1.4", set.Ranked[0].Lens.Id);
        Assert.Equal(Json(set), Json(again));
    }

    [Fact]
    public async Task TopN_TruncatesRankingButKeepsCandidateCount()
    {
        var all = await Run(Request(LowLightAt35(), Priced, [Z2470F4], preferences: Prefs(replicates: 50)));
        var top = await Run(Request(LowLightAt35(), Priced, [Z2470F4], preferences: Prefs(replicates: 50) with { TopN = 2 }));

        Assert.Equal(5, all.Ranked.Count);
        Assert.Equal(2, top.Ranked.Count);
        Assert.Equal(5, top.CandidateCount);
        Assert.Equal(all.Ranked.Take(2).Select(r => r.Lens.Id), top.Ranked.Select(r => r.Lens.Id));
        Assert.Contains(top.Notes, n => n.Contains("top 2 of 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoCandidates_RespectsMaxSuggestions()
    {
        var set = await Run(
            Request(SpreadZoom(), null, [Z2470F4], profile: Profile(new FocalPeak(35, 0.4, 28, 50))),
            new McdaSettings { MaxSuggestions = 3 });

        Assert.InRange(set.Ranked.Count, 1, 3);
        Assert.All(set.Ranked, r => Assert.Equal(LensSpecSource.Suggested, r.Lens.Source));
    }

    [Fact]
    public async Task NoCandidates_ApsC_ArchetypesAreNative()
    {
        var samples = Times(100, Frame(52, 2.8, 2.8, iso: 3200, exposureS: 1.0 / 30, crop: 1.5)).ToList();
        var set = await Run(Request(samples, null, target: ApsC, profile: Profile(new FocalPeak(52, 1, 48, 56))));

        var prime = Assert.Single(set.Ranked, r => !r.Lens.IsZoom);
        Assert.Equal(33, prime.Lens.FocalMinMm);
        Assert.Contains("≈50mm-eq", prime.Lens.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFramesAndNoPeaks_ReturnsEmptyWithNote()
    {
        var set = await Run(Request([], null));

        Assert.Empty(set.Ranked);
        Assert.NotEmpty(set.Notes);
    }

    [Fact]
    public async Task WeightAndPriceMissing_PortabilityAndValueInactive()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs()));

        Assert.False(set.Weights.ContainsKey(CriterionIds.Portability));
        Assert.False(set.Weights.ContainsKey(CriterionIds.Value));
        Assert.DoesNotContain(set.Criteria, c => c.Id is CriterionIds.Portability or CriterionIds.Value);
        Assert.Contains(set.Notes, n => n.StartsWith("Portability not scored — weight not supplied for all candidates", StringComparison.Ordinal));
        Assert.Contains(set.Notes, n => n.StartsWith("Value not scored — price not supplied for all candidates", StringComparison.Ordinal));
        Assert.Equal(1.0, set.Weights.Values.Sum(), 9);
        Assert.All(set.Ranked, r => Assert.DoesNotContain(CriterionIds.Portability, r.Criteria.Keys));
    }

    [Fact]
    public async Task WeightAndPriceSupplied_PortabilityAndValueActive()
    {
        var set = await Run(Request(SpreadZoom(), Priced, [Z2470F4], preferences: Prefs()));

        Assert.True(set.Weights[CriterionIds.Portability] > 0);
        Assert.True(set.Weights[CriterionIds.Value] > 0);
        Assert.Contains(set.Criteria, c => c.Id == CriterionIds.Portability);
        Assert.Contains(set.Criteria, c => c.Id == CriterionIds.Value);
        Assert.DoesNotContain(set.Notes, n => n.StartsWith("Portability not scored", StringComparison.Ordinal) || n.StartsWith("Value not scored", StringComparison.Ordinal));
        Assert.Equal(1.0, Find(set, "24/1.4").Criteria[CriterionIds.Portability], 9);
        Assert.Equal(1.0, Find(set, "50/1.2").Criteria[CriterionIds.Value], 9);
        Assert.Equal(1.0, set.Weights.Values.Sum(), 9);
    }

    [Fact]
    public async Task OnlySomeWeightsSupplied_PortabilityInactiveAndNamesMissing()
    {
        var set = await Run(Request(SpreadZoom(), "35/1.2 1060g, 24-70/2.8 805g, 24/1.4", [Z2470F4], preferences: Prefs()));

        Assert.False(set.Weights.ContainsKey(CriterionIds.Portability));
        var note = Assert.Single(set.Notes, n => n.StartsWith("Portability not scored", StringComparison.Ordinal));
        Assert.EndsWith("Missing for: 24/1.4.", note, StringComparison.Ordinal);
        Assert.Contains("\"35/1.2 1060g\"", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Budget_ExcludesPricedCandidatesOverBudget()
    {
        var prefs = Prefs() with { BudgetUsd = 2350 };
        var set = await Run(Request(SpreadZoom(), Priced, [Z2470F4], preferences: prefs));

        Assert.Equal(["24-70/2.8", "50/1.2"], set.Ranked.Select(r => r.Lens.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, set.CandidateCount);
        var note = Assert.Single(set.Notes, n => n.Contains("Excluded over your $2350 budget", StringComparison.Ordinal));
        Assert.Contains("35mm f/1.2", note, StringComparison.Ordinal);
        Assert.Contains("14-24mm f/2.8", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Budget_UnpricedCandidatesKeptWithNote()
    {
        var prefs = Prefs() with { BudgetUsd = 2000 };
        var set = await Run(Request(SpreadZoom(), "35/1.2 $2800, 24-70/2.8, 50/1.2 $1800", [Z2470F4], preferences: prefs));

        Assert.Equal(["24-70/2.8", "50/1.2"], set.Ranked.Select(r => r.Lens.Id).Order(StringComparer.Ordinal));
        Assert.Contains(set.Notes, n => n.Contains("Excluded over your $2000 budget", StringComparison.Ordinal));
        Assert.Contains(set.Notes, n => n.StartsWith("Budget not applied to", StringComparison.Ordinal) && n.Contains("24-70mm f/2.8", StringComparison.Ordinal));
        Assert.False(set.Weights.ContainsKey(CriterionIds.Value));
    }

    [Fact]
    public async Task MaxWeight_ExcludesHeavyCandidates()
    {
        var prefs = Prefs() with { MaxWeightG = 900 };
        var set = await Run(Request(SpreadZoom(), Priced, [Z2470F4], preferences: prefs));

        Assert.Equal(["14-24/2.8", "24-70/2.8", "24/1.4"], set.Ranked.Select(r => r.Lens.Id).Order(StringComparer.Ordinal));
        var note = Assert.Single(set.Notes, n => n.Contains("Excluded over your 900g weight limit", StringComparison.Ordinal));
        Assert.Contains("50mm f/1.2", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSeed_IdenticalOutputAndProbabilitiesSumToOne()
    {
        var a = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 300, seed: 7)));
        var b = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 300, seed: 7)));
        var c = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 300, seed: 8)));

        Assert.Equal(Json(a), Json(b));
        Assert.Equal(a.Ranked.Select(r => r.Lens.Id), c.Ranked.Select(r => r.Lens.Id));
        Assert.Contains(a.Notes, n => n.Contains("bootstrap resamples", StringComparison.Ordinal) && n.Contains("seed 7", StringComparison.Ordinal));
        AssertBootstrapSane(a);
        AssertBootstrapSane(c);
    }

    [Fact]
    public async Task CloseCall_ProbabilityIsSharedAndSumsToOne()
    {
        List<CaptureSample> samples =
        [
            .. Times(50, Frame(24, 2.8, 2.8, iso: 1600, exposureS: 1.0 / 60)),
            .. Times(50, Frame(35, 2.8, 2.8, iso: 1600, exposureS: 1.0 / 60)),
            .. Times(4, Frame(50, 2.8, 2.8, iso: 1600, exposureS: 1.0 / 60)),
        ];
        var set = await Run(Request(samples, "24/1.4, 35/1.4", preferences: Prefs(replicates: 500, seed: 3)));
        foreach (var r in set.Ranked)
        {
            output.WriteLine($"{r.Lens.Id} score={r.Score:0.0000} [{r.ScoreLow:0.0000},{r.ScoreHigh:0.0000}] pBest={r.ProbabilityBest:0.000}");
        }

        AssertBootstrapSane(set);
        Assert.All(set.Ranked, r => Assert.InRange(r.ProbabilityBest, 0.02, 0.98));
    }

    [Fact]
    public async Task NoBootstrap_PointIntervalAndWinnerTakesAll()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 0)));

        Assert.Equal(1.0, set.Ranked[0].ProbabilityBest, 9);
        Assert.All(set.Ranked, r =>
        {
            Assert.Equal(r.Score, r.ScoreLow, 9);
            Assert.Equal(r.Score, r.ScoreHigh, 9);
        });
    }

    [Fact]
    public async Task Sensitivity_WinsInsteadWhenAndPresetWinnersPopulated()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs()));

        Assert.Equal(McdaPresets.All.Order(StringComparer.Ordinal), set.PresetWinners.Keys.Order(StringComparer.Ordinal));
        Assert.All(set.PresetWinners.Values, id => Assert.Contains(set.Ranked, r => r.Lens.Id == id));
        Assert.Equal("24-70/2.8", set.PresetWinners[McdaPresets.Balanced]);
        Assert.Empty(set.Ranked[0].WinsInsteadWhen);
        Assert.Contains(set.Ranked.Skip(1), r => r.WinsInsteadWhen.Count > 0);

        var fast35 = Find(set, "35/1.2");
        var line = Assert.Single(fast35.WinsInsteadWhen, w => w.StartsWith("Ranks first if low-light weight", StringComparison.Ordinal));
        output.WriteLine(line);
        Assert.Contains($"(now {set.Weights[CriterionIds.LowLight].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitivity_ThresholdActuallyFlipsTheWinner()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 0)));
        var line = Find(set, "35/1.2").WinsInsteadWhen.First(w => w.StartsWith("Ranks first if low-light weight ≥ ", StringComparison.Ordinal));
        var threshold = double.Parse(line["Ranks first if low-light weight ≥ ".Length..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

        var raw = McdaPresets.Get(McdaPresets.Balanced);
        var active = set.Weights.Keys.ToList();
        var others = active.Where(k => k != CriterionIds.LowLight).Sum(k => raw[k]);
        var overrides = active.ToDictionary(k => k, k => k == CriterionIds.LowLight ? threshold + 0.01 : raw[k] / others * (1 - threshold - 0.01));
        var flipped = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 0) with { Weights = overrides }));
        var below = active.ToDictionary(k => k, k => k == CriterionIds.LowLight ? threshold - 0.02 : raw[k] / others * (1 - threshold + 0.02));
        var kept = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs(replicates: 0) with { Weights = below }));

        Assert.Equal("35/1.2", flipped.Ranked[0].Lens.Id);
        Assert.Equal("24-70/2.8", kept.Ranked[0].Lens.Id);
    }

    [Fact]
    public async Task WeightOverrides_MergeOnPresetAndReportUnknownKeys()
    {
        var weights = new Dictionary<string, double> { [CriterionIds.LowLight] = 0.9, ["sharpness"] = 1 };
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs() with { Weights = weights }));
        var baseline = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs()));

        Assert.True(set.Weights[CriterionIds.LowLight] > 0.6);
        Assert.True(set.Weights[CriterionIds.LowLight] > baseline.Weights[CriterionIds.LowLight]);
        Assert.Equal(1.0, set.Weights.Values.Sum(), 9);
        Assert.Contains(set.Notes, n => n.StartsWith("Ignored weight overrides: sharpness", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownPreset_FallsBackToBalancedWithNote()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs("speed")));

        Assert.Equal(McdaPresets.Balanced, set.Preset);
        Assert.Contains(set.Notes, n => n.StartsWith("Unknown preset 'speed'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explanations_InvariantCultureAndShortNames()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";
        comma.NumberFormat.PercentDecimalSeparator = ",";
        Thread.CurrentThread.CurrentCulture = comma;
        try
        {
            var set = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4], preferences: Prefs()));
            var text = string.Join("\n", set.Ranked.SelectMany(r => r.Evidence.Concat(r.TradeOffs).Concat(r.WinsInsteadWhen).Append(r.Verdict ?? string.Empty)))
                + "\n" + set.Headline + "\n" + string.Join("\n", set.Notes.Concat(set.Assumptions));
            output.WriteLine(text);

            Assert.DoesNotMatch(@"f/\d+,\d", text);
            Assert.DoesNotMatch(@"\d+,\d+ stops", text);
            Assert.Contains("35mm f/1.2", text, StringComparison.Ordinal);
            Assert.All(set.Ranked, r => Assert.InRange(r.Evidence.Count, 1, 4));
            Assert.All(set.Ranked, r => Assert.False(string.IsNullOrWhiteSpace(r.Verdict)));
            Assert.Contains(set.Assumptions, a => a.Contains("crop factor", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Factory_CreatesConfiguredRecommender()
    {
        var factory = new McdaRecommenderFactory();
        using var doc = JsonDocument.Parse("""{"primeSigma":0.2,"maxSuggestions":3}""");
        var plugin = factory.Create(new PluginContext
        {
            Settings = doc.RootElement.Clone(),
            DataDirectory = ".",
            ModelsDirectory = ".",
            StateDirectory = ".",
            LoggerFactory = NullLoggerFactory.Instance,
            Taxonomy = Taxonomy,
        });

        var recommender = Assert.IsType<McdaRecommender>(plugin);
        Assert.Equal("builtin.recommend.mcda", factory.Id);
        Assert.Equal(ComponentKind.Recommendation, factory.Kind);
        Assert.Equal(factory.Id, recommender.Manifest.Id);
        Assert.Equal(ComponentKind.Recommendation, recommender.Manifest.Kind);
        Assert.Equal(0.2, recommender.Settings.PrimeSigma, 9);
        Assert.Equal(3, recommender.Settings.MaxSuggestions);
        Assert.Equal(0.8, recommender.Settings.RedundancyPenalty, 9);

        var defaults = Assert.IsType<McdaRecommender>(factory.Create(new PluginContext
        {
            DataDirectory = ".",
            ModelsDirectory = ".",
            StateDirectory = ".",
            LoggerFactory = NullLoggerFactory.Instance,
            Taxonomy = Taxonomy,
        }));
        Assert.Equal(new McdaSettings(), defaults.Settings);
        Assert.NotEqual(recommender.Manifest.Fingerprint, defaults.Manifest.Fingerprint);

        var set = await recommender.RecommendAsync(Request(SpreadZoom(), UserCandidates, [Z2470F4], preferences: Prefs()));
        Assert.Equal("builtin.recommend.mcda", set.RecommenderId);
    }

    private static void AssertBootstrapSane(RecommendationSet set)
    {
        Assert.Equal(1.0, set.Ranked.Sum(r => r.ProbabilityBest), 9);
        Assert.All(set.Ranked, r =>
        {
            Assert.InRange(r.ProbabilityBest, 0, 1);
            Assert.True(r.ScoreLow <= r.ScoreHigh + 1e-12, $"{r.Lens.Id} interval [{r.ScoreLow}, {r.ScoreHigh}]");
        });
    }
}
