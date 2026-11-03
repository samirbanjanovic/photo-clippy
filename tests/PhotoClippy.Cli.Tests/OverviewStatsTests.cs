using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Cli.Web.Pages;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Services;
using PhotoClippy.Plugins.Reporting;

namespace PhotoClippy.Cli.Tests;

public sealed class OverviewStatsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overview_and_report_show_the_same_focal_summary(bool hasPeak)
    {
        using var harness = new CliHarness();
        using var runtime = AppRuntime.Create(new GlobalSettings(harness.ConfigFile, harness.Home, true, false, true));
        var snapshot = new AnalysisSnapshot
        {
            Run = new RunInfo { RunId = "focal-summary", StartedUtc = DateTimeOffset.UtcNow },
            Profile = new ShootingProfile
            {
                Library = new LibrarySummary { CapturesInScope = 100 },
                Signals = new ShootingSignals { MedianFocal35 = 49.5 },
                FocalPeaks = hasPeak ? [new(24, 0.27, 22, 26)] : [],
            },
        };
        await runtime.Store.SaveSnapshotAsync(snapshot);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<ScanCoordinator>();
        builder.Services.AddSingleton<ScanScheduler>();
        using var host = builder.Build();
        await using var renderer = new HtmlRenderer(host.Services, host.Services.GetRequiredService<ILoggerFactory>());

        var overview = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = await renderer.RenderComponentAsync<Overview>();
            return page.ToHtmlString();
        });
        var report = new HtmlReportRenderer().RenderToString(ReportRunner.BuildModel(snapshot, "Focal summary"));
        var caption = hasPeak
            ? "Dominant focal peak: 24 mm (27% of focal-length samples)"
            : "Dominant focal peak: unavailable";

        foreach (var html in new[] { overview, report })
        {
            Assert.Contains("49.5 mm", html, StringComparison.Ordinal);
            Assert.Contains(FocalSummary.MedianLabel, html, StringComparison.Ordinal);
            Assert.Contains(caption, html, StringComparison.Ordinal);
            Assert.Contains(FocalSummary.Explanation, html, StringComparison.Ordinal);
            Assert.DoesNotContain(">50 mm<", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Most-used focal", html, StringComparison.Ordinal);
        }
    }
}
