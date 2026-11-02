namespace PhotoClippy.Contracts;

public enum InsightTone
{
    Neutral,
    Positive,
    Attention,
}

/// <summary>A short, human-readable finding shown in a report section.</summary>
public sealed record Insight
{
    /// <summary>One of <see cref="ReportSections"/>.</summary>
    public required string Section { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    /// <summary>Optional headline figure, e.g. "62%" or "f/1.8".</summary>
    public string? Metric { get; init; }
    public InsightTone Tone { get; init; }
}

public static class ReportSections
{
    public const string Overview = "overview";
    public const string Gear = "gear";
    public const string Focal = "focal";
    public const string Aperture = "aperture";
    public const string Light = "light";
    public const string Subjects = "subjects";
    public const string Composition = "composition";
    public const string Color = "color";
    public const string Time = "time";
    public const string Gaps = "gaps";
    public const string Recommendation = "recommendation";
    public const string Method = "method";
}

public sealed record RunInfo
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }
    public string ToolVersion { get; init; } = "0.0.0";
    public string? Host { get; init; }
    public IReadOnlyList<PluginManifest> Plugins { get; init; } = [];
    /// <summary>Stage name → seconds.</summary>
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>Everything a renderer needs. Renderers must not need any other input.</summary>
public sealed record ReportModel
{
    public required RunInfo Run { get; init; }
    public required ShootingProfile Profile { get; init; }
    public RecommendationSet? Recommendation { get; init; }
    public IReadOnlyList<Insight> Insights { get; init; } = [];
    public GenreTaxonomy Taxonomy { get; init; } = new();
    public string Title { get; init; } = "Photo habits";
}

public sealed record RenderTarget
{
    public required string OutputDirectory { get; init; }
    public string BaseName { get; init; } = "photo-report";
    /// <summary>Renderer options, e.g. theme=light|dark|auto, embedFonts=true.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

public sealed record RenderResult
{
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>Report rendering component.</summary>
public interface IReportRenderer : IPlugin
{
    /// <summary>Format id, e.g. "html", "json", "csv", "md".</summary>
    string Format { get; }

    Task<RenderResult> RenderAsync(ReportModel model, RenderTarget target, CancellationToken cancellationToken = default);
}
