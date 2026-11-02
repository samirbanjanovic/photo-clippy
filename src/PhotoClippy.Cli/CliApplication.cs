using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli;

/// <summary>Command tree, shared options and the central error/exit-code handling.</summary>
internal static partial class CliApplication
{
    private static readonly Option<string?> ConfigOption = new("--config") { Description = "Config file (default: ./photo-clippy.json, then <home>/photo-clippy.json).", Recursive = true };
    private static readonly Option<string?> HomeOption = new("--home") { Description = "Data home for the cache, models and state (env PHOTO_CLIPPY_HOME).", Recursive = true };
    private static readonly Option<bool> JsonOption = new("--json") { Description = "Machine-readable NDJSON on stdout ({\"type\",\"data\"} per line).", Recursive = true };
    private static readonly Option<bool> VerboseOption = new("--verbose", "-v") { Description = "Debug logging on stderr.", Recursive = true };
    private static readonly Option<bool> QuietOption = new("--quiet", "-q") { Description = "Only errors.", Recursive = true };

    // Library / analysis
    private static readonly Argument<string[]> PathsArgument = new("paths") { Description = "Photo folders (or files) to analyse recursively. Defaults to library.roots from the config.", Arity = ArgumentArity.ZeroOrMore };
    private static readonly Option<string[]> IgnoreOption = new("--ignore") { Description = "Glob(s) to skip, e.g. '**/exports/**'.", AllowMultipleArgumentsPerToken = true };
    private static readonly Option<string?> ScopeOption = CreateScopeOption();
    private static readonly Option<bool> NoFramesOption = new("--no-frames") { Description = "Metadata only: skip pixel analysis (composition, colour, subject recognition)." };
    private static readonly Option<DateTime?> SinceOption = new("--since") { Description = "Only captures on/after this date (yyyy-MM-dd)." };
    private static readonly Option<DateTime?> UntilOption = new("--until") { Description = "Only captures before this date (yyyy-MM-dd)." };
    private static readonly Option<bool> RetryFailedOption = new("--retry-failed") { Description = "Retry files that previously failed to decode." };
    private static readonly Option<int?> JobsOption = new("--jobs", "-j") { Description = "Parallel pixel-analysis workers (default: CPU count)." };

    // Recommendation
    private static readonly Option<string[]> CandidatesOption = new("--candidates", "-c") { Description = "Lenses to evaluate, e.g. \"24/1.4, 35/1.2\". `recommend` asks when omitted; `serve` uses these only as defaults for explicit recommendation requests.", AllowMultipleArgumentsPerToken = true };
    private static readonly Option<string?> BodyOption = new("--body") { Description = "Target body (name or key) when you own several; default: the one you use most recently." };
    private static readonly Option<string?> PresetOption = new("--preset") { Description = "Scoring preset: balanced (default), low-light, reach, versatility, portability, value." };
    private static readonly Option<double?> BudgetOption = new("--budget") { Description = "Budget in USD (only for candidates with a $price)." };
    private static readonly Option<double?> MaxWeightOption = new("--max-weight") { Description = "Maximum lens weight in grams (only for candidates with a weight)." };
    private static readonly Option<int?> TopOption = new("--top") { Description = "Number of ranked lenses to keep (default 8; 0 = all)." };
    private static readonly Option<bool> NoPromptOption = new("--no-prompt") { Description = "Never ask for candidates. An explicitly requested ranking without candidates suggests archetypes; this flag alone does not request a ranking." };

    // Report
    private static readonly Option<string?> OutOption = new("--out", "-o") { Description = "Report output folder (default: ./photo-report)." };
    private static readonly Option<string[]> FormatOption = new("--format", "-f") { Description = "Report format(s): html, json, csv (default: all installed).", AllowMultipleArgumentsPerToken = true };
    private static readonly Option<string?> TitleOption = new("--title") { Description = "Report title." };
    private static readonly Option<bool> OpenOption = new("--open") { Description = "Open the HTML report when done." };

    public static async Task<int> RunAsync(string[] args)
    {
        var root = new RootCommand("Analyse your photo library — gear, focal lengths, apertures, light, subjects, composition and colour — and rank the lens you should buy next.");
        foreach (var option in new Option[] { ConfigOption, HomeOption, JsonOption, VerboseOption, QuietOption })
        {
            root.Options.Add(option);
        }

        root.Subcommands.Add(BuildRunCommand());
        root.Subcommands.Add(BuildScanCommand());
        root.Subcommands.Add(BuildRecommendCommand());
        root.Subcommands.Add(BuildReportCommand());
        root.Subcommands.Add(BuildSummaryCommand());
        root.Subcommands.Add(BuildInspectCommand());
        root.Subcommands.Add(BuildServeCommand());
        root.Subcommands.Add(BuildPluginsCommand());
        root.Subcommands.Add(BuildModelsCommand());
        root.Subcommands.Add(BuildDoctorCommand());
        root.Subcommands.Add(BuildConfigCommand());
        root.Subcommands.Add(BuildCacheCommand());
        root.Subcommands.Add(BuildUsersCommand());

        var parse = root.Parse(args);
        return await parse.InvokeAsync().ConfigureAwait(false);
    }

    private static Option<string?> CreateScopeOption()
    {
        var option = new Option<string?>("--scope") { Description = "interchangeable (default: cameras with interchangeable lenses only) or all (include phones and compacts)." };
        option.AcceptOnlyFromAmong("interchangeable", "all");
        return option;
    }

    private static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }

    private static Option[] LibraryOptionSet => [IgnoreOption, ScopeOption, NoFramesOption, SinceOption, UntilOption, RetryFailedOption, JobsOption];

    private static Option[] RecommendationOptionSet => [CandidatesOption, BodyOption, PresetOption, BudgetOption, MaxWeightOption, TopOption, NoPromptOption];

    private static Option[] ReportOptionSet => [OutOption, FormatOption, TitleOption, OpenOption];

    private static GlobalSettings Globals(ParseResult parse) => new(
        parse.GetValue(ConfigOption),
        parse.GetValue(HomeOption),
        parse.GetValue(JsonOption),
        parse.GetValue(VerboseOption),
        parse.GetValue(QuietOption));

    private static string[] SplitList(string[]? values) =>
        values is null ? [] : [.. values.SelectMany(v => v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    private static void ApplyLibrary(ParseResult parse, AppOptions options)
    {
        var paths = parse.GetValue(PathsArgument);
        if (paths is { Length: > 0 })
        {
            options.Library.Roots = [.. paths];
        }

        if (parse.GetValue(IgnoreOption) is { Length: > 0 } ignore)
        {
            options.Library.Ignore.AddRange(ignore);
        }

        if (parse.GetValue(ScopeOption) is { } scope)
        {
            options.Analysis.Scope = scope;
        }

        if (parse.GetValue(NoFramesOption))
        {
            options.Analysis.Frames = false;
        }

        options.Analysis.Since = parse.GetValue(SinceOption) ?? options.Analysis.Since;
        options.Analysis.Until = parse.GetValue(UntilOption) ?? options.Analysis.Until;
        options.Analysis.RetryFailed |= parse.GetValue(RetryFailedOption);
        if (parse.GetValue(JobsOption) is { } jobs)
        {
            options.Analysis.CpuConcurrency = Math.Max(1, jobs);
        }
    }

    private static void ApplyRecommendation(ParseResult parse, AppOptions options)
    {
        var r = options.Recommendation;
        if (parse.GetValue(CandidatesOption) is { Length: > 0 } candidates)
        {
            r.Candidates = [.. candidates];
        }

        r.Body = parse.GetValue(BodyOption) ?? r.Body;
        r.Preset = parse.GetValue(PresetOption) ?? r.Preset;
        r.BudgetUsd = parse.GetValue(BudgetOption) ?? r.BudgetUsd;
        r.MaxWeightG = parse.GetValue(MaxWeightOption) ?? r.MaxWeightG;
        r.TopN = parse.GetValue(TopOption) ?? r.TopN;
        if (parse.GetValue(NoPromptOption))
        {
            r.Prompt = false;
        }
    }

    private static void ApplyReport(ParseResult parse, AppOptions options)
    {
        options.Report.Output = parse.GetValue(OutOption) ?? options.Report.Output;
        options.Report.Title = parse.GetValue(TitleOption) ?? options.Report.Title;
    }

    private static bool HasRecommendationOverrides(ParseResult parse) =>
        parse.GetValue(CandidatesOption) is { Length: > 0 } || parse.GetValue(BodyOption) is not null || parse.GetValue(PresetOption) is not null
        || parse.GetValue(BudgetOption) is not null || parse.GetValue(MaxWeightOption) is not null || parse.GetValue(TopOption) is not null;

    /// <summary>Creates the runtime, runs <paramref name="body"/>, and maps failures to exit codes and friendly messages.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Mirrors the (parse, ct) action signature; the trailing parameters are optional hooks.")]
    private static async Task<int> ExecuteAsync(
        ParseResult parse,
        CancellationToken cancellationToken,
        Func<AppRuntime, Ui, CancellationToken, Task<int>> body,
        Action<AppOptions>? configure = null,
        Microsoft.Extensions.Logging.LogLevel? logLevel = null)
    {
        var globals = Globals(parse);
        var ui = new Ui(globals.Json, globals.Quiet);
        try
        {
            using var runtime = AppRuntime.Create(globals, configure, logLevel);
            return await body(runtime, ui, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            ui.Error(ex.Message, ex.Hint);
            return ex.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ui.Error("Cancelled. Finished work is cached; the next run resumes from there.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException)
        {
            ui.Error(ex.Message, ex is JsonException ? "Check the config file and PHOTO_CLIPPY_* variables (`photo-clippy config show`)." : null);
            return ExitCodes.Usage;
        }
        catch (Exception ex)
        {
            ui.Error(ex.Message, globals.Verbose ? null : "Re-run with --verbose for details.");
            if (globals.Verbose)
            {
                Console.Error.WriteLine(ex);
            }

            return ExitCodes.Error;
        }
    }

    private static void OpenFile(Ui ui, string path)
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]) { UseShellExecute = false };
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ui.Warn($"Could not open {path}: {ex.Message}");
        }
    }
}
