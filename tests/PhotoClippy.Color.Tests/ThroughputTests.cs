using System.Diagnostics;
using System.Globalization;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Color;
using Xunit.Abstractions;
using static PhotoClippy.Color.Tests.TestImages;

namespace PhotoClippy.Color.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThroughputCollection
{
    public const string Name = "Color throughput";
}

[Collection(ThroughputCollection.Name)]
public sealed class ThroughputTests(ITestOutputHelper output)
{
    private static readonly AnalysisContext Context = new() { CaptureId = "bench", SourcePath = "bench.jpg" };

    [Fact]
    public async Task WorkingSizeFrames_SingleThreadedThroughput()
    {
        var analyzer = new ColorAnalyzer();
        var frames = new Frame[4];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = ToFrame(Scene(W, H, seed: i), W, H);
        }

        for (var i = 0; i < 40; i++)
        {
            await analyzer.AnalyzeAsync(frames[i % frames.Length], Context);
        }

        var (iterations, sw) = await RunTimeBoxedAsync(
            i => analyzer.AnalyzeAsync(frames[i % frames.Length], Context), maxIterations: 2000, budget: TimeSpan.FromSeconds(1.5));
        var fps = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"builtin.color: {fps:F0} frames/s at {W}x{H} single-threaded ({sw.Elapsed.TotalMilliseconds / iterations:F3} ms/frame, {iterations} frames)"));
        Assert.True(fps > 50, $"throughput {fps:F0} frames/s");
    }

    [Fact]
    public async Task LargeFrames_DownsampleThroughput()
    {
        var analyzer = new ColorAnalyzer();
        var frame = ToFrame(Scene(1600, 1066), 1600, 1066);
        for (var i = 0; i < 5; i++)
        {
            await analyzer.AnalyzeAsync(frame, Context);
        }

        var (iterations, sw) = await RunTimeBoxedAsync(
            _ => analyzer.AnalyzeAsync(frame, Context), maxIterations: 200, budget: TimeSpan.FromSeconds(1));
        var fps = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"builtin.color: {fps:F0} frames/s at 1600x1066 incl. area downsample"));
        Assert.True(fps > 5, $"throughput {fps:F0} frames/s");
    }

    /// <summary>Runs until either the iteration cap or the time budget is reached (keeps Debug runs short).</summary>
    private static async Task<(int Iterations, Stopwatch Elapsed)> RunTimeBoxedAsync(
        Func<int, ValueTask<ColorFeatures>> run, int maxIterations, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        var iterations = 0;
        while (iterations < maxIterations && (iterations < 10 || sw.Elapsed < budget))
        {
            await run(iterations);
            iterations++;
        }

        sw.Stop();
        return (iterations, sw);
    }
}
