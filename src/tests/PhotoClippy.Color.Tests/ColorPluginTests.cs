using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Color;
using static PhotoClippy.Color.Tests.TestImages;

namespace PhotoClippy.Color.Tests;

public sealed class ColorPluginTests
{
    private static readonly AnalysisContext Context = new() { CaptureId = "test", SourcePath = "test.jpg" };

    private static PluginContext CreateContext(string? settingsJson = null) => new()
    {
        Settings = settingsJson is null ? default : JsonDocument.Parse(settingsJson).RootElement.Clone(),
        DataDirectory = ".",
        ModelsDirectory = ".",
        StateDirectory = ".",
        LoggerFactory = NullLoggerFactory.Instance,
        Taxonomy = new GenreTaxonomy(),
    };

    [Fact]
    public void Factory_IdentifiesAsBuiltinColor()
    {
        var factory = new ColorAnalyzerFactory();
        Assert.Equal("builtin.color", factory.Id);
        Assert.Equal(ComponentKind.Color, factory.Kind);
        Assert.NotNull(typeof(ColorAnalyzerFactory).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public async Task Factory_WithoutSettings_CreatesDefaultAnalyzer()
    {
        var plugin = new ColorAnalyzerFactory().Create(CreateContext());

        var analyzer = Assert.IsAssignableFrom<IColorAnalyzer>(plugin);
        var manifest = analyzer.Manifest;
        Assert.Equal("builtin.color", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(ComponentKind.Color, manifest.Kind);
        Assert.Equal(ContractInfo.Version, manifest.ContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Name));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        Assert.NotEmpty(manifest.Capabilities);
        Assert.Matches("^[0-9a-f]{12}$", manifest.Fingerprint!);
        Assert.Equal($"builtin.color@1.0.0#{manifest.Fingerprint}", manifest.CacheKey);
        Assert.Equal(320, analyzer.Requirements.MaxEdge);
        Assert.Equal(new ColorSettings(), ((ColorAnalyzer)analyzer).Settings);
        Assert.True((await analyzer.CheckHealthAsync()).Healthy);
    }

    [Fact]
    public void Factory_ReadsSettings()
    {
        var analyzer = (ColorAnalyzer)new ColorAnalyzerFactory().Create(
            CreateContext("""{ "workingEdge": 256, "paletteSize": 3, "maxPaletteSamples": 1000 }"""));

        Assert.Equal(new ColorSettings { WorkingEdge = 256, PaletteSize = 3, MaxPaletteSamples = 1000 }, analyzer.Settings);
        Assert.Equal(256, analyzer.Requirements.MaxEdge);
    }

    [Fact]
    public void Settings_AreClampedToSaneRanges()
    {
        var analyzer = (ColorAnalyzer)new ColorAnalyzerFactory().Create(
            CreateContext("""{ "workingEdge": 5, "paletteSize": 100, "maxPaletteSamples": 1 }"""));

        Assert.Equal(new ColorSettings { WorkingEdge = 32, PaletteSize = 16, MaxPaletteSamples = 64 }, analyzer.Settings);
        Assert.Equal(analyzer.Manifest.Fingerprint, new ColorAnalyzer(analyzer.Settings).Manifest.Fingerprint);
    }

    [Fact]
    public void Fingerprint_ChangesWithOutputAffectingSettings()
    {
        var baseline = new ColorAnalyzer().Manifest.Fingerprint;

        Assert.Equal(baseline, new ColorAnalyzer(new ColorSettings()).Manifest.Fingerprint);
        Assert.NotEqual(baseline, new ColorAnalyzer(new ColorSettings { WorkingEdge = 400 }).Manifest.Fingerprint);
        Assert.NotEqual(baseline, new ColorAnalyzer(new ColorSettings { PaletteSize = 6 }).Manifest.Fingerprint);
        Assert.NotEqual(baseline, new ColorAnalyzer(new ColorSettings { MaxPaletteSamples = 2048 }).Manifest.Fingerprint);
    }

    [Fact]
    public void Settings_RoundTripThroughContractJson()
    {
        var settings = new ColorSettings { WorkingEdge = 400, PaletteSize = 7, MaxPaletteSamples = 3000 };
        var json = JsonSerializer.Serialize(settings, ContractJson.Options);
        Assert.Contains("\"workingEdge\":400", json, StringComparison.Ordinal);
        Assert.Equal(settings, CreateContext(json).GetSettings<ColorSettings>());
    }

    [Fact]
    public async Task AnalyzeAsync_IsDeterministic()
    {
        var rgb = Scene(W, H, seed: 7);
        var first = await new ColorAnalyzer().AnalyzeAsync(ToFrame(rgb, W, H), Context);
        var analyzer = new ColorAnalyzer();
        var second = await analyzer.AnalyzeAsync(ToFrame(rgb, W, H), Context);
        var third = await analyzer.AnalyzeAsync(ToFrame((byte[])rgb.Clone(), W, H), Context);

        var expected = TestJson.Serialize(first);
        Assert.Equal(expected, TestJson.Serialize(second));
        Assert.Equal(expected, TestJson.Serialize(third));
    }

    [Fact]
    public async Task AnalyzeAsync_32WayConcurrent_MatchesSequential()
    {
        var analyzer = new ColorAnalyzer();
        var images = new byte[8][];
        var expected = new string[8];
        for (var i = 0; i < images.Length; i++)
        {
            var width = i % 2 == 0 ? W : 480;
            var height = i % 2 == 0 ? H : 360;
            images[i] = Scene(width, height, seed: i);
            expected[i] = TestJson.Serialize(await analyzer.AnalyzeAsync(new Frame(width, height, images[i], width, height), Context));
        }

        var tasks = new Task<string>[32];
        for (var t = 0; t < tasks.Length; t++)
        {
            var index = t % images.Length;
            tasks[t] = Task.Run(async () =>
            {
                var width = index % 2 == 0 ? W : 480;
                var height = index % 2 == 0 ? H : 360;
                var result = string.Empty;
                for (var repeat = 0; repeat < 4; repeat++)
                {
                    result = TestJson.Serialize(await analyzer.AnalyzeAsync(new Frame(width, height, images[index], width, height), Context));
                }

                return result;
            });
        }

        var results = await Task.WhenAll(tasks);
        for (var t = 0; t < results.Length; t++)
        {
            Assert.Equal(expected[t % images.Length], results[t]);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new ColorAnalyzer().AnalyzeAsync(ToFrame(Scene(W, H), W, H), Context, cts.Token));
    }

    [Fact]
    public async Task AnalyzeAsync_PopulatesEveryField()
    {
        var f = await new ColorAnalyzer().AnalyzeAsync(ToFrame(Scene(W, H), W, H), Context);

        Assert.NotNull(f.MeanLuma);
        Assert.NotNull(f.LumaP05);
        Assert.NotNull(f.LumaP50);
        Assert.NotNull(f.LumaP95);
        Assert.NotNull(f.ShadowClip);
        Assert.NotNull(f.HighlightClip);
        Assert.NotNull(f.Contrast);
        Assert.NotNull(f.Key);
        Assert.NotNull(f.Colorfulness);
        Assert.NotNull(f.MeanChroma);
        Assert.NotNull(f.Saturation);
        Assert.NotNull(f.Monochrome);
        Assert.NotNull(f.Warmth);
        Assert.NotNull(f.Tint);
        Assert.NotNull(f.Sharpness);
        Assert.NotNull(f.HueHistogram);
        Assert.NotNull(f.LumaHistogram);
        Assert.NotNull(f.Palette);
        Assert.NotEmpty(f.Extras);

        Assert.InRange(f.MeanLuma.Value, 0.0, 1.0);
        Assert.InRange(f.Colorfulness.Value, 0.0, 1.0);
        Assert.InRange(f.Saturation.Value, 0.0, 1.0);
        Assert.InRange(f.Sharpness.Value, 0.0, 1.0);
        Assert.Equal(1.0, f.HueHistogram.Sum(), 9);
        Assert.Equal(1.0, f.LumaHistogram.Sum(), 9);
    }
}
