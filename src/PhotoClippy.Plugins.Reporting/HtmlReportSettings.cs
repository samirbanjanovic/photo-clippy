namespace PhotoClippy.Plugins.Reporting;

/// <summary>Settings for <c>builtin.report.html</c> (config section <c>plugins:builtin.report.html</c>).</summary>
public sealed record HtmlReportSettings
{
    /// <summary>Initial theme: <c>auto</c> (follows the system), <c>light</c> or <c>dark</c>. Overridden by the <c>theme</c> render option.</summary>
    public string Theme { get; init; } = "auto";

    /// <summary>Directory with <c>.woff2</c> files to embed. Overridden by the <c>fontsDirectory</c> render option; defaults to <c>&lt;data&gt;/report/fonts</c>.</summary>
    public string? FontsDirectory { get; init; }

    /// <summary>Embed local fonts as base64 when available. Overridden by the <c>embedFonts</c> render option.</summary>
    public bool EmbedFonts { get; init; } = true;

    /// <summary>Add a collapsible data table under every chart. Overridden by the <c>dataTables</c> render option.</summary>
    public bool DataTables { get; init; } = true;
}
