using PhotoClippy.Contracts;
using PhotoClippy.Core.Analysis;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Pipeline;
using PhotoClippy.Core.Plugins;
using PhotoClippy.Core.Storage;

namespace PhotoClippy.Core.Tests;

public sealed class PipelineTests : IDisposable
{
    private static readonly IReadOnlySet<ComponentKind> AnalysisKinds = new HashSet<ComponentKind> { ComponentKind.Metadata, ComponentKind.Composition, ComponentKind.Color };
    private static readonly DateTime T0 = new(2023, 5, 1, 10, 0, 0);

    private readonly TestDir _dir = new("pipeline");
    private readonly AnalysisStore _store;
    private readonly string _library;

    public PipelineTests()
    {
        _library = _dir.Combine("library");
        _dir.WriteTiff("library/a.tif");
        _dir.WriteTiff("library/b.tif");
        _dir.WriteTiff("library/trip/c.tif");
        _dir.WriteText("library/notes.txt", "not a photo");
        _store = AnalysisStore.Open(_dir.Combine("home", "cache.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static PhotoMetadata Nikon(SourceFile file) => TestData.NikonMeta(file.Path, Offset(file), focal: Path.GetFileName(file.Path) == "c.tif" ? 70 : 35);

    private static PhotoMetadata Phone(SourceFile file) => TestData.PhoneMeta(file.Path, Offset(file));

    private static DateTime Offset(SourceFile file) => Path.GetFileName(file.Path) switch
    {
        "a.tif" => T0,
        "b.tif" => T0.AddHours(1),
        _ => T0.AddDays(1),
    };

    private AppOptions Options(string[] metadata, string[]? composition = null, string[]? color = null, bool frames = true)
    {
        var options = new AppOptions { Plugins = PluginHostTests.Options(metadata, composition, color) };
        options.Library.Roots = [_library];
        options.Analysis.Frames = frames;
        options.Analysis.CpuConcurrency = 1;
        options.Analysis.IoConcurrency = 1;
        return options;
    }

    private async Task<PluginSet> PluginsAsync(AppOptions options, params IPlugin[] plugins)
    {
        var catalog = new PluginCatalog();
        foreach (var plugin in plugins)
        {
            catalog.Add(new FakeFactory(plugin.Manifest.Id, plugin.Manifest.Kind, _ => plugin));
        }

        var host = new PluginHost(catalog, PluginHostTests.Paths(_dir.Combine("home")), TestData.Taxonomy);
        return await host.CreateAsync(options.Plugins, AnalysisKinds);
    }

    private Task<AnalysisSnapshot> RunAsync(AppOptions options, PluginSet plugins, IFrameSource? frames = null, IProgress<PipelineProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new AnalysisPipeline(options, new PipelineServices
        {
            Plugins = plugins,
            Store = _store,
            Taxonomy = TestData.Taxonomy,
            FrameSource = frames,
            ToolVersion = "9.9.9-test",
        }).RunAsync(progress, cancellationToken);

    [Fact]
    public async Task Fake_extractor_drives_the_profile()
    {
        var extractor = new FakeExtractor { Map = Nikon };
        var options = Options(["fake.metadata"], frames: false);
        await using var plugins = await PluginsAsync(options, extractor);
        var progress = new RecordingProgress();

        var snapshot = await RunAsync(options, plugins, progress: progress);

        Assert.Equal(3, extractor.FilesExtracted);
        var library = snapshot.Profile.Library;
        Assert.Equal(3, library.FilesSeen);
        Assert.Equal(3, library.Captures);
        Assert.Equal(3, library.CapturesInScope);
        Assert.Equal("interchangeable", library.Scope);
        Assert.Equal(new[] { "library" }, library.Roots);
        Assert.Equal(new[] { "fake.metadata" }, snapshot.Run.Plugins.Select(p => p.Id));
        Assert.Equal("9.9.9-test", snapshot.Run.ToolVersion);
        Assert.Equal("off", snapshot.Run.Options["frames"]);
        Assert.Contains(snapshot.Profile.Quality.Notes, n => n.StartsWith("Pixel analysis was disabled (--no-frames)", StringComparison.Ordinal));
        Assert.Contains(snapshot.Insights, i => i.Title == "Library");
        Assert.Equal(2, snapshot.Profile.Distributions[DistributionIds.Focal35].Bins.Count(b => b.Count > 0));
        Assert.Equal(new[] { PipelineStages.Discover, PipelineStages.Metadata, PipelineStages.Normalize, PipelineStages.Profile }, progress.Stages);

        var saved = await _store.LoadLatestSnapshotAsync();
        Assert.Equal(snapshot.Run.RunId, saved!.Run.RunId);
    }

    [Fact]
    public async Task Analyzer_signals_and_extensions_reach_the_profile()
    {
        var composition = new FakeComposition();
        var frames = new FakeFrameSource();
        var options = Options(["fake.metadata"], ["fake.composition"], ["fake.color"]);
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon }, composition, new FakeColor());
        var progress = new RecordingProgress();

        var snapshot = await RunAsync(options, plugins, frames, progress);

        Assert.Contains(PipelineStages.Frames, progress.Stages);
        Assert.Equal(3, frames.Decodes);
        Assert.Equal(3, composition.Calls);
        var profile = snapshot.Profile;
        Assert.Equal(3, profile.Library.FramesAnalyzed);
        Assert.Equal("fake.composition", profile.Quality.GenreSource);
        Assert.Equal(0.7, profile.Extensions["composition.clip.top1Prob"].Stats!.Median, 6);
        Assert.Equal(0.3, profile.Extensions["color.meanHue"].Stats!.Median, 6);
        Assert.DoesNotContain(profile.Extensions.Keys, k => k.EndsWith("nan", StringComparison.Ordinal));
        Assert.Equal(3, profile.Distributions[DistributionIds.Genre].Bins.Sum(b => b.Count), 6);
        Assert.DoesNotContain(profile.Quality.Notes, n => n.Contains("Pixel analysis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_run_reuses_cached_metadata_and_features()
    {
        var extractor = new FakeExtractor { Map = Nikon };
        var composition = new FakeComposition();
        var frames = new FakeFrameSource();
        var options = Options(["fake.metadata"], ["fake.composition"]);
        await using var plugins = await PluginsAsync(options, extractor, composition);

        var first = await RunAsync(options, plugins, frames);
        var second = await RunAsync(options, plugins, frames);

        Assert.Equal(1, extractor.Calls);
        Assert.Equal(3, composition.Calls);
        Assert.Equal(3, frames.Decodes);
        Assert.Equal(first.Profile.Library.Captures, second.Profile.Library.Captures);
        Assert.Equal(3, second.Profile.Library.FramesAnalyzed);
        Assert.Equal(2, _store.GetStats().Snapshots);
    }

    [Fact]
    public async Task Changed_file_is_re_extracted()
    {
        var extractor = new FakeExtractor { Map = Nikon };
        var options = Options(["fake.metadata"], frames: false);
        await using var plugins = await PluginsAsync(options, extractor);

        await RunAsync(options, plugins);
        File.SetLastWriteTimeUtc(_dir.Combine("library", "a.tif"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await RunAsync(options, plugins);

        Assert.Equal(4, extractor.FilesExtracted);
    }

    [Fact]
    public async Task Throwing_extractor_is_isolated_and_reported()
    {
        var good = new FakeExtractor { Map = Nikon };
        var options = Options(["fake.metadata.throwing", "fake.metadata"], frames: false);
        await using var plugins = await PluginsAsync(options, new ThrowingExtractor(), good);

        var snapshot = await RunAsync(options, plugins);

        Assert.Equal(3, snapshot.Profile.Library.Captures);
        Assert.Equal(0, snapshot.Profile.Quality.MetadataErrors);
        Assert.Contains("Metadata extraction failed for 1 batch(es); affected files are counted as metadata errors and will be retried next run.", snapshot.Profile.Quality.Notes);
    }

    [Fact]
    public async Task Only_extractor_failing_counts_metadata_errors_and_is_not_cached()
    {
        var options = Options(["fake.metadata.throwing"], frames: false);
        await using var plugins = await PluginsAsync(options, new ThrowingExtractor());

        var snapshot = await RunAsync(options, plugins);

        Assert.Equal(3, snapshot.Profile.Quality.MetadataErrors);
        Assert.Equal(0, snapshot.Profile.Library.CapturesInScope);
        Assert.Contains(snapshot.Profile.Quality.Notes, n => n.StartsWith("Metadata extraction failed", StringComparison.Ordinal));
        Assert.Equal(0, _store.GetStats().MetadataRows);
    }

    [Fact]
    public async Task Throwing_analyzer_is_isolated_and_reported()
    {
        var composition = new FakeComposition();
        var options = Options(["fake.metadata"], ["fake.composition.throwing", "fake.composition"]);
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon }, new ThrowingComposition(), composition);

        var snapshot = await RunAsync(options, plugins, new FakeFrameSource());

        Assert.Equal(3, composition.Calls);
        Assert.Equal(3, snapshot.Profile.Library.FramesAnalyzed);
        Assert.Equal("fake.composition", snapshot.Profile.Quality.GenreSource);
        Assert.Contains("fake.composition.throwing failed on 3 image(s); see the log for details.", snapshot.Profile.Quality.Notes);
    }

    [Fact]
    public async Task Plugin_host_notes_are_carried_into_the_profile()
    {
        var options = Options(["fake.metadata"], ["fake.composition.unhealthy"], ["missing.color"], frames: false);
        var unhealthy = new FakeComposition("fake.composition.unhealthy") { ThrowOnHealth = true };
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon }, unhealthy);

        var snapshot = await RunAsync(options, plugins);

        Assert.Equal(3, snapshot.Profile.Library.Captures);
        Assert.Contains("fake.composition.unhealthy: unavailable — health probe exploded", snapshot.Profile.Quality.Notes);
        Assert.Contains("missing.color: plugin not found.", snapshot.Profile.Quality.Notes);
        Assert.Equal(0, unhealthy.Calls);
    }

    [Fact]
    public async Task Frames_without_decoder_or_analyzers_are_noted()
    {
        var options = Options(["fake.metadata"], ["fake.composition"]);
        await using (var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon }, new FakeComposition()))
        {
            var snapshot = await RunAsync(options, plugins, frames: null);
            Assert.Contains("No image decoder is available; pixel analysis was skipped.", snapshot.Profile.Quality.Notes);
        }

        var bare = Options(["fake.metadata"]);
        await using (var plugins = await PluginsAsync(bare, new FakeExtractor { Map = Nikon }))
        {
            var frames = new FakeFrameSource();
            var snapshot = await RunAsync(bare, plugins, frames);
            Assert.Contains(snapshot.Profile.Quality.Notes, n => n.StartsWith("No composition or colour analyzers are enabled", StringComparison.Ordinal));
            Assert.Equal(0, frames.Decodes);
        }
    }

    [Fact]
    public async Task Library_without_interchangeable_lens_falls_back_to_all_devices()
    {
        var options = Options(["fake.metadata"], frames: false);
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Phone });

        var snapshot = await RunAsync(options, plugins);

        Assert.Equal("all", snapshot.Profile.Library.Scope);
        Assert.Equal(3, snapshot.Profile.Library.CapturesInScope);
        Assert.Contains("No interchangeable-lens captures were found, so statistics include all devices.", snapshot.Profile.Quality.Notes);
    }

    [Fact]
    public async Task Date_window_limits_scope_and_is_noted()
    {
        var options = Options(["fake.metadata"], frames: false);
        options.Analysis.Since = T0.Date.AddDays(1);
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon });

        var snapshot = await RunAsync(options, plugins);

        Assert.Equal(3, snapshot.Profile.Library.Captures);
        Assert.Equal(1, snapshot.Profile.Library.CapturesInScope);
        Assert.Contains(snapshot.Profile.Quality.Notes, n => n.StartsWith("Statistics limited to captures from 2023-05-02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pre_cancelled_token_stops_the_run_without_saving()
    {
        var extractor = new FakeExtractor { Map = Nikon };
        var options = Options(["fake.metadata"], frames: false);
        await using var plugins = await PluginsAsync(options, extractor);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(options, plugins, cancellationToken: cts.Token));

        Assert.Equal(0, extractor.Calls);
        Assert.Null(await _store.LoadLatestSnapshotAsync());
    }

    [Fact]
    public async Task Cancellation_during_frame_analysis_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        var options = Options(["fake.metadata"], ["fake.composition.cancelling"]);
        await using var plugins = await PluginsAsync(options, new FakeExtractor { Map = Nikon }, new CancellingComposition(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(options, plugins, new FakeFrameSource(), cancellationToken: cts.Token));

        Assert.Null(await _store.LoadLatestSnapshotAsync());
        Assert.Equal(3, _store.GetStats().MetadataRows);
    }

    [Fact]
    public async Task Missing_metadata_plugins_or_roots_are_rejected()
    {
        var options = Options([], frames: false);
        await using (var empty = await PluginsAsync(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(options, empty));
        }

        var noRoots = Options(["fake.metadata"], frames: false);
        noRoots.Library.Roots = [];
        await using var plugins = await PluginsAsync(noRoots, new FakeExtractor());
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(noRoots, plugins));
    }

    private sealed class RecordingProgress : IProgress<PipelineProgress>
    {
        private readonly List<string> _stages = [];

        public IReadOnlyList<string> Stages
        {
            get
            {
                lock (_stages)
                {
                    return [.. _stages.Distinct()];
                }
            }
        }

        public void Report(PipelineProgress value)
        {
            lock (_stages)
            {
                _stages.Add(value.Stage);
            }
        }
    }

    private sealed class CancellingComposition(CancellationTokenSource cts) : FakePlugin("fake.composition.cancelling", ComponentKind.Composition), ICompositionAnalyzer
    {
        public FrameRequirements Requirements { get; } = new(64);

        public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CompositionFeatures());
        }
    }
}
