using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Core.Services;

public sealed record ReportFailure(string RendererId, string Error);

public sealed record ReportOutcome(IReadOnlyList<string> Files, IReadOnlyList<ReportFailure> Failures);

/// <summary>Feeds a snapshot to every selected report renderer. One failing renderer does not stop the others.</summary>
public static class ReportRunner
{
    public static ReportModel BuildModel(AnalysisSnapshot snapshot, string title) => new()
    {
        Run = snapshot.Run,
        Profile = snapshot.Profile,
        Recommendation = snapshot.Recommendation,
        Insights = snapshot.Insights,
        Taxonomy = snapshot.Taxonomy,
        Title = title,
    };

    /// <summary>Renderers whose <see cref="IReportRenderer.Format"/> is in <paramref name="formats"/> (all when empty).</summary>
    public static IReadOnlyList<IReportRenderer> Select(IEnumerable<IReportRenderer> renderers, IReadOnlyCollection<string>? formats) =>
        formats is null || formats.Count == 0
            ? [.. renderers]
            : [.. renderers.Where(r => formats.Contains(r.Format, StringComparer.OrdinalIgnoreCase))];

    public static async Task<ReportOutcome> RenderAsync(
        IReadOnlyList<IReportRenderer> renderers,
        AnalysisSnapshot snapshot,
        ReportOptions options,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        var model = BuildModel(snapshot, options.Title);
        var output = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(output);
        var target = new RenderTarget
        {
            OutputDirectory = output,
            BaseName = options.BaseName,
            Options = new Dictionary<string, string>(options.Options, StringComparer.OrdinalIgnoreCase),
        };

        var files = new List<string>();
        var failures = new List<ReportFailure>();
        foreach (var renderer in renderers)
        {
            try
            {
                var result = await renderer.RenderAsync(model, target, cancellationToken).ConfigureAwait(false);
                files.AddRange(result.Files);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Report renderer {Renderer} failed", renderer.Manifest.Id);
                failures.Add(new ReportFailure(renderer.Manifest.Id, ex.Message));
            }
        }

        return new ReportOutcome(files, failures);
    }
}
