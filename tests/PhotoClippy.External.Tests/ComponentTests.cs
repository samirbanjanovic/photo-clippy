using PhotoClippy.Contracts;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.External.Tests;

public sealed class ComponentTests
{
    private static SourceFile File(string path, MediaKind kind = MediaKind.Jpeg) =>
        new() { Path = path, Size = 1000, LastWriteUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Kind = kind };

    [PythonFact]
    public async Task MetadataExtract_ReturnsExactlyOneResultPerInput_InInputOrder()
    {
        await using var scope = new TestScope();
        var extractor = await scope.CreateAsync<IMetadataExtractor>(TestScope.Mock(ComponentKind.Metadata));

        Assert.True(extractor.CanHandle(File("/photos/a.JPG")));
        Assert.False(extractor.CanHandle(File("/photos/a.png", MediaKind.Png)));

        // maxBatch is 3: chunks are [a, missing, c], [d, explode, f], [g].
        string[] paths = ["/p/a.jpg", "/p/missing.jpg", "/p/c.jpg", "/p/d.jpg", "/p/explode.jpg", "/p/f.jpg", "/p/g.jpg"];
        var results = await extractor.ExtractAsync([.. paths.Select(p => File(p))]);

        Assert.Equal(paths, results.Select(r => r.Path));
        Assert.All(results, r => Assert.Equal("ext.mock", r.Extractor));
        foreach (var ok in new[] { 0, 2, 6 })
        {
            Assert.Equal("MockCam", results[ok].Make);
            Assert.Equal(2.8, results[ok].FNumber);
            Assert.Empty(results[ok].Warnings);
        }

        Assert.Null(results[1].Make);
        Assert.Contains(results[1].Warnings, w => w.Contains("no result returned", StringComparison.Ordinal));
        foreach (var failed in new[] { 3, 4, 5 })
        {
            Assert.Null(results[failed].Make);
            Assert.Contains(results[failed].Warnings, w => w.Contains("extract exploded", StringComparison.Ordinal) && w.Contains("-32000", StringComparison.Ordinal));
        }

        Assert.Empty(await extractor.ExtractAsync([]));
    }

    [PythonFact]
    public async Task Composition_GenresAreCanonicalizedAndNormalized()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition));

        var features = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 10, 10, 10), Frames.Context("c1"));

        Assert.Equal(0.5, features.ThirdsScore);
        Assert.NotNull(features.Genres);
        Assert.Equal(["night", "portrait"], features.Genres.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0.6, features.Genres["night"], 6);
        Assert.Equal(0.4, features.Genres["portrait"], 6);
    }

    [PythonFact]
    public async Task Color_ReturnsFeatures()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<IColorAnalyzer>(TestScope.Mock(ComponentKind.Color));

        var features = await analyzer.AnalyzeAsync(Frames.Solid(6, 6, 51, 0, 0), Frames.Context("c2"));

        Assert.Equal(0.2, features.MeanLuma!.Value, 6);
        Assert.Equal("low", features.Key);
        Assert.Equal(2, features.Extras["mock.index"]);
    }

    [PythonFact]
    public async Task Recommender_FillsMissingRequiredFields()
    {
        await using var scope = new TestScope();
        var recommender = await scope.CreateAsync<IRecommender>(TestScope.Mock(ComponentKind.Recommendation));
        var target = new TargetSystem { Format = "aps-c", CropFactor = 1.5, BodyKey = "body-1" };

        var set = await recommender.RecommendAsync(new RecommendationRequest
        {
            Profile = new ShootingProfile { Library = new LibrarySummary { FilesSeen = 3, Captures = 3, Roots = ["/p"] } },
            Target = target,
            Samples = [new CaptureSample { Focal35 = 35 }, new CaptureSample { Focal35 = 50 }],
            Preferences = new RecommendationPreferences { Preset = "travel" },
        });

        Assert.Equal("ext.mock", set.RecommenderId);
        Assert.Equal("travel", set.Preset);
        Assert.Equal(target, set.Target);
        Assert.Equal("mock headline", set.Headline);
        Assert.Equal(2, set.SampleCount);
    }

    [PythonFact]
    public async Task LensCatalog_SearchesAndTreatsMountLookupAsOptional()
    {
        await using var scope = new TestScope();
        var catalog = await scope.CreateAsync<ILensCatalog>(TestScope.Mock(ComponentKind.LensCatalog));

        var info = await catalog.GetInfoAsync();
        Assert.True(info.Available);
        Assert.Equal(2, info.LensCount);
        Assert.Equal(2, (await catalog.RefreshAsync()).LensCount);

        var found = await catalog.SearchAsync(new LensCatalogQuery { Text = "16-55", Mount = "Mock Y" });
        var zoom = Assert.Single(found);
        Assert.Equal("Mock 16-55mm f/2.8", zoom.DisplayName);
        Assert.Equal(655, zoom.WeightG);
        Assert.Empty(await catalog.SearchAsync(new LensCatalogQuery { Text = "23", Mount = "Mock Y" }));

        Assert.Null(await catalog.MountForBodyAsync("Mock", "Body 1"));
    }

    [PythonFact]
    public async Task ReportRenderer_ResolvesFilesAgainstOutputDirectory()
    {
        await using var scope = new TestScope();
        var renderer = await scope.CreateAsync<IReportRenderer>(TestScope.Mock(ComponentKind.Report));
        Assert.Equal("txt", renderer.Format);

        var model = new ReportModel
        {
            Run = new RunInfo { RunId = "r1", StartedUtc = DateTimeOffset.UnixEpoch },
            Profile = new ShootingProfile { Library = new LibrarySummary { FilesSeen = 1, Captures = 1, Roots = ["/p"] } },
            Title = "Mock title",
        };
        var output = Path.Combine(scope.Root, "reports", "nested");
        var result = await renderer.RenderAsync(model, new RenderTarget { OutputDirectory = output, BaseName = "mine" });

        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(output, "mine.txt"), file);
        Assert.Equal("Mock title", await System.IO.File.ReadAllTextAsync(file));
    }

    [PythonFact]
    public async Task Health_ReflectsPluginAnswer()
    {
        await using var scope = new TestScope();
        var healthy = await scope.CreateAsync<IColorAnalyzer>(TestScope.Mock(ComponentKind.Color));
        var sick = await scope.CreateAsync<IColorAnalyzer>(TestScope.Mock(ComponentKind.Color, new Dictionary<string, string> { ["MOCK_UNHEALTHY"] = "1" }));
        var silent = await scope.CreateAsync<IColorAnalyzer>(TestScope.Mock(ComponentKind.Color, new Dictionary<string, string> { ["MOCK_NO_HEALTH"] = "1" }));

        var ok = await healthy.CheckHealthAsync();
        Assert.True(ok.Healthy);
        Assert.Contains(((IExternalPlugin)healthy).ProcessId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), ok.Message, StringComparison.Ordinal);

        var bad = await sick.CheckHealthAsync();
        Assert.False(bad.Healthy);
        Assert.Contains("sick", bad.Message, StringComparison.Ordinal);

        Assert.True((await silent.CheckHealthAsync()).Healthy);
    }

    [PythonFact]
    public async Task Settings_ArePassedAndFingerprinted()
    {
        await using var scope = new TestScope();
        var options = TestScope.Mock(ComponentKind.Composition) with { Settings = System.Text.Json.JsonDocument.Parse("""{"a":1}""").RootElement };
        var first = await scope.CreateAsync<ICompositionAnalyzer>(options);
        var second = await scope.CreateAsync<ICompositionAnalyzer>(options with { Settings = System.Text.Json.JsonDocument.Parse("""{"a":2}""").RootElement });

        Assert.NotNull(first.Manifest.Fingerprint);
        Assert.NotEqual(first.Manifest.Fingerprint, second.Manifest.Fingerprint);
        Assert.NotEqual(first.Manifest.CacheKey, second.Manifest.CacheKey);
    }
}
