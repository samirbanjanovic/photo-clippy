using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Renders the report as one self-contained, offline HTML file with inline CSS, SVG charts and a little optional JavaScript.</summary>
public sealed class HtmlReportRenderer : IReportRenderer
{
    public const string PluginId = "builtin.report.html";
    public const string PluginVersion = "1.0.0";

    private const string LayoutRevision = "html-layout-1";

    private readonly HtmlReportSettings _settings;
    private readonly string? _defaultFontsDirectory;

    public HtmlReportRenderer(HtmlReportSettings? settings = null, string? dataDirectory = null)
    {
        _settings = settings ?? new HtmlReportSettings();
        _defaultFontsDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "report", "fonts");
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "HTML report",
            Version = PluginVersion,
            Kind = ComponentKind.Report,
            Description = "Self-contained offline HTML report with inline SVG charts, light and dark themes and a print stylesheet.",
            Capabilities = ["report.html", "report.offline", "report.print", "report.dark-mode"],
            Fingerprint = RenderIo.Fingerprint(string.Create(CultureInfo.InvariantCulture,
                $"{LayoutRevision};theme={NormalizeTheme(_settings.Theme)};fonts={_settings.FontsDirectory};embed={_settings.EmbedFonts};tables={_settings.DataTables}")),
        };
    }

    public PluginManifest Manifest { get; }

    public string Format => "html";

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(PluginHealth.Ok());

    /// <summary>Builds the HTML document in memory without writing it.</summary>
    public string RenderToString(ReportModel model, RenderTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        target ??= new RenderTarget { OutputDirectory = "." };
        var theme = NormalizeTheme(RenderIo.Option(target, "theme") ?? _settings.Theme);
        var tables = RenderIo.Flag(target, "dataTables") ?? _settings.DataTables;
        var embed = RenderIo.Flag(target, "embedFonts") ?? _settings.EmbedFonts;
        var fontsDir = FirstNonEmpty(RenderIo.Option(target, "fontsDirectory"), _settings.FontsDirectory, _defaultFontsDirectory);
        var fonts = embed ? FontEmbedder.Embed(fontsDir) : FontEmbedder.Embed(null);
        return new HtmlReportDocument(model, theme, tables, fonts).Render();
    }

    public async Task<RenderResult> RenderAsync(ReportModel model, RenderTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dir = RenderIo.PrepareDirectory(target);
        cancellationToken.ThrowIfCancellationRequested();
        var html = RenderToString(model, target);
        var path = Path.Combine(dir, RenderIo.BaseName(target) + ".html");
        await RenderIo.WriteTextAsync(path, html, cancellationToken).ConfigureAwait(false);
        return new RenderResult { Files = [path] };
    }

    private static string NormalizeTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "auto",
    };

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
