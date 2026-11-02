using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Composition;
using Xunit.Abstractions;
using static PhotoClippy.Composition.Tests.TestScenes;

namespace PhotoClippy.Composition.Tests;

public sealed class AnalyzerHostingTests(ITestOutputHelper output)
{
    private const int W = 384;
    private const int H = 256;
    private const double Aspect = (double)W / H;

    [Fact]
    public void Factory_CreatesAnalyzer_WithSettingsFromContext()
    {
        var factory = new HeuristicCompositionFactory();
        Assert.Equal(HeuristicCompositionAnalyzer.PluginId, factory.Id);
        Assert.Equal("builtin.composition.heuristic", factory.Id);
        Assert.Equal(ComponentKind.Composition, factory.Kind);

        var defaults = Assert.IsType<HeuristicCompositionAnalyzer>(factory.Create(PluginContext(default)));
        Assert.Equal(256, defaults.Settings.WorkingEdge);
        Assert.True(defaults.Settings.EmitGenrePriors);
        Assert.Equal(384, defaults.Requirements.MaxEdge);
        Assert.Equal("1.0.0", defaults.Manifest.Version);
        Assert.Equal(ComponentKind.Composition, defaults.Manifest.Kind);
        Assert.Equal(factory.Id, defaults.Manifest.Id);
        Assert.False(string.IsNullOrEmpty(defaults.Manifest.Fingerprint));

        using JsonDocument doc = JsonDocument.Parse("""{ "workingEdge": 128, "emitGenrePriors": false }""");
        var custom = Assert.IsType<HeuristicCompositionAnalyzer>(factory.Create(PluginContext(doc.RootElement.Clone())));
        Assert.Equal(128, custom.Settings.WorkingEdge);
        Assert.False(custom.Settings.EmitGenrePriors);
        Assert.NotEqual(defaults.Manifest.Fingerprint, custom.Manifest.Fingerprint);
        Assert.Equal(defaults.Manifest.Fingerprint, new HeuristicCompositionAnalyzer(new HeuristicCompositionSettings(), Taxonomy).Manifest.Fingerprint);

        var reduced = new GenreTaxonomy { Version = "other", Genres = Taxonomy.Genres };
        Assert.NotEqual(defaults.Manifest.Fingerprint, new HeuristicCompositionAnalyzer(null, reduced).Manifest.Fingerprint);
    }

    [Fact]
    public async Task Health_IsOk()
    {
        PluginHealth health = await Analyzer().CheckHealthAsync();
        Assert.True(health.Healthy);
    }

    [Fact]
    public void Analysis_IsDeterministic()
    {
        HeuristicCompositionAnalyzer analyzer = Analyzer();
        foreach (Frame frame in Scenes())
        {
            CompositionFeatures a = Analyze(frame, Facts(focal35: 85, fNumber: 1.8, ev100: 3), analyzer);
            CompositionFeatures b = Analyze(frame, Facts(focal35: 85, fNumber: 1.8, ev100: 3), Analyzer());
            AssertSame(a, b);
        }
    }

    [Fact]
    public async Task ConcurrentAnalysis_MatchesSequential()
    {
        HeuristicCompositionAnalyzer analyzer = Analyzer();
        Frame[] frames = Scenes();
        CaptureFacts facts = Facts(focal35: 24, ev100: 12);
        var expected = new CompositionFeatures[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            expected[i] = Analyze(frames[i], facts, analyzer);
        }

        var tasks = new Task<CompositionFeatures>[32];
        for (int t = 0; t < tasks.Length; t++)
        {
            Frame frame = frames[t % frames.Length];
            tasks[t] = Task.Run(async () =>
            {
                CompositionFeatures last = await analyzer.AnalyzeAsync(frame, Context(facts));
                for (int k = 0; k < 3; k++)
                {
                    last = await analyzer.AnalyzeAsync(frame, Context(facts));
                }

                return last;
            });
        }

        CompositionFeatures[] results = await Task.WhenAll(tasks);
        for (int t = 0; t < results.Length; t++)
        {
            AssertSame(expected[t % frames.Length], results[t]);
        }
    }

    [Fact]
    public void Throughput_IsLogged()
    {
        HeuristicCompositionAnalyzer analyzer = Analyzer();
        Frame small = Render(W, H, NoiseScene(Aspect), 1);
        Frame large = Render(1536, 1024, HorizonScene(0.55, 2, Aspect), 1);
        AnalysisContext context = Context(Facts(focal35: 35, ev100: 10));

        double fpsSmall = Measure(analyzer, small, context, TimeSpan.FromSeconds(1.5));
        double fpsLarge = Measure(analyzer, large, context, TimeSpan.FromSeconds(1.0));
        bool jitOptimized = typeof(HeuristicCompositionAnalyzer).Assembly
            .GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];
        string config = jitOptimized ? "plugin optimized" : "plugin unoptimized";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{config}] 384x256: {fpsSmall:0} frames/s, 1536x1024 (downsampled to 256): {fpsLarge:0} frames/s (single thread)"));

        // Loose: unoptimised Debug builds on slow CI agents must still pass.
        Assert.True(fpsSmall > 15, $"{fpsSmall:0.0} frames/s");
    }

    private static double Measure(HeuristicCompositionAnalyzer analyzer, Frame frame, AnalysisContext context, TimeSpan budget)
    {
        // Warm up past tiered JIT + dynamic PGO (tier0 -> instrumented -> tier1 needs well over 60 calls) so the
        // measurement reflects steady-state code rather than tier-0 code.
        var warmup = Stopwatch.StartNew();
        for (int i = 0; i < 200 && warmup.Elapsed < budget; i++)
        {
            _ = analyzer.AnalyzeAsync(frame, context).AsTask().GetAwaiter().GetResult();
        }

        int n = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < budget)
        {
            _ = analyzer.AnalyzeAsync(frame, context).AsTask().GetAwaiter().GetResult();
            n++;
        }

        return n / sw.Elapsed.TotalSeconds;
    }

    private static Frame[] Scenes() =>
    [
        Render(W, H, DiscScene(0.5, 0.5, 0.15, Aspect)),
        Render(W, H, HorizonScene(0.6, 4, Aspect)),
        Render(W, H, NoiseScene(Aspect)),
        Render(W, H, SeparationScene(Aspect, false)),
        Render(W, H, MirrorScene(Aspect)),
        Render(W, H, EmptyScene(Aspect)),
        Render(640, 480, StarScene(4.0 / 3.0), 1),
        Render(200, 300, ScreenshotScene(), 1),
    ];

    private static void AssertSame(CompositionFeatures a, CompositionFeatures b)
    {
        foreach (((string name, double? x), (_, double? y)) in CompositionFeatureTests.Fields(a).Zip(CompositionFeatureTests.Fields(b)))
        {
            Assert.True(x == y, $"{name}: {x} vs {y}");
        }

        Assert.Equal(a.Extras.Count, b.Extras.Count);
        foreach (KeyValuePair<string, double> kv in a.Extras)
        {
            Assert.Equal(kv.Value, b.Extras[kv.Key]);
        }

        Assert.Equal(a.Genres is null, b.Genres is null);
        if (a.Genres is not null)
        {
            Assert.Equal(a.Genres.Count, b.Genres!.Count);
            foreach (KeyValuePair<string, double> kv in a.Genres)
            {
                Assert.Equal(kv.Value, b.Genres[kv.Key]);
            }
        }
    }

    private static PluginContext PluginContext(JsonElement settings) => new()
    {
        Settings = settings,
        DataDirectory = AppContext.BaseDirectory,
        ModelsDirectory = AppContext.BaseDirectory,
        StateDirectory = AppContext.BaseDirectory,
        LoggerFactory = NullLoggerFactory.Instance,
        Taxonomy = Taxonomy,
    };
}
