using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Cli;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Testing;

namespace PhotoClippy.Cli.Tests;

public sealed class LazyRecommendationTests : IDisposable
{
    private const string OwnedLens = "NIKKOR Z 24-70mm f/4 S";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photo-clippy-lazy-tests", Guid.NewGuid().ToString("N"));
    private readonly string _photo;
    private readonly string _config;
    private readonly string _home;
    private readonly AppOptions _options;

    public LazyRecommendationTests()
    {
        var library = Path.Combine(_directory, "photos");
        Directory.CreateDirectory(library);
        _photo = Path.Combine(library, "capture.tif");
        _config = Path.Combine(_directory, "photo-clippy.json");
        _home = Path.Combine(_directory, "home");
        File.WriteAllBytes(_photo, ExifWriter.BuildTiff(new ExifSpec
        {
            Make = "NIKON CORPORATION",
            Model = "NIKON Z 6_2",
            LensMake = "Nikon",
            LensModel = OwnedLens,
            LensSpec = (24, 70, 4, 4),
            FocalLength = 35,
            FocalLength35 = 35,
            FNumber = 4,
            Iso = 3200,
            ExposureTime = 1.0 / 60,
            DateTimeOriginal = new DateTime(2025, 6, 1, 18, 0, 0),
        }));
        _options = new AppOptions
        {
            Library = new LibraryOptions { Roots = [library] },
            Analysis = new AnalysisOptions { Frames = false },
            Recommendation = new RecommendationOptions { BootstrapReplicates = 0 },
            Report = new ReportOptions { Output = Path.Combine(_directory, "report") },
        };
        _options.Plugins.Reports = ["builtin.report.html", "builtin.report.json"];
        SaveConfig();
    }

    [Theory]
    [InlineData("run", "")]
    [InlineData("run", "24/1.4")]
    [InlineData("run", "not a lens specification")]
    [InlineData("scan", "not a lens specification")]
    public async Task AnalysisCommands_DoNotParseCandidatesOrRequireARecommender(string command, string candidate)
    {
        _options.Recommendation.Candidates = [candidate];
        _options.Plugins.Recommender = "unavailable.recommender";
        SaveConfig();

        Assert.Equal(ExitCodes.Ok, await RunCliAsync(command));
        using var runtime = CreateRuntime();
        var snapshot = await LoadSnapshotAsync(runtime);
        AssertAnalysisOnly(snapshot);

        if (command == "scan")
        {
            Assert.False(Directory.Exists(_options.Report.Output));
            Assert.Equal(ExitCodes.Ok, await RunCliAsync("report"));
        }

        Assert.Null(LoadReport().Recommendation);
        Assert.Contains(OwnedLens, await File.ReadAllTextAsync(Path.Combine(_options.Report.Output, "photo-report.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_NoPromptAlone_DoesNotRequestRanking()
    {
        _options.Plugins.Recommender = "unavailable.recommender";
        SaveConfig();

        Assert.Equal(ExitCodes.Ok, await RunCliAsync("run", "--no-prompt"));
        using var runtime = CreateRuntime();
        AssertAnalysisOnly(await LoadSnapshotAsync(runtime));
        Assert.Null(LoadReport().Recommendation);
    }

    [Fact]
    public async Task Run_ExplicitCandidates_OverridesDefaultsAndRanksOnlyThoseLenses()
    {
        _options.Recommendation.Candidates = ["not a lens specification"];
        SaveConfig();

        Assert.Equal(ExitCodes.Ok, await RunCliAsync("run", "--candidates", "24/1.4, 35/1.2"));
        using var runtime = CreateRuntime();
        var snapshot = await LoadSnapshotAsync(runtime);
        AssertCandidateRanking(snapshot.Recommendation);
        AssertCandidateRanking(LoadReport().Recommendation);
    }

    [Fact]
    public async Task RecommendAndReport_ReuseSavedAnalysisWithoutReadingPhotos()
    {
        Assert.Equal(ExitCodes.Ok, await RunCliAsync("scan"));
        using var runtime = CreateRuntime();
        var before = await LoadSnapshotAsync(runtime);
        File.Delete(_photo);

        Assert.Equal(ExitCodes.Ok, await RunCliAsync("recommend", "--candidates", "24/1.4, 35/1.2"));
        var after = await LoadSnapshotAsync(runtime);
        Assert.Equal(before.Run.StartedUtc, after.Run.StartedUtc);
        Assert.Equal(before.Run.FinishedUtc, after.Run.FinishedUtc);
        AssertCandidateRanking(after.Recommendation);

        _options.Recommendation.Candidates = ["not a lens specification"];
        _options.Plugins.Recommender = "unavailable.recommender";
        SaveConfig();
        Assert.Equal(ExitCodes.Ok, await RunCliAsync("report"));
        AssertCandidateRanking(LoadReport().Recommendation);
        Assert.Equal(before.Run.StartedUtc, LoadReport().Run.StartedUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24/1.4")]
    [InlineData("not a lens specification")]
    public async Task Daemon_ScansRenderAnalysisWithoutParsingOrRankingCandidates(string candidate)
    {
        _options.Recommendation.Candidates = [candidate];
        _options.Plugins.Recommender = "unavailable.recommender";
        SaveConfig();
        using var runtime = CreateRuntime();
        using var coordinator = new ScanCoordinator(runtime, NullLogger<ScanCoordinator>.Instance);

        await ScanAsync(coordinator);

        AssertAnalysisOnly(await LoadSnapshotAsync(runtime));
        Assert.Equal(2, coordinator.State.Files.Count);
        Assert.Null(LoadReport().Recommendation);
    }

    [Fact]
    public async Task Daemon_ExplicitRecommendation_ReusesSnapshotAndNextScanClearsRanking()
    {
        using var runtime = CreateRuntime();
        using var coordinator = new ScanCoordinator(runtime, NullLogger<ScanCoordinator>.Instance);
        await ScanAsync(coordinator);
        var before = await LoadSnapshotAsync(runtime);

        var set = await coordinator.RecommendAsync(new RecommendRequest { Candidates = ["24/1.4", "35/1.2"] }, CancellationToken.None);
        AssertCandidateRanking(set);
        AssertCandidateRanking(LoadReport().Recommendation);
        var ranked = await LoadSnapshotAsync(runtime);
        Assert.Equal(before.Run.StartedUtc, ranked.Run.StartedUtc);
        Assert.Equal(before.Run.FinishedUtc, ranked.Run.FinishedUtc);

        await ScanAsync(coordinator);
        AssertAnalysisOnly(await LoadSnapshotAsync(runtime));
        Assert.Null(LoadReport().Recommendation);
    }

    [Fact]
    public async Task Daemon_ExplicitRecommendation_ReportsInvalidCandidates()
    {
        using var runtime = CreateRuntime();
        using var coordinator = new ScanCoordinator(runtime, NullLogger<ScanCoordinator>.Instance);
        await ScanAsync(coordinator);

        var error = await Assert.ThrowsAsync<CliException>(() => coordinator.RecommendAsync(
            new RecommendRequest { Candidates = ["not a lens specification"] }, CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Contains("Could not parse candidate lens", error.Message, StringComparison.Ordinal);
        AssertAnalysisOnly(await LoadSnapshotAsync(runtime));
    }

    private void SaveConfig() => File.WriteAllText(_config, ConfigLoader.Serialize(_options));

    private Task<int> RunCliAsync(params string[] args) =>
        CliApplication.RunAsync([.. args, "--config", _config, "--home", _home, "--json"]);

    private AppRuntime CreateRuntime() => AppRuntime.Create(new GlobalSettings(_config, _home, true, false, true));

    private static async Task<AnalysisSnapshot> LoadSnapshotAsync(AppRuntime runtime) =>
        Assert.IsType<AnalysisSnapshot>(await runtime.Store.LoadLatestSnapshotAsync());

    private ReportModel LoadReport() => Assert.IsType<ReportModel>(JsonSerializer.Deserialize<ReportModel>(
        File.ReadAllText(Path.Combine(_options.Report.Output, "photo-report.json")), ContractJson.Options));

    private static async Task ScanAsync(ScanCoordinator coordinator)
    {
        Assert.True(coordinator.TryStartScan(ScanTriggers.Api, CancellationToken.None));
        await coordinator.Current.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(coordinator.State.State == "completed", coordinator.State.Error);

        var job = Assert.IsType<ScanJob>(coordinator.Jobs.Last);
        Assert.Equal(coordinator.State.JobId, job.Id);
        Assert.Equal(ScanTriggers.Api, job.Trigger);
        Assert.Equal("completed", job.State);
        Assert.Equal(coordinator.State.RunId, job.RunId);
        Assert.True(job.FilesSeen >= 1);
        Assert.Contains(job.Stages, s => s.Stage == "metadata" && s.Total >= 1);
        Assert.NotNull(job.CpuSeconds);
        Assert.Null(coordinator.RunningJob);
    }

    private static void AssertAnalysisOnly(AnalysisSnapshot snapshot)
    {
        Assert.Equal(1, snapshot.Profile.Library.CapturesInScope);
        Assert.NotNull(snapshot.Profile.Target);
        Assert.Equal(OwnedLens, Assert.Single(snapshot.Owned).Lens.Name);
        Assert.Null(snapshot.Recommendation);
        Assert.DoesNotContain(snapshot.Run.Plugins, p => p.Kind == ComponentKind.Recommendation);
    }

    private static void AssertCandidateRanking(RecommendationSet? set)
    {
        Assert.NotNull(set);
        Assert.Equal(2, set.CandidateCount);
        Assert.Equal(new[] { "24/1.4", "35/1.2" }, set.Ranked.Select(r => r.Lens.Id).Order(StringComparer.Ordinal));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
