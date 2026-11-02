using System.CommandLine;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Services;

namespace PhotoClippy.Cli;

internal static partial class CliApplication
{
    private static Command BuildRunCommand()
    {
        var command = new Command("run", "Scan and render a habits report. Lens ranking is opt-in: pass --candidates or other recommendation options, or use `recommend` later.");
        command.Arguments.Add(PathsArgument);
        AddOptions(command, LibraryOptionSet);
        AddOptions(command, RecommendationOptionSet);
        AddOptions(command, ReportOptionSet);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var recommend = HasRecommendationOverrides(parse);
            IReadOnlyList<LensSpec> candidates = recommend ? Workflows.ParseCandidates(runtime.Options.Recommendation.Candidates) : [];
            var snapshot = await ScanWithProgressAsync(runtime, ui, token).ConfigureAwait(false);
            if (snapshot.Profile.Library.CapturesInScope == 0)
            {
                return NoCaptures(ui, snapshot);
            }

            if (recommend)
            {
                candidates = MaybePrompt(runtime, ui, snapshot, candidates);
                snapshot = await Workflows.RecommendAsync(runtime, snapshot, candidates, token).ConfigureAwait(false);
                NoTargetNote(ui, snapshot);
            }

            Presenter.Summary(ui, snapshot);
            Presenter.Insights(ui, snapshot.Insights);
            Presenter.Recommendation(ui, snapshot.Recommendation);
            if (!recommend && ui.ShowHuman)
            {
                ui.Info("Compare candidate lenses later with `photo-clippy recommend` (no rescan).");
            }

            var outcome = await Workflows.RenderAsync(runtime, snapshot, Formats(parse), token).ConfigureAwait(false);
            Presenter.Files(ui, outcome);
            ui.Emit("result", new
            {
                snapshot.Run.RunId,
                snapshot.Profile.Library,
                snapshot.Profile.Target,
                snapshot.Recommendation,
                snapshot.Insights,
                outcome.Files,
                outcome.Failures,
            });
            OpenIfRequested(parse, ui, outcome);
            return outcome.Failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }, options =>
        {
            ApplyLibrary(parse, options);
            ApplyRecommendation(parse, options);
            ApplyReport(parse, options);
        }));
        return command;
    }

    private static Command BuildScanCommand()
    {
        var command = new Command("scan", "Scan and analyse the library (incremental: unchanged files come from the cache), without ranking or rendering.");
        command.Arguments.Add(PathsArgument);
        AddOptions(command, LibraryOptionSet);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var snapshot = await ScanWithProgressAsync(runtime, ui, token).ConfigureAwait(false);
            if (snapshot.Profile.Library.CapturesInScope == 0)
            {
                return NoCaptures(ui, snapshot);
            }

            Presenter.Summary(ui, snapshot);
            Presenter.Insights(ui, snapshot.Insights);
            if (ui.ShowHuman)
            {
                ui.Info("Next: `photo-clippy recommend -c \"24/1.4, 35/1.2\"` or `photo-clippy report`.");
            }

            ui.Emit("profile", new { snapshot.Run, snapshot.Profile, snapshot.Systems, snapshot.Owned, snapshot.Insights });
            return ExitCodes.Ok;
        }, options => ApplyLibrary(parse, options)));
        return command;
    }

    private static Command BuildRecommendCommand()
    {
        var command = new Command("recommend", "Rank candidate lenses against the last scan (no rescan). Without --candidates you are asked, or archetypes are suggested.");
        AddOptions(command, RecommendationOptionSet);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var candidates = Workflows.ParseCandidates(runtime.Options.Recommendation.Candidates);
            var snapshot = await Workflows.LoadLatestAsync(runtime, token).ConfigureAwait(false);
            candidates = MaybePrompt(runtime, ui, snapshot, candidates);
            snapshot = await Workflows.RecommendAsync(runtime, snapshot, candidates, token).ConfigureAwait(false);

            Presenter.Recommendation(ui, snapshot.Recommendation);
            NoTargetNote(ui, snapshot);
            if (ui.ShowHuman && snapshot.Recommendation is not null)
            {
                ui.Info("Render it with `photo-clippy report`.");
            }

            ui.Emit("recommendation", snapshot.Recommendation);
            return snapshot.Recommendation is null ? ExitCodes.NoData : ExitCodes.Ok;
        }, options => ApplyRecommendation(parse, options)));
        return command;
    }

    private static Command BuildReportCommand()
    {
        var command = new Command("report", "Render the report from the last scan. Recommendation options re-rank first (reusing the previous candidates unless --candidates is given).");
        AddOptions(command, ReportOptionSet);
        AddOptions(command, [CandidatesOption, BodyOption, PresetOption, BudgetOption, MaxWeightOption, TopOption]);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var snapshot = await Workflows.LoadLatestAsync(runtime, token).ConfigureAwait(false);
            if (HasRecommendationOverrides(parse))
            {
                var candidates = Workflows.ParseCandidates(runtime.Options.Recommendation.Candidates);
                if (candidates.Count == 0 && snapshot.Recommendation is { } previous)
                {
                    candidates = [.. previous.Ranked.Select(r => r.Lens)];
                }

                snapshot = await Workflows.RecommendAsync(runtime, snapshot, candidates, token).ConfigureAwait(false);
            }

            var outcome = await Workflows.RenderAsync(runtime, snapshot, Formats(parse), token).ConfigureAwait(false);
            if (snapshot.Recommendation is null && ui.ShowHuman)
            {
                ui.Info("The report has no lens ranking yet — add one with `photo-clippy recommend` or `report -c \"24/1.4, 35/1.2\"`.");
            }

            Presenter.Files(ui, outcome);
            ui.Emit("report", new { outcome.Files, outcome.Failures });
            OpenIfRequested(parse, ui, outcome);
            return outcome.Failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }, options =>
        {
            ApplyRecommendation(parse, options);
            ApplyReport(parse, options);
        }));
        return command;
    }

    private static Command BuildSummaryCommand()
    {
        var command = new Command("summary", "Show the last scan: library, bodies, lenses, focal peaks, insights and the latest ranking.");
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var snapshot = await Workflows.LoadLatestAsync(runtime, token).ConfigureAwait(false);
            if (ui.ShowHuman)
            {
                ui.Info($"Run {snapshot.Run.RunId} · {snapshot.Run.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · v{snapshot.Run.ToolVersion}");
            }

            Presenter.Summary(ui, snapshot);
            Presenter.Bodies(ui, snapshot);
            Presenter.Insights(ui, snapshot.Insights, max: 12);
            Presenter.Recommendation(ui, snapshot.Recommendation);
            ui.Emit("summary", new { snapshot.Run, snapshot.Profile, snapshot.Systems, snapshot.Owned, snapshot.Insights, snapshot.Recommendation });
            return ExitCodes.Ok;
        }));
        return command;
    }

    private static Task<AnalysisSnapshot> ScanWithProgressAsync(AppRuntime runtime, Ui ui, CancellationToken cancellationToken)
    {
        if (ui.ShowHuman)
        {
            var roots = runtime.Options.Library.Roots;
            ui.Info($"Scanning {(roots.Count == 0 ? "(no folders)" : string.Join(", ", roots))}{(runtime.Options.Analysis.Frames ? string.Empty : " · metadata only")}");
        }

        return ui.RunWithProgressAsync(progress => Workflows.ScanAsync(runtime, progress, cancellationToken));
    }

    private static IReadOnlyList<LensSpec> MaybePrompt(AppRuntime runtime, Ui ui, AnalysisSnapshot snapshot, IReadOnlyList<LensSpec> candidates)
    {
        if (candidates.Count > 0 || !runtime.Options.Recommendation.Prompt || !ui.CanPrompt || snapshot.Profile.Target is null)
        {
            return candidates;
        }

        return CandidatePrompt.Ask(ui, snapshot, runtime.Options.Recommendation.Body);
    }

    private static int NoCaptures(Ui ui, AnalysisSnapshot snapshot)
    {
        var library = snapshot.Profile.Library;
        var hint = library.FilesSeen == 0
            ? "No supported images were found. Check the folder path and --ignore globs."
            : library.Captures > 0 && library.Scope != "all"
                ? $"{Ui.N(library.Captures)} captures were found but none from an interchangeable-lens camera. Use --scope all to include phones and compacts."
                : "Files were found but none had usable metadata (see --verbose).";
        Presenter.Notes(ui, snapshot.Profile.Quality.Notes);
        ui.Emit("result", new { snapshot.Profile.Library, snapshot.Profile.Quality });
        throw new CliException(ExitCodes.NoData, $"Nothing to analyse ({Ui.N(library.FilesSeen)} files seen, {Ui.N(library.Captures)} captures).", hint);
    }

    private static void NoTargetNote(Ui ui, AnalysisSnapshot snapshot)
    {
        if (snapshot.Recommendation is null && snapshot.Profile.Target is null)
        {
            ui.Warn("No interchangeable-lens body was found, so there is nothing to recommend lenses for. The habits report is still complete.");
        }
    }

    private static string[]? Formats(ParseResult parse)
    {
        var formats = SplitList(parse.GetValue(FormatOption));
        return formats.Length == 0 ? null : formats;
    }

    private static void OpenIfRequested(ParseResult parse, Ui ui, ReportOutcome outcome)
    {
        if (!parse.GetValue(OpenOption))
        {
            return;
        }

        var html = outcome.Files.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ?? outcome.Files.FirstOrDefault();
        if (html is not null)
        {
            OpenFile(ui, Path.GetFullPath(html));
        }
    }
}
