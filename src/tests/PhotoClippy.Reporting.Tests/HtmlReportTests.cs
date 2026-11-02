using System.Text.RegularExpressions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Reporting;

namespace PhotoClippy.Reporting.Tests;

public sealed partial class HtmlReportTests
{
    private static readonly string[] Sections =
        ["overview", "gear", "focal", "aperture", "light", "subjects", "composition", "color", "time", "gaps", "recommendation", "method"];

    [Fact]
    public void Full_model_renders_every_section()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>", html.TrimEnd(), StringComparison.Ordinal);
        foreach (var id in Sections)
        {
            Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("24mm f/1.4", html, StringComparison.Ordinal);
        Assert.Contains("NIKKOR Z 24-70mm f/4 S", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_is_self_contained_and_offline()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full);

        Assert.DoesNotMatch(SrcOrHref(), html);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        var urls = ExternalUrl().Matches(html).Select(m => m.Value).Where(u => !u.StartsWith("http://www.w3.org/", StringComparison.Ordinal)).ToList();
        Assert.Empty(urls);
    }

    [Fact]
    public void Numbers_are_always_finite()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full);

        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", html, StringComparison.Ordinal);
    }

    [Fact]
    public void User_and_exif_text_is_escaped()
    {
        var model = ReportFixture.Full with { Title = "<script>alert(1)</script> & co" };
        var html = new HtmlReportRenderer().RenderToString(model);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>FF</b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;FF&lt;/b&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var renderer = new HtmlReportRenderer();

        Assert.Equal(renderer.RenderToString(ReportFixture.Full), renderer.RenderToString(ReportFixture.Full));
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("LIGHT", "light")]
    [InlineData("neon", "auto")]
    public void Theme_option_is_normalised(string requested, string expected)
    {
        var target = new RenderTarget { OutputDirectory = ".", Options = new Dictionary<string, string> { ["theme"] = requested } };

        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full, target);

        Assert.Contains($"data-theme=\"{expected}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_library_still_renders_a_valid_document()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Empty());

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"overview\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_analyzer_signals_are_collapsed_with_readable_titles()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full);

        Assert.Contains("<details class=\"more\">", html, StringComparison.Ordinal);
        Assert.Contains("More on composition", html, StringComparison.Ordinal);
        Assert.Contains("Dark share", html, StringComparison.Ordinal);
        Assert.Contains("Clip · top 1 prob", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">composition.darkShare<", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.55, "Largely redundant with NIKKOR Z 24-70mm f/4 S")]
    [InlineData(0.2, "Partly redundant with NIKKOR Z 24-70mm f/4 S")]
    [InlineData(0.05, null)]
    public void Redundancy_wording_follows_thresholds(double redundancy, string? expected)
    {
        var html = RenderWithFirstRanked(r => r with { Redundancy = redundancy, RedundantWith = "NIKKOR Z 24-70mm f/4 S" });

        if (expected is null)
        {
            Assert.DoesNotContain("redundant with NIKKOR Z 24-70mm f/4 S, which you already own", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, html, StringComparison.Ordinal);
            Assert.Contains("redundancy " + Math.Round(redundancy * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Owned_candidate_is_called_out()
    {
        var html = RenderWithFirstRanked(r => r with { AlreadyOwned = true });

        Assert.Contains("You already own this lens", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_candidates_the_section_explains_how_to_supply_them()
    {
        var html = new HtmlReportRenderer().RenderToString(ReportFixture.Full with { Recommendation = null });

        Assert.Contains("id=\"recommendation\"", html, StringComparison.Ordinal);
        Assert.Contains("no candidate lenses were supplied", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"badge\">your candidate</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_writes_one_file_with_a_safe_name()
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            var result = await new HtmlReportRenderer().RenderAsync(ReportFixture.Full, new RenderTarget { OutputDirectory = dir, BaseName = "trip/2025:summer" });

            var file = Assert.Single(result.Files);
            Assert.Equal(dir, Path.GetDirectoryName(file));
            Assert.Equal("trip-2025-summer.html", Path.GetFileName(file));
            Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_honours_cancellation()
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HtmlReportRenderer().RenderAsync(ReportFixture.Full, new RenderTarget { OutputDirectory = dir }, cts.Token));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Manifest_describes_the_plugin()
    {
        var r = new HtmlReportRenderer();

        Assert.Equal("builtin.report.html", r.Manifest.Id);
        Assert.Equal(ComponentKind.Report, r.Manifest.Kind);
        Assert.Equal("html", r.Format);
        Assert.False(string.IsNullOrWhiteSpace(r.Manifest.Fingerprint));
        Assert.NotEqual(r.Manifest.Fingerprint, new HtmlReportRenderer(new HtmlReportSettings { Theme = "dark" }).Manifest.Fingerprint);
    }

    private static string RenderWithFirstRanked(Func<LensRecommendation, LensRecommendation> change)
    {
        var model = ReportFixture.Full;
        var set = model.Recommendation!;
        var ranked = set.Ranked.ToList();
        ranked[0] = change(ranked[0] with { Redundancy = 0, RedundantWith = null, AlreadyOwned = false });
        for (var i = 1; i < ranked.Count; i++)
        {
            ranked[i] = ranked[i] with { Redundancy = 0, RedundantWith = null };
        }

        return new HtmlReportRenderer().RenderToString(model with { Recommendation = set with { Ranked = ranked } });
    }

    [GeneratedRegex("""<(?:script|link|img|iframe)\b[^>]*\b(?:src|href)\s*=\s*["']?(?:https?:)?//""", RegexOptions.IgnoreCase)]
    private static partial Regex SrcOrHref();

    [GeneratedRegex("""https?://[^\s"'<>)]+""", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();
}
