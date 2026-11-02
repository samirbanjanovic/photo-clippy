using System.Diagnostics;
using PhotoClippy.Contracts;
using Xunit.Abstractions;
using static PhotoClippy.Recommendation.Tests.TestData;

namespace PhotoClippy.Recommendation.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "performance";
}

[Collection(PerformanceCollection.Name)]
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int SampleCount = 100_000;
    private const int Replicates = 500;
    private const string TenCandidates = "14-24/2.8, 20/1.8, 24/1.4, 24-70/2.8, 35/1.2, 50/1.2, 85/1.8, 135/1.8, 70-200/2.8, 100-400/4.5-5.6";

    private static readonly OwnedLens[] Kit =
    [
        Owned("24-70/4", "NIKKOR Z 24-70mm f/4 S", 60_000),
        Owned("70-200/4", "AF-S 70-200mm f/4G", 30_000),
        Owned("40/2", "NIKKOR Z 40mm f/2", 10_000),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HundredThousandSamples_TenCandidates_FiveHundredReplicates(bool continuousFocals)
    {
        var request = Request(Library(continuousFocals), TenCandidates, Kit, preferences: Prefs(replicates: Replicates, seed: 99) with { TopN = 0 });
        Assert.Equal(10, request.Candidates.Count);

        await Run(request);
        var best = double.MaxValue;
        RecommendationSet? set = null;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            set = await Run(request);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            output.WriteLine($"run {i + 1}: {sw.Elapsed.TotalMilliseconds:0} ms");
        }

        output.WriteLine($"{(continuousFocals ? "continuous" : "discrete")} focals: {SampleCount} samples × 10 candidates × {Replicates} replicates, best {best:0} ms");
        output.WriteLine(set!.Headline);
        Assert.Equal(SampleCount, set.SampleCount);
        Assert.Equal(10, set.Ranked.Count);
        Assert.Equal(1.0, set.Ranked.Sum(r => r.ProbabilityBest), 9);
        Assert.True(best < 3000, $"took {best:0} ms");
    }

    private static List<CaptureSample> Library(bool continuousFocals)
    {
        var rng = new Random(42);
        double[] fNumbers = [2, 2.8, 4, 5.6, 8, 11];
        int[] isos = [100, 200, 400, 800, 1600, 3200, 6400, 12800];
        double[] shutters = [1.0 / 1000, 1.0 / 250, 1.0 / 60, 1.0 / 30, 1.0 / 8];
        string?[] genres = [null, "street", "landscape", "people", "night", "wildlife"];
        var samples = new List<CaptureSample>(SampleCount);
        for (var i = 0; i < SampleCount; i++)
        {
            var u = rng.NextDouble();
            double f35;
            double ceiling;
            bool wide, tele;
            if (u < 0.6)
            {
                f35 = 24 * Math.Pow(70.0 / 24, rng.NextDouble());
                ceiling = 4;
                wide = f35 < 24.5;
                tele = f35 > 69;
            }
            else if (u < 0.9)
            {
                f35 = 70 * Math.Pow(200.0 / 70, rng.NextDouble());
                ceiling = 4;
                wide = f35 < 71;
                tele = f35 > 195;
            }
            else
            {
                f35 = 40;
                ceiling = 2;
                wide = tele = false;
            }

            if (!continuousFocals)
            {
                f35 = Math.Round(f35);
            }

            var fnum = rng.NextDouble() < 0.45 ? ceiling : fNumbers[rng.Next(fNumbers.Length)];
            fnum = Math.Max(fnum, ceiling);
            samples.Add(Frame(
                f35,
                fnum,
                ceiling,
                iso: isos[rng.Next(isos.Length)],
                exposureS: shutters[rng.Next(shutters.Length)],
                wideEnd: wide,
                teleEnd: tele,
                genre: genres[rng.Next(genres.Length)],
                zoom: ceiling > 2) with
            {
                Weight = continuousFocals ? 0.5 + rng.NextDouble() : 1,
            });
        }

        return samples;
    }
}
