using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Pipeline;
using PhotoClippy.Core.Services;

namespace PhotoClippy.Cli;

/// <summary>Analysis, opt-in recommendation and rendering workflows shared by the CLI and daemon.</summary>
internal static class Workflows
{
    public static async Task<AnalysisSnapshot> ScanAsync(AppRuntime runtime, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        var kinds = new HashSet<ComponentKind> { ComponentKind.Metadata };
        if (runtime.Options.Analysis.Frames)
        {
            kinds.Add(ComponentKind.Composition);
            kinds.Add(ComponentKind.Color);
        }

        await using var plugins = await runtime.CreatePluginsAsync(kinds, cancellationToken: cancellationToken).ConfigureAwait(false);
        var pipeline = new AnalysisPipeline(runtime.Options, new PipelineServices
        {
            Plugins = plugins,
            Store = runtime.Store,
            Taxonomy = runtime.Taxonomy,
            FrameSource = runtime.CreateFrameSource(),
            LoggerFactory = runtime.LoggerFactory,
            ToolVersion = AppRuntime.ToolVersion,
        });

        try
        {
            return await pipeline.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex.ParamName is null)
        {
            throw new CliException(ExitCodes.Usage, ex.Message, "Pass one or more folders, e.g. `photo-clippy run ~/Photos`, or set library.roots in the config.");
        }
        catch (InvalidOperationException ex) when (plugins.Metadata.Count == 0)
        {
            throw new CliException(ExitCodes.Usage, ex.Message, string.Join(" ", plugins.Notes));
        }
    }

    public static async Task<AnalysisSnapshot> LoadLatestAsync(AppRuntime runtime, CancellationToken cancellationToken) =>
        await runtime.Store.LoadLatestSnapshotAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new CliException(ExitCodes.NoData, "No stored analysis yet.", "Run `photo-clippy scan <folder>` (or `run`) first.");

    public static IReadOnlyList<LensSpec> ParseCandidates(IEnumerable<string> entries)
    {
        var candidates = RecommendationRunner.ParseCandidates(entries, out var errors);
        if (errors.Count > 0)
        {
            throw new CliException(ExitCodes.Usage, $"Could not parse candidate lens: {string.Join("; ", errors)}", "Use shorthand like 24/1.4, 14-24/2.8, 24-70mm f/2.8, 100-400/4.5-5.6, optionally with 1060g $2800 \"label\".");
        }

        return candidates;
    }

    public static async Task<AnalysisSnapshot> RecommendAsync(AppRuntime runtime, AnalysisSnapshot snapshot, IReadOnlyList<LensSpec> candidates, CancellationToken cancellationToken, RecommendationOptions? options = null)
    {
        await using var plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind> { ComponentKind.Recommendation }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var recommender = plugins.Recommender
            ?? throw new CliException(ExitCodes.Usage, $"Recommender '{runtime.Options.Plugins.Recommender}' is not available. {string.Join(" ", plugins.Notes)}", "Check `photo-clippy plugins`.");

        AnalysisSnapshot updated;
        try
        {
            updated = await RecommendationRunner.RunAsync(snapshot, recommender, candidates, options ?? runtime.Options.Recommendation, runtime.Options.Analysis.HighIsoThreshold, cancellationToken).ConfigureAwait(false);
        }
        catch (TargetNotFoundException ex)
        {
            throw new CliException(ExitCodes.Usage, ex.Message, "Omit --body to use the auto-detected body; `photo-clippy summary` lists the bodies in your library.");
        }

        updated = updated with
        {
            Run = updated.Run with
            {
                Plugins = [.. updated.Run.Plugins.Where(p => p.Kind != ComponentKind.Recommendation), recommender.Manifest],
            },
        };
        await runtime.Store.SaveSnapshotAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public static async Task<ReportOutcome> RenderAsync(AppRuntime runtime, AnalysisSnapshot snapshot, IReadOnlyCollection<string>? formats, CancellationToken cancellationToken)
    {
        await using var plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind> { ComponentKind.Report }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var renderers = ReportRunner.Select(plugins.Reports, formats);
        if (renderers.Count == 0)
        {
            var available = string.Join(", ", plugins.Reports.Select(r => r.Format));
            throw new CliException(ExitCodes.Usage, formats is { Count: > 0 }
                ? $"No report renderer for format(s) {string.Join(", ", formats)}. Available: {(available.Length == 0 ? "none" : available)}."
                : $"No report renderer is available. {string.Join(" ", plugins.Notes)}");
        }

        var withRenderers = snapshot with
        {
            Run = snapshot.Run with
            {
                Plugins = [.. snapshot.Run.Plugins.Where(p => p.Kind != ComponentKind.Report), .. renderers.Select(r => r.Manifest)],
            },
        };
        return await ReportRunner.RenderAsync(renderers, withRenderers, runtime.Options.Report, runtime.CreateLogger("Report"), cancellationToken).ConfigureAwait(false);
    }
}
