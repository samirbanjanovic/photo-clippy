using PhotoClippy.Contracts;
using PhotoClippy.Core.Profiling;
using PhotoClippy.Core.Storage;

namespace PhotoClippy.Core.Tests;

public sealed class AnalysisStoreTests : IDisposable
{
    private readonly TestDir _dir = new("store");
    private readonly AnalysisStore _store;

    public AnalysisStoreTests() => _store = AnalysisStore.Open(_dir.Combine("db", "cache.db"));

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static AnalysisSnapshot Snapshot(string runId, string root = "/lib")
    {
        var profile = new ProfileBuilder(TestData.Taxonomy, new ProfileBuilderOptions()).Build(new LibraryInfo { Roots = [root], FilesSeen = 3 }).Profile;
        return new AnalysisSnapshot
        {
            Run = new RunInfo { RunId = runId, StartedUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), FinishedUtc = new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero), ToolVersion = "9.9.9" },
            Profile = profile,
            Insights = [new Insight { Section = ReportSections.Overview, Title = "Hello", Text = "World" }],
        };
    }

    [Fact]
    public async Task Load_latest_returns_null_when_empty()
    {
        Assert.Null(await _store.LoadLatestSnapshotAsync());
        Assert.Equal(new StoreStats(0, 0, 0, _store.GetStats().SizeBytes), _store.GetStats());
    }

    [Fact]
    public async Task Snapshot_round_trips()
    {
        var id = await _store.SaveSnapshotAsync(Snapshot("run-1", "/photos"));

        var loaded = await _store.LoadLatestSnapshotAsync();

        Assert.True(id > 0);
        Assert.NotNull(loaded);
        Assert.Equal("run-1", loaded.Run.RunId);
        Assert.Equal("9.9.9", loaded.Run.ToolVersion);
        Assert.Equal(new[] { "/photos" }, loaded.Profile.Library.Roots);
        Assert.Equal(3, loaded.Profile.Library.FilesSeen);
        Assert.Equal("Hello", Assert.Single(loaded.Insights).Title);
    }

    [Fact]
    public async Task Keeps_only_last_ten_snapshots()
    {
        for (var i = 0; i < 12; i++)
        {
            await _store.SaveSnapshotAsync(Snapshot($"run-{i}"));
        }

        Assert.Equal(10, _store.GetStats().Snapshots);
        Assert.Equal("run-11", (await _store.LoadLatestSnapshotAsync())!.Run.RunId);
    }

    [Fact]
    public async Task Metadata_cache_hits_only_for_unchanged_files()
    {
        var when = new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var a = TestData.Src("/lib/a.jpg", size: 100, lastWrite: when);
        var b = TestData.Src("/lib/b.jpg", size: 200, lastWrite: when);
        await _store.WriteMetadataAsync([(a, TestData.NikonMeta(a.Path)), (b, TestData.NikonMeta(b.Path, focal: 70))], "native@1");

        var unchanged = new Dictionary<string, SourceFile> { [a.Path] = a, [b.Path] = b };
        var hits = _store.ReadMetadata(unchanged, "native@1").ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(70, hits.Single(h => h.File.Path == b.Path).Metadata.FocalLengthMm);

        var modified = new Dictionary<string, SourceFile>
        {
            [a.Path] = a with { Size = 101 },
            [b.Path] = b with { LastWriteUtc = when.AddSeconds(1) },
        };
        Assert.Empty(_store.ReadMetadata(modified, "native@1"));
        Assert.Empty(_store.ReadMetadata(unchanged, "other@1"));
    }

    [Fact]
    public async Task Feature_rows_upsert_by_path_and_plugin()
    {
        await _store.WriteFeaturesAsync([new FeatureRow("/b", "p1", "k1", 1, 2, "{}"), new FeatureRow("/a", "p1", "k1", 1, 2, "{}")]);
        await _store.WriteFeaturesAsync([new FeatureRow("/a", "p1", "k2", 1, 2, "{\"x\":1}"), new FeatureRow("/a", "p2", "k1", 1, 2, "{}")]);

        var rows = _store.ReadFeatures().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "/a", "/a", "/b" }, rows.Select(r => r.Path));
        Assert.Equal("{\"x\":1}", rows.Single(r => r is { Path: "/a", PluginId: "p1" }).Json);
    }

    [Fact]
    public async Task Clear_removes_everything_and_store_reopens()
    {
        var file = TestData.Src("/lib/a.jpg");
        await _store.WriteMetadataAsync([(file, TestData.NikonMeta(file.Path))], "k");
        await _store.WriteFeaturesAsync([new FeatureRow(file.Path, "p", "k", 1, 1, "{}")]);
        await _store.SaveSnapshotAsync(Snapshot("r"));
        var before = _store.GetStats();

        _store.Clear();

        Assert.Equal((1L, 1L, 1L), (before.MetadataRows, before.FeatureRows, before.Snapshots));
        var after = _store.GetStats();
        Assert.Equal((0L, 0L, 0L), (after.MetadataRows, after.FeatureRows, after.Snapshots));
        using var reopened = AnalysisStore.Open(_store.DatabasePath);
        Assert.Null(await reopened.LoadLatestSnapshotAsync());
    }
}
