using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Clip;
using Xunit.Abstractions;

namespace PhotoClippy.Clip.Tests;

/// <summary>Shared, lazily loaded analyzer with the default settings and a fresh state directory.</summary>
public sealed class ClipModelFixture : IDisposable
{
    public static readonly string[] ImageNames = ["landscape.jpg", "landscape2.jpg", "portrait.jpg", "cat.jpg", "food.jpg", "night.jpg", "night2.jpg"];

    private readonly Lazy<ClipCompositionAnalyzer> _analyzer;
    private readonly Lazy<IReadOnlyDictionary<string, Frame>> _frames;

    public ClipModelFixture()
    {
        StateDirectory = ClipTestAssets.NewStateDirectory("models");
        _analyzer = new Lazy<ClipCompositionAnalyzer>(() => new ClipCompositionAnalyzer(ClipTestAssets.CreateContext(ClipTestAssets.ModelsDirectory!, StateDirectory)));
        _frames = new Lazy<IReadOnlyDictionary<string, Frame>>(LoadFrames);
    }

    public string StateDirectory { get; }

    public ClipCompositionAnalyzer Analyzer => _analyzer.Value;

    /// <summary>Test images decoded at the working edge, plus a rendered "screenshot.png".</summary>
    public IReadOnlyDictionary<string, Frame> Frames => _frames.Value;

    public void Dispose()
    {
        if (_analyzer.IsValueCreated)
        {
            _analyzer.Value.Dispose();
        }
    }

    private static Dictionary<string, Frame> LoadFrames()
    {
        var frames = new Dictionary<string, Frame>(StringComparer.Ordinal) { ["screenshot.png"] = ClipTestAssets.RenderScreenshot() };
        foreach (var name in ImageNames)
        {
            if (ClipTestAssets.ImagePath(name) is { } path)
            {
                frames[name] = ClipTestAssets.LoadFrame(path);
            }
        }

        return frames;
    }
}

[CollectionDefinition(Name)]
public sealed class ClipModelCollection : ICollectionFixture<ClipModelFixture>
{
    public const string Name = "clip-models";
}

/// <summary>Tests that run the real CLIP models; they skip when the models are not available.</summary>
[Collection(ClipModelCollection.Name)]
public sealed class ClipModelTests(ClipModelFixture fixture, ITestOutputHelper output)
{
    public static TheoryData<string, string[]> Expectations => new()
    {
        { "landscape.jpg", ["landscape"] },
        { "landscape2.jpg", ["landscape"] },
        { "portrait.jpg", ["portrait", "people"] },
        { "cat.jpg", ["pets"] },
        { "food.jpg", ["food"] },
        { "night.jpg", ["cityscape", "night"] },
        { "night2.jpg", ["cityscape", "night"] },
        { "screenshot.png", ["screenshots", "documents"] },
    };

    [ModelFact]
    public async Task GenreTextEmbeddings_AreComputedOnce_ThenLoadedFromCache()
    {
        var first = fixture.Analyzer;
        var health = await first.CheckHealthAsync();
        Assert.True(health.Healthy, health.Message);
        var runtime = first.LoadedRuntime!;
        Assert.False(runtime.Genres.FromCache);
        Assert.True(File.Exists(first.TextCachePath));
        Assert.Equal(ClipTestAssets.Taxonomy.Genres.Select(g => g.Id), runtime.Genres.Ids);
        Assert.Equal(512, runtime.Genres.Dimension);
        Assert.Equal(16 + (runtime.Genres.Matrix.Length * 4), new FileInfo(first.TextCachePath).Length);
        for (var g = 0; g < runtime.Genres.Ids.Length; g++)
        {
            Assert.Equal(1f, ClipGenreEmbeddingStore.Norm(runtime.Genres.Matrix.AsSpan(g * 512, 512)), 1e-4f);
        }

        var writtenAt = File.GetLastWriteTimeUtc(first.TextCachePath);
        using var second = new ClipCompositionAnalyzer(ClipTestAssets.CreateContext(ClipTestAssets.ModelsDirectory!, fixture.StateDirectory));
        Assert.Equal(first.TextCachePath, second.TextCachePath);
        Assert.True((await second.CheckHealthAsync()).Healthy);
        Assert.True(second.LoadedRuntime!.Genres.FromCache);
        Assert.Equal(runtime.Genres.Matrix, second.LoadedRuntime.Genres.Matrix);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(first.TextCachePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.TextCachePath)!));
    }

    [ModelImagesTheory]
    [MemberData(nameof(Expectations))]
    public async Task Classification_ExpectedGenreIsInTopTwo(string image, string[] accepted)
    {
        Assert.True(fixture.Frames.ContainsKey(image), $"Test image {image} is missing.");

        var features = await fixture.Analyzer.AnalyzeAsync(fixture.Frames[image], ClipTestAssets.AnalysisContext(image));

        Assert.NotNull(features.Genres);
        Assert.Equal(ClipTestAssets.Taxonomy.Genres.Count, features.Genres.Count);
        Assert.Equal(1.0, features.Genres.Values.Sum(), 1e-6);
        var ranked = features.Genres.OrderByDescending(kv => kv.Value).ToList();
        output.WriteLine(string.Join(", ", ranked.Take(4).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value:F3}"))));
        output.WriteLine(string.Join(", ", features.Extras.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value:F3}"))));
        Assert.True(accepted.Contains(ranked[0].Key) || accepted.Contains(ranked[1].Key), $"Expected one of [{string.Join(", ", accepted)}] in the top 2, got {ranked[0].Key}, {ranked[1].Key}.");
        Assert.Equal(ranked[0].Value, features.Extras["clip.top1Prob"], 1e-9);
        Assert.Equal(ranked[0].Value - ranked[1].Value, features.Extras["clip.margin"], 1e-9);
        Assert.InRange(features.Extras["clip.entropy"], 0, 1);
        Assert.Null(features.SubjectX);
    }

    [ModelImagesTheory]
    [InlineData(8)]
    public async Task ConcurrentCalls_MatchSequentialResults(int parallelism)
    {
        var analyzer = fixture.Analyzer;
        var frames = fixture.Frames.ToArray();
        var expected = new Dictionary<string, IReadOnlyDictionary<string, double>>();
        foreach (var (name, frame) in frames)
        {
            expected[name] = (await analyzer.AnalyzeAsync(frame, ClipTestAssets.AnalysisContext(name))).Genres!;
        }

        var results = new ConcurrentBag<(string Name, IReadOnlyDictionary<string, double> Genres)>();
        await Parallel.ForAsync(0, parallelism * 2, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (i, ct) =>
        {
            var (name, frame) = frames[i % frames.Length];
            var features = await analyzer.AnalyzeAsync(frame, ClipTestAssets.AnalysisContext(name), ct);
            results.Add((name, features.Genres!));
        });

        Assert.Equal(parallelism * 2, results.Count);
        foreach (var (name, genres) in results)
        {
            foreach (var (genre, p) in expected[name])
            {
                Assert.Equal(p, genres[genre], 1e-4);
            }
        }
    }

    [ModelImagesTheory]
    [InlineData(4)]
    public async Task Throughput_IsLogged(int callers)
    {
        var analyzer = fixture.Analyzer;
        await analyzer.CheckHealthAsync();
        var result = await ClipBenchmark.MeasureAsync(analyzer, fixture.Frames.Values.ToArray(), callers, images: 48);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{analyzer.Settings.Model} quantized={analyzer.Settings.Quantized} intraOp={analyzer.Settings.IntraOpThreads} callers={callers}: {result.ImagesPerSecond:F1} img/s ({result.Images} images in {result.Elapsed.TotalMilliseconds:F0} ms)"));
        Assert.True(result.ImagesPerSecond > 0);
    }

    [ModelFact]
    public async Task Analyze_HonoursCancellation()
    {
        await fixture.Analyzer.CheckHealthAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Analyzer.AnalyzeAsync(ClipTestAssets.Solid(300, 200, 1, 2, 3), ClipTestAssets.AnalysisContext(), cts.Token));
    }

    [ModelFact]
    public async Task Analyze_AcceptsTinyAndExtremeAspectFrames()
    {
        foreach (var frame in new[] { ClipTestAssets.Solid(1, 1, 255, 0, 0), ClipTestAssets.Solid(448, 20, 0, 0, 255), ClipTestAssets.Solid(3, 448, 0, 255, 0) })
        {
            var features = await fixture.Analyzer.AnalyzeAsync(frame, ClipTestAssets.AnalysisContext());
            Assert.Equal(1.0, features.Genres!.Values.Sum(), 1e-6);
        }
    }
}

/// <summary>Opt-in comparison of vision model variants and thread settings (PHOTO_CLIPPY_CLIP_BENCH=1).</summary>
[Collection(ClipModelCollection.Name)]
public sealed class ClipBenchmarkTests(ClipModelFixture fixture, ITestOutputHelper output)
{
    [BenchmarkFact]
    public async Task QuantizedVersusFp32Vision()
    {
        var frames = fixture.Frames.Values.ToArray();
        var state = ClipTestAssets.NewStateDirectory("bench");
        foreach (var quantized in new[] { true, false })
        {
            foreach (var intraOp in new[] { 1, 2, 4 })
            {
                var settings = new ClipSettings { Quantized = quantized, IntraOpThreads = intraOp };
                using var analyzer = new ClipCompositionAnalyzer(ClipTestAssets.CreateContext(ClipTestAssets.ModelsDirectory!, state), settings);
                var load = Stopwatch.StartNew();
                Assert.True((await analyzer.CheckHealthAsync()).Healthy);
                load.Stop();
                foreach (var callers in new[] { 1, 4 })
                {
                    var result = await ClipBenchmark.MeasureAsync(analyzer, frames, callers, images: 64);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"quantized={quantized,-5} intraOp={intraOp} callers={callers}: {result.ImagesPerSecond,6:F1} img/s, {result.Elapsed.TotalMilliseconds / result.Images * callers,6:F1} ms/img/caller (load {load.ElapsedMilliseconds} ms)"));
                }

                if (intraOp == 2)
                {
                    foreach (var name in fixture.Frames.Keys.Order(StringComparer.Ordinal))
                    {
                        var genres = (await analyzer.AnalyzeAsync(fixture.Frames[name], ClipTestAssets.AnalysisContext(name))).Genres!;
                        output.WriteLine("  " + name + ": " + string.Join(", ", genres.OrderByDescending(kv => kv.Value).Take(2).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value:F3}"))));
                    }
                }
            }
        }
    }

    /// <summary>Raw vision-session throughput for batched runs, to decide whether a micro-batcher would pay off.</summary>
    [BenchmarkFact]
    public async Task BatchedVisionRuns()
    {
        var state = ClipTestAssets.NewStateDirectory("bench-batch");
        foreach (var quantized in new[] { true, false })
        {
            foreach (var (intraOp, batch, callers) in new[] { (2, 1, 4), (2, 4, 2), (2, 8, 1), (4, 8, 1), (8, 8, 1), (2, 8, 4) })
            {
                using var analyzer = new ClipCompositionAnalyzer(ClipTestAssets.CreateContext(ClipTestAssets.ModelsDirectory!, state), new ClipSettings { Quantized = quantized, IntraOpThreads = intraOp });
                Assert.True((await analyzer.CheckHealthAsync()).Healthy);
                var runtime = analyzer.LoadedRuntime!;
                var size = runtime.ImageSize;
                var runs = Math.Max(2, 64 / (batch * callers));

                void RunBatches(int count)
                {
                    var pixels = new float[batch * 3 * size * size];
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = (i % 255 / 127f) - 1f;
                    }

                    using var runOptions = new RunOptions();
                    using var input = OrtValue.CreateTensorValueFromMemory(pixels, [batch, 3, size, size]);
                    for (var r = 0; r < count; r++)
                    {
                        using var outputs = runtime.Vision.Run(runOptions, [runtime.VisionInput], [input], [runtime.VisionOutput]);
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(() => RunBatches(1))));
                var stopwatch = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(() => RunBatches(runs))));
                stopwatch.Stop();
                var images = runs * batch * callers;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"quantized={quantized,-5} intraOp={intraOp} batch={batch} callers={callers}: {images / stopwatch.Elapsed.TotalSeconds,6:F1} img/s (vision Run only)"));
            }
        }
    }
}

internal static class ClipBenchmark
{
    public static async Task<(int Images, TimeSpan Elapsed, double ImagesPerSecond)> MeasureAsync(ClipCompositionAnalyzer analyzer, Frame[] frames, int callers, int images)
    {
        for (var i = 0; i < callers; i++)
        {
            await analyzer.AnalyzeAsync(frames[i % frames.Length], ClipTestAssets.AnalysisContext());
        }

        var next = -1;
        var stopwatch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < images)
            {
                await analyzer.AnalyzeAsync(frames[index % frames.Length], ClipTestAssets.AnalysisContext());
            }
        }));
        await Task.WhenAll(workers);
        stopwatch.Stop();
        return (images, stopwatch.Elapsed, images / stopwatch.Elapsed.TotalSeconds);
    }
}
