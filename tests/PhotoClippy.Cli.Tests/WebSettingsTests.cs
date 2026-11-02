using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Cli.Web;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli.Tests;

public sealed class WebSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photo-clippy-web-tests", Guid.NewGuid().ToString("N"));
    private readonly string _browseRoot;
    private readonly string _library;
    private readonly string _config;
    private readonly string _home;

    public WebSettingsTests()
    {
        _browseRoot = Path.Combine(_directory, "photos");
        _library = Path.Combine(_browseRoot, "2024");
        _config = Path.Combine(_directory, "photo-clippy.json");
        _home = Path.Combine(_directory, "home");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(Path.Combine(_browseRoot, "2025"));
        Directory.CreateDirectory(Path.Combine(_browseRoot, ".thumbnails"));
        Directory.CreateDirectory(Path.Combine(_directory, "outside"));
    }

    [Fact]
    public void ComputePins_ReportsEnvironmentAndCommandLineOverridesButNotPaths()
    {
        var env = new Hashtable
        {
            ["PHOTO_CLIPPY_REPORT__OPTIONS__THEME"] = "dark",
            ["PHOTO_CLIPPY_SERVE__BROWSEROOTS__0"] = "/photos",
            ["PHOTO_CLIPPY_PATHS__HOME"] = "/data",
        };
        var fileOnly = ConfigLoader.Load(null, new Hashtable());
        var withEnvironment = ConfigLoader.Load(null, env);
        var effective = ConfigLoader.Load(null, env);
        effective.Report.Output = "/tmp/elsewhere";

        var pins = SettingsService.ComputePins(new ConfigLayers(fileOnly, withEnvironment, effective));

        Assert.Equal(SettingsService.FromEnvironment, pins["report.options.theme"]);
        Assert.Equal(SettingsService.FromEnvironment, pins["serve.browseRoots"]);
        Assert.Equal(SettingsService.FromCommandLine, pins["report.output"]);
        Assert.DoesNotContain(pins.Keys, k => k.StartsWith("paths", StringComparison.OrdinalIgnoreCase));
        Assert.True(SettingsService.IsPinned(pins, "report.options", out var source));
        Assert.Equal(SettingsService.FromEnvironment, source);
        Assert.False(SettingsService.IsPinned(pins, "report.title", out _));
        Assert.False(SettingsService.IsPinned(pins, "serve.rescanHours", out _));
    }

    [Fact]
    public async Task SaveAsync_WritesEditableFields_PreservesProtectedOnes_AndHotApplies()
    {
        var file = BaseOptions();
        file.Plugins.Directory = Path.Combine(_directory, "plugins");
        file.Report.Output = Path.Combine(_directory, "report");
        File.WriteAllText(_config, ConfigLoader.Serialize(file));
        using var harness = CreateHarness();
        var applied = 0;
        harness.Settings.Applied += () => applied++;

        var draft = harness.Settings.LoadDraft().Options;
        draft.Serve.RescanHours = 6;
        draft.Report.Title = "Holiday habits";
        draft.Library.Roots = [_library, $"  {Path.Combine(_browseRoot, "2025")} ", _library, " "];
        draft.Serve.BrowseRoots = ["/"];
        draft.Serve.Urls = "http://0.0.0.0:1";
        draft.Plugins.Directory = "/somewhere/else";
        draft.Report.Output = "/somewhere/else";

        var outcome = await harness.Settings.SaveAsync(draft, harness.Browser, CancellationToken.None);

        Assert.True(outcome.Saved, string.Join("; ", outcome.Errors));
        Assert.False(outcome.Deferred);
        var saved = ConfigLoader.Load(_config, new Hashtable());
        Assert.Equal(6, saved.Serve.RescanHours);
        Assert.Equal("Holiday habits", saved.Report.Title);
        Assert.Equal([_library, Path.Combine(_browseRoot, "2025")], saved.Library.Roots);
        Assert.Equal([_browseRoot], saved.Serve.BrowseRoots);
        Assert.Equal(file.Serve.Urls, saved.Serve.Urls);
        Assert.Equal(file.Plugins.Directory, saved.Plugins.Directory);
        Assert.Equal(file.Report.Output, saved.Report.Output);
        Assert.True(File.Exists(_config + ".bak"));

        Assert.Equal(1, applied);
        Assert.False(harness.Settings.PendingApply);
        Assert.Equal(6, harness.Runtime.Options.Serve.RescanHours);
        Assert.Equal("Holiday habits", harness.Runtime.Options.Report.Title);
        Assert.Equal(2, harness.Runtime.Options.Library.Roots.Count);
    }

    [Fact]
    public async Task SaveAsync_RejectsInvalidDraftsWithoutTouchingTheFile()
    {
        File.WriteAllText(_config, ConfigLoader.Serialize(BaseOptions()));
        var before = File.ReadAllText(_config);
        using var harness = CreateHarness();

        var draft = harness.Settings.LoadDraft().Options;
        draft.Library.Roots = [.. draft.Library.Roots, Path.Combine(_directory, "outside")];
        draft.Analysis.Scope = "everything";
        draft.Plugins.Recommender = " ";
        draft.Recommendation.Candidates = ["not a lens"];
        draft.Serve.RescanHours = -1;

        var outcome = await harness.Settings.SaveAsync(draft, harness.Browser, CancellationToken.None);

        Assert.False(outcome.Saved);
        Assert.Contains(outcome.Errors, e => e.Contains("outside the folders", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.StartsWith("Scope", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.Contains("recommender", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.StartsWith("Default candidate", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.StartsWith("Rescan interval", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(_config));
        Assert.False(File.Exists(_config + ".bak"));
    }

    [Fact]
    public async Task SaveAsync_FirstSaveCreatesConfigInHome()
    {
        using var harness = CreateHarness(useConfigFile: false, seed: [_library]);
        var draft = harness.Settings.LoadDraft();
        Assert.False(draft.ConfigExists);
        Assert.Equal([_library], draft.Options.Library.Roots);

        var outcome = await harness.Settings.SaveAsync(draft.Options, harness.Browser, CancellationToken.None);

        Assert.True(outcome.Saved, string.Join("; ", outcome.Errors));
        var written = Path.Combine(_home, ConfigLoader.DefaultFileName);
        Assert.Equal(Path.GetFullPath(written), harness.Runtime.ConfigFile);
        Assert.Equal([_library], ConfigLoader.Load(written, new Hashtable()).Library.Roots);
    }

    [Fact]
    public void FolderBrowser_ListsVisibleSubfoldersInsideRootsOnly()
    {
        var browser = new FolderBrowser(() => [_browseRoot, Path.Combine(_directory, "missing")]);

        var roots = browser.List(null);
        Assert.Equal([_browseRoot], roots.Folders.Select(f => f.Path));

        var listing = browser.List(_browseRoot);
        Assert.Null(listing.Error);
        Assert.Equal(string.Empty, listing.Parent);
        Assert.Equal(["2024", "2025"], listing.Folders.Select(f => f.Name));
        Assert.Equal(_browseRoot, browser.List(_library).Parent);

        Assert.True(browser.IsAllowed(_library + Path.DirectorySeparatorChar, out var full));
        Assert.Equal(_library, full);
        Assert.False(browser.IsAllowed(Path.Combine(_directory, "outside"), out _));
        Assert.False(browser.IsAllowed(Path.Combine(_browseRoot, "..", "outside"), out _));
        Assert.False(browser.IsAllowed(_browseRoot + "-sibling", out _));
        Assert.False(browser.IsAllowed(Path.Combine(_browseRoot, "missing"), out _));
        Assert.NotNull(browser.List(Path.Combine(_directory, "outside")).Error);
    }

    [Fact]
    public void FolderBrowser_DoesNotFollowSymbolicLinksOutOfTheRoots()
    {
        var link = Path.Combine(_browseRoot, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_directory, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var browser = new FolderBrowser(() => [_browseRoot]);

        Assert.DoesNotContain(browser.List(_browseRoot).Folders, f => f.Name == "escape");
        Assert.False(browser.IsAllowed(link, out _));
        Assert.NotNull(browser.List(link).Error);
    }

    [Fact]
    public async Task ScanScheduler_RescheduleAppliesNewInterval()
    {
        var options = BaseOptions();
        options.Serve.RescanHours = 0;
        File.WriteAllText(_config, ConfigLoader.Serialize(options));
        using var harness = CreateHarness();
        using var stop = new CancellationTokenSource();
        var loop = harness.Scheduler.RunAsync(stop.Token);

        await Task.Delay(50);
        Assert.Null(harness.Scheduler.NextRunUtc);

        harness.Runtime.Options.Serve.RescanHours = 2;
        harness.Scheduler.Reschedule();
        var next = await WaitForAsync(() => harness.Scheduler.NextRunUtc);
        Assert.InRange(next - DateTimeOffset.UtcNow, TimeSpan.FromHours(1.9), TimeSpan.FromHours(2));

        harness.Runtime.Options.Serve.RescanHours = 0;
        harness.Scheduler.Reschedule();
        await WaitForAsync(() => harness.Scheduler.NextRunUtc is null ? (DateTimeOffset?)DateTimeOffset.MinValue : null);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(harness.Scheduler.NextRunUtc);
    }

    private AppOptions BaseOptions()
    {
        var options = new AppOptions
        {
            Library = new LibraryOptions { Roots = [_library] },
            Analysis = new AnalysisOptions { Frames = false },
        };
        options.Serve.BrowseRoots = [_browseRoot];
        return options;
    }

    private Harness CreateHarness(bool useConfigFile = true, IReadOnlyList<string>? seed = null)
    {
        var runtime = AppRuntime.Create(new GlobalSettings(useConfigFile ? _config : null, _home, true, false, true));
        if (!useConfigFile)
        {
            runtime.Options.Serve.BrowseRoots = [_browseRoot];
            if (seed is not null)
            {
                runtime.Options.Library.Roots = [.. seed];
            }
        }

        var coordinator = new ScanCoordinator(runtime, NullLogger<ScanCoordinator>.Instance);
        var scheduler = new ScanScheduler(runtime, coordinator, NullLogger<ScanScheduler>.Instance);
        var settings = new SettingsService(runtime, coordinator, scheduler, seed ?? [], NullLogger<SettingsService>.Instance, CancellationToken.None);
        var browser = new FolderBrowser(() => settings.BrowseRoots);
        return new Harness(runtime, coordinator, scheduler, settings, browser);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe)
        where T : struct
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not reached in time.");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed record Harness(AppRuntime Runtime, ScanCoordinator Coordinator, ScanScheduler Scheduler, SettingsService Settings, FolderBrowser Browser) : IDisposable
    {
        public void Dispose()
        {
            Scheduler.Dispose();
            Coordinator.Dispose();
            Runtime.Dispose();
        }
    }
}
