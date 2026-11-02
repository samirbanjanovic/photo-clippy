using PhotoClippy.Contracts;
using Xunit.Abstractions;
using static PhotoClippy.Recommendation.Tests.TestData;

namespace PhotoClippy.Recommendation.Tests;

public sealed class ScenarioTests(ITestOutputHelper output)
{
    private static readonly OwnedLens Z2470F4 = Owned("24-70/4", "NIKKOR Z 24-70mm f/4 S");
    private static readonly OwnedLens Z1635F4 = Owned("16-35/4", "NIKKOR Z 14-30mm f/4 S");

    [Theory]
    [InlineData("balanced")]
    [InlineData("low-light")]
    public async Task LightLimitedAt35_FastThirtyFiveWins(string preset)
    {
        var set = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4], preferences: Prefs(preset)));
        Dump(set);

        var winner = set.Ranked[0];
        Assert.Equal("35/1.2", winner.Lens.Id);
        Assert.Equal(preset, set.Preset);
        Assert.True(winner.Metrics["stopsGained"] > 3, $"stops gained {winner.Metrics["stopsGained"]}");
        Assert.True(winner.Metrics["ceilingCoveredShare"] > 0.7);
        Assert.True(winner.Metrics["medianIsoAfter"] < winner.Metrics["medianIsoBefore"] / 8);
        Assert.Contains(winner.Evidence, e => e.Contains("ISO 6400", StringComparison.Ordinal));
        Assert.Contains("35mm f/1.2", set.Headline, StringComparison.Ordinal);
        Assert.InRange(winner.Evidence.Count, 2, 4);
        Assert.False(string.IsNullOrWhiteSpace(winner.Verdict));
    }

    [Fact]
    public async Task SpreadAcrossZoomRange_FasterZoomWinsBalanced()
    {
        var set = await Run(Request(SpreadZoom(), UserCandidates, [Z2470F4]));
        Dump(set);

        var winner = set.Ranked[0];
        Assert.Equal("24-70/2.8", winner.Lens.Id);
        Assert.True(winner.Metrics["coverageShare"] > 0.99);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", winner.RedundantWith);
        Assert.Contains(winner.TradeOffs, t => t.Contains("NIKKOR Z 24-70mm f/4 S", StringComparison.Ordinal));
        Assert.All(set.Ranked.Skip(1), r => Assert.True(r.Metrics["coverageShare"] < 0.5));
    }

    [Fact]
    public async Task PiledAtWideEndStop_UltraWideZoomWinsReach()
    {
        var set = await Run(Request(WideEndStop(), UserCandidates, [Z1635F4], preferences: Prefs("reach")));
        Dump(set);

        var winner = set.Ranked[0];
        Assert.Equal("14-24/2.8", winner.Lens.Id);
        Assert.True(winner.Metrics["reachShare"] > 0.7, $"reach share {winner.Metrics["reachShare"]}");
        Assert.Contains(winner.Evidence, e => e.Contains("end", StringComparison.OrdinalIgnoreCase) || e.Contains("wider", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("14-24/2.8", set.PresetWinners["reach"]);
    }

    [Fact]
    public async Task ApsCBody_ConvertsCandidatesToEquivalentFocals()
    {
        const double crop = 1.5;
        List<CaptureSample> samples =
        [
            .. Times(60, Frame(34.5, 2.8, 2.8, iso: 6400, exposureS: 1.0 / 30, crop: crop)),
            .. Times(20, Frame(27, 5.6, 2.8, iso: 400, crop: crop)),
            .. Times(20, Frame(75, 5.6, 2.8, iso: 400, crop: crop)),
        ];
        var owned = Owned("16-55/2.8", "XF16-55mmF2.8 R LM WR");
        var set = await Run(Request(samples, "23/1.4, 16-55/2.8, 56/1.2, 10-24/4", [owned], target: ApsC));
        Dump(set);

        var p23 = Find(set, "23/1.4");
        Assert.Equal(34.5, p23.Metrics["focalMin35"], 3);
        Assert.Equal(34.5, p23.Metrics["focalMax35"], 3);
        var p56 = Find(set, "56/1.2");
        Assert.Equal(84, p56.Metrics["focalMin35"], 3);
        var z1024 = Find(set, "10-24/4.0");
        Assert.Equal(15, z1024.Metrics["focalMin35"], 3);
        Assert.Equal(36, z1024.Metrics["focalMax35"], 3);

        Assert.Equal("23/1.4", set.Ranked[0].Lens.Id);
        Assert.True(p23.Metrics["stopsGained"] > 1.8 && p23.Metrics["stopsGained"] < 2.3, $"stops {p23.Metrics["stopsGained"]}");
        Assert.True(Find(set, "16-55/2.8").AlreadyOwned);
        Assert.Contains(set.Assumptions, a => a.Contains("1.5", StringComparison.Ordinal));
        Assert.Contains(p23.Evidence, e => e.Contains("1.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnedCandidate_IsFlaggedAndRankedLast()
    {
        var owned = Owned("24-70/2.8", "NIKKOR Z 24-70mm f/2.8 S");
        var set = await Run(Request(SpreadZoom(), "24-70/2.8, 35/1.8, 70-200/2.8", [owned]));
        Dump(set);

        var last = set.Ranked[^1];
        Assert.Equal("24-70/2.8", last.Lens.Id);
        Assert.True(last.AlreadyOwned);
        Assert.Equal(set.Ranked.Count, last.Rank);
        Assert.Contains(last.TradeOffs, t => t.Contains("own", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(set.Notes, n => n.Contains("24-70mm f/2.8", StringComparison.Ordinal) && n.Contains("own", StringComparison.OrdinalIgnoreCase));
        Assert.All(set.Ranked.Take(set.Ranked.Count - 1), r => Assert.False(r.AlreadyOwned));
        Assert.Equal(0, last.ProbabilityBest, 9);
    }

    [Fact]
    public async Task OwnedWithinTolerance_IsDetected()
    {
        var owned = Owned("24-70/2.8", "Tamron 24-70mm f/2.8");
        var set = await Run(Request(SpreadZoom(), "24-72/2.8, 24-70/4, 28-75/2.8", [owned]));
        Dump(set);

        Assert.True(Find(set, "24-72/2.8").AlreadyOwned);
        Assert.False(Find(set, "24-70/4.0").AlreadyOwned);
        Assert.False(Find(set, "28-75/2.8").AlreadyOwned);
    }

    [Fact]
    public async Task Ranks_AreDenseAndCountsMatch()
    {
        var set = await Run(Request(LowLightAt35(), UserCandidates, [Z2470F4]));

        Assert.Equal(5, set.CandidateCount);
        Assert.Equal(100, set.SampleCount);
        Assert.Equal(Enumerable.Range(1, 5), set.Ranked.Select(r => r.Rank));
        Assert.Equal("builtin.recommend.mcda", set.RecommenderId);
        Assert.Equal(1.0, set.Weights.Values.Sum(), 9);
        Assert.All(set.Ranked, r =>
        {
            Assert.InRange(r.Score, 0, 1);
            Assert.True(r.ScoreLow <= r.ScoreHigh);
        });
        Assert.Contains(set.Assumptions, a => a.Contains("allowed", StringComparison.Ordinal));
    }

    private void Dump(RecommendationSet set)
    {
        output.WriteLine(set.Headline ?? "(no headline)");
        foreach (var r in set.Ranked)
        {
            output.WriteLine($"#{r.Rank} {r.Lens.Id} score={r.Score:0.000} [{r.ScoreLow:0.000},{r.ScoreHigh:0.000}] pBest={r.ProbabilityBest:0.00} R={r.Redundancy:0.00} owned={r.AlreadyOwned} — {r.Verdict}");
            output.WriteLine("   crit: " + string.Join(", ", r.Criteria.Select(kv => $"{kv.Key}={kv.Value:0.00}")));
            foreach (var e in r.Evidence)
            {
                output.WriteLine("   + " + e);
            }

            foreach (var t in r.TradeOffs)
            {
                output.WriteLine("   - " + t);
            }

            foreach (var w in r.WinsInsteadWhen)
            {
                output.WriteLine("   ? " + w);
            }
        }

        output.WriteLine("weights: " + string.Join(", ", set.Weights.Select(kv => $"{kv.Key}={kv.Value:0.000}")));
        output.WriteLine("preset winners: " + string.Join(", ", set.PresetWinners.Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var n in set.Notes)
        {
            output.WriteLine("note: " + n);
        }

        foreach (var a in set.Assumptions)
        {
            output.WriteLine("assume: " + a);
        }
    }
}
