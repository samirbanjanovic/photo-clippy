using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Testing;

namespace PhotoClippy.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Help_lists_commands_and_exits_ok()
    {
        var result = await CliHarness.InvokeAsync(["--help"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("run", result.Out, StringComparison.Ordinal);
        Assert.Contains("scan", result.Out, StringComparison.Ordinal);
        Assert.Contains("doctor", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_prints_a_version_and_exits_ok()
    {
        var result = await CliHarness.InvokeAsync(["--version"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Matches(@"\d+\.\d+", result.Out);
    }

    [Fact]
    public async Task Unknown_command_fails()
    {
        var result = await CliHarness.InvokeAsync(["bogus-command"]);

        Assert.NotEqual(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("bogus-command", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_init_writes_starter_file_and_refuses_to_overwrite_without_force()
    {
        var file = Path.Combine(_cli.Home, "photo-clippy.json");

        var first = await _cli.RunAsync("config", "init");
        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.True(File.Exists(file));
        Assert.Contains("Wrote", first.Out, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
        {
            var roots = doc.RootElement.GetProperty("library").GetProperty("roots");
            Assert.Equal(_cli.Library, Assert.Single(roots.EnumerateArray()).GetString());
        }

        var second = await _cli.RunAsync("config", "init");
        Assert.Equal(ExitCodes.Usage, second.ExitCode);
        // Redirected stderr is wrapped at 80 columns and the absolute path in this message depends on the checkout location.
        var unwrapped = string.Join(' ', second.Error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("already exists", unwrapped, StringComparison.Ordinal);
        Assert.Contains("--force", second.Error, StringComparison.Ordinal);

        var forced = await _cli.RunAsync("config", "init", "--force");
        Assert.Equal(ExitCodes.Ok, forced.ExitCode);
    }

    [Fact]
    public async Task Config_show_prints_effective_configuration_with_environment_overrides()
    {
        const string variable = "PHOTO_CLIPPY_REPORT__TITLE";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Holiday book");
            var result = await _cli.RunAsync("config", "show");

            Assert.Equal(ExitCodes.Ok, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Out.Trim());
            Assert.Equal("Holiday book", doc.RootElement.GetProperty("report").GetProperty("title").GetString());
            Assert.Equal(_cli.Library, Assert.Single(doc.RootElement.GetProperty("library").GetProperty("roots").EnumerateArray()).GetString());
            Assert.False(doc.RootElement.GetProperty("analysis").GetProperty("frames").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Malformed_config_file_is_a_usage_error_with_hint()
    {
        File.WriteAllText(_cli.ConfigFile, "{ \"library\": ");

        var result = await _cli.RunAsync("config", "show");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Check the config file", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_explicit_config_file_is_a_usage_error()
    {
        var missing = Path.Combine(_cli.Root, "nope.json");

        var result = await CliHarness.InvokeAsync(["config", "show", "--config", missing, "--home", _cli.Home]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Config file not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_reports_checks_and_passes()
    {
        var result = await _cli.RunAsync("doctor");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.All);
        Assert.Contains("runtime", result.Out, StringComparison.Ordinal);
        Assert.Contains("taxonomy", result.Out, StringComparison.Ordinal);
        Assert.Contains("cache", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugins_lists_builtin_plugins()
    {
        var result = await _cli.RunAsync("plugins", "--no-health");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        foreach (var id in new[] { "builtin.metadata.native", "builtin.recommend.mcda", "builtin.report.html", "builtin.report.json", "builtin.color" })
        {
            Assert.Contains(id, result.Out, StringComparison.Ordinal);
        }

        Assert.Contains("Plugin folder:", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_with_candidates_writes_reports_with_ranking()
    {
        var output = Path.Combine(_cli.Root, "out");

        var result = await _cli.RunAsync("run", _cli.Library, "--no-frames", "-c", "24/1.4, 35/1.2", "-o", output);

        Assert.True(result.ExitCode == ExitCodes.Ok, result.All);
        Assert.True(File.Exists(Path.Combine(output, "photo-report.html")));
        var report = LoadReport(output);
        Assert.Equal(4, report.Profile.Library.CapturesInScope);
        var recommendation = Assert.IsType<RecommendationSet>(report.Recommendation);
        Assert.Equal(2, recommendation.CandidateCount);
        Assert.Equal(new[] { "24/1.4", "35/1.2" }, recommendation.Ranked.Select(r => r.Lens.Id).Order(StringComparer.Ordinal));
        Assert.Contains("Your photography", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_without_candidates_renders_analysis_only_and_suggests_recommend()
    {
        var result = await _cli.RunAsync("run", "--no-frames");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.All);
        Assert.Null(LoadReport(_cli.Output).Recommendation);
        Assert.Contains("photo-clippy recommend", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_with_unparseable_candidate_fails_before_scanning()
    {
        var output = Path.Combine(_cli.Root, "out");

        var result = await _cli.RunAsync("run", "--no-frames", "-c", "banana", "-o", output);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Could not parse candidate lens", result.Error, StringComparison.Ordinal);
        Assert.Contains("24/1.4", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Run_with_top_zero_keeps_every_candidate()
    {
        var result = await _cli.RunAsync("run", "--no-frames", "--top", "0", "-c", "24/1.4, 35/1.2, 50/1.8");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.All);
        var recommendation = Assert.IsType<RecommendationSet>(LoadReport(_cli.Output).Recommendation);
        Assert.Equal(3, recommendation.CandidateCount);
        Assert.Equal(3, recommendation.Ranked.Count);
    }

    [Fact]
    public async Task Run_with_unknown_report_format_is_a_usage_error()
    {
        var result = await _cli.RunAsync("run", "--no-frames", "-f", "pdf");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("No report renderer", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_then_summary_recommend_and_report_reuse_the_stored_snapshot()
    {
        var scan = await _cli.RunAsync("scan", "--no-frames");
        Assert.True(scan.ExitCode == ExitCodes.Ok, scan.All);
        Assert.Contains("Next:", scan.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_cli.Output));

        var summary = await _cli.RunAsync("summary");
        Assert.Equal(ExitCodes.Ok, summary.ExitCode);
        Assert.Contains("Your photography", summary.Out, StringComparison.Ordinal);

        var recommend = await _cli.RunAsync("recommend", "-c", "24/1.4, 35/1.2");
        Assert.True(recommend.ExitCode == ExitCodes.Ok, recommend.All);

        var reportDir = Path.Combine(_cli.Root, "out2");
        var report = await _cli.RunAsync("report", "-o", reportDir);
        Assert.True(report.ExitCode == ExitCodes.Ok, report.All);
        Assert.True(File.Exists(Path.Combine(reportDir, "photo-report.html")));
        Assert.Equal(2, Assert.IsType<RecommendationSet>(LoadReport(reportDir).Recommendation).CandidateCount);
    }

    [Theory]
    [InlineData("summary", null)]
    [InlineData("report", null)]
    [InlineData("recommend", "24/1.4")]
    public async Task Commands_needing_a_snapshot_report_no_data_on_a_fresh_home(string command, string? candidates)
    {
        var result = candidates is null
            ? await _cli.RunAsync(command)
            : await _cli.RunAsync(command, "-c", candidates);

        Assert.Equal(ExitCodes.NoData, result.ExitCode);
        Assert.Contains("No stored analysis yet", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_of_empty_folder_reports_no_data()
    {
        var empty = _cli.MakeDir("empty");

        var result = await _cli.RunAsync("scan", empty, "--no-frames");

        Assert.Equal(ExitCodes.NoData, result.ExitCode);
        Assert.Contains("Nothing to analyse", result.Error, StringComparison.Ordinal);
        Assert.Contains("No supported images were found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phone_only_library_falls_back_to_all_devices()
    {
        var phones = _cli.MakeDir("phones");
        File.WriteAllBytes(Path.Combine(phones, "phone.tif"), ExifWriter.BuildTiff(new ExifSpec
        {
            Make = "Apple",
            Model = "iPhone 15 Pro",
            FocalLength = 6.86,
            FocalLength35 = 24,
            FNumber = 1.78,
            Iso = 80,
            ExposureTime = 1.0 / 120,
            DateTimeOriginal = new DateTime(2025, 7, 1, 12, 0, 0),
        }));

        var automatic = await _cli.RunAsync("scan", phones, "--no-frames");
        Assert.True(automatic.ExitCode == ExitCodes.Ok, automatic.All);
        using var runtime = AppRuntime.Create(new GlobalSettings(_cli.ConfigFile, _cli.Home, true, false, true));
        var snapshot = await runtime.Store.LoadLatestSnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal("all", snapshot.Profile.Library.Scope);
        Assert.Equal(1, snapshot.Profile.Library.CapturesInScope);
        Assert.Null(snapshot.Profile.Target);
        Assert.Contains("No interchangeable-lens captures were found, so statistics include all devices.", snapshot.Profile.Quality.Notes);

        var wide = await _cli.RunAsync("scan", phones, "--no-frames", "--scope", "all");
        Assert.True(wide.ExitCode == ExitCodes.Ok, wide.All);
    }

    [Fact]
    public async Task Cache_clear_requires_confirmation()
    {
        var refused = await _cli.RunAsync("cache", "clear");
        Assert.Equal(ExitCodes.Usage, refused.ExitCode);
        Assert.Contains("--yes", refused.Error, StringComparison.Ordinal);

        var stats = await _cli.RunAsync("cache", "stats");
        Assert.Equal(ExitCodes.Ok, stats.ExitCode);

        var cleared = await _cli.RunAsync("cache", "clear", "--yes");
        Assert.Equal(ExitCodes.Ok, cleared.ExitCode);
        Assert.Contains("Cache cleared", cleared.Out, StringComparison.Ordinal);
    }

    private static ReportModel LoadReport(string folder) => Assert.IsType<ReportModel>(JsonSerializer.Deserialize<ReportModel>(
        File.ReadAllText(Path.Combine(folder, "photo-report.json")), ContractJson.Options));
}
