using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Pipeline;
using Spectre.Console;

namespace PhotoClippy.Cli;

/// <summary>
/// Console output in two modes: human (Spectre, Nordic palette) or <c>--json</c> (NDJSON envelopes on stdout,
/// human text suppressed). Logs always go to stderr.
/// </summary>
internal sealed class Ui
{
    public static readonly Color Fjord = new(74, 108, 140);
    public static readonly Color Sage = new(124, 140, 110);
    public static readonly Color Sand = new(196, 176, 140);
    public static readonly Color Clay = new(176, 104, 80);
    public static readonly Color Slate = new(110, 118, 126);

    private static readonly Lock JsonLock = new();
    private readonly Stream? _stdout;

    public Ui(bool json, bool quiet)
    {
        Json = json;
        Quiet = quiet;
        Console = json
            ? AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) })
            : AnsiConsole.Console;
        _stdout = json ? System.Console.OpenStandardOutput() : null;
    }

    public bool Json { get; }
    public bool Quiet { get; }
    public IAnsiConsole Console { get; }

    public bool CanPrompt => !Json && !System.Console.IsInputRedirected && Console.Profile.Capabilities.Interactive;

    public bool ShowHuman => !Json && !Quiet;

    /// <summary>Writes one NDJSON envelope <c>{"type":…,"data":…}</c> to stdout (json mode only).</summary>
    public void Emit(string type, object? data)
    {
        if (_stdout is null)
        {
            return;
        }

        lock (JsonLock)
        {
            using var writer = new Utf8JsonWriter(_stdout);
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, data, data?.GetType() ?? typeof(object), ContractJson.Options);
            writer.WriteEndObject();
            writer.Flush();
            _stdout.WriteByte((byte)'\n');
            _stdout.Flush();
        }
    }

    public void Heading(string text)
    {
        if (ShowHuman)
        {
            Console.WriteLine();
            Console.Write(new Rule($"[bold]{Markup.Escape(text)}[/]").LeftJustified().RuleStyle(new Style(Slate)));
        }
    }

    public void Info(string text)
    {
        if (ShowHuman)
        {
            Console.MarkupLine($"[{Slate.ToMarkup()}]{Markup.Escape(text)}[/]");
        }
    }

    public void Success(string text)
    {
        if (ShowHuman)
        {
            Console.MarkupLine($"[{Sage.ToMarkup()}]✓[/] {Markup.Escape(text)}");
        }
    }

    public void Warn(string text)
    {
        if (Json)
        {
            Emit("warning", new { message = text });
            return;
        }

        if (!Quiet)
        {
            Console.MarkupLine($"[{Sand.ToMarkup()}]![/] {Markup.Escape(text)}");
        }
    }

    public void Error(string text, string? hint = null)
    {
        if (Json)
        {
            Emit("error", new { message = text, hint });
            return;
        }

        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) });
        err.MarkupLine($"[{Clay.ToMarkup()} bold]error[/] {Markup.Escape(text)}");
        if (hint is not null)
        {
            err.MarkupLine($"[{Slate.ToMarkup()}]  hint: {Markup.Escape(hint)}[/]");
        }
    }

    public void Write(Spectre.Console.Rendering.IRenderable renderable)
    {
        if (ShowHuman)
        {
            Console.Write(renderable);
        }
    }

    /// <summary>Runs <paramref name="work"/> with live per-stage progress bars (human) or throttled progress events (json).</summary>
    public async Task<T> RunWithProgressAsync<T>(Func<IProgress<PipelineProgress>, Task<T>> work)
    {
        if (Json)
        {
            return await work(new JsonProgress(this)).ConfigureAwait(false);
        }

        if (Quiet || !Console.Profile.Capabilities.Interactive)
        {
            return await work(new LineProgress(this)).ConfigureAwait(false);
        }

        return await Console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new SpinnerColumn(Spinner.Known.Dots) { Style = new Style(Fjord) },
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn { CompletedStyle = new Style(Fjord), FinishedStyle = new Style(Sage), RemainingStyle = new Style(Color.Grey23) },
                new PercentageColumn { Style = new Style(Slate), CompletedStyle = new Style(Sage) },
                new RemainingTimeColumn { Style = new Style(Slate) })
            .StartAsync(ctx => work(new SpectreProgress(ctx)))
            .ConfigureAwait(false);
    }

    public static string StageName(string stage) => stage switch
    {
        PipelineStages.Discover => "Finding photos",
        PipelineStages.Metadata => "Reading metadata",
        PipelineStages.Normalize => "Grouping captures",
        PipelineStages.Frames => "Analysing pixels",
        PipelineStages.Profile => "Building profile",
        PipelineStages.Recommend => "Ranking lenses",
        PipelineStages.Render => "Rendering report",
        _ => stage,
    };

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Pct(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private sealed class SpectreProgress(ProgressContext context) : IProgress<PipelineProgress>
    {
        private readonly Dictionary<string, ProgressTask> _tasks = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();
        private ProgressTask? _current;

        public void Report(PipelineProgress value)
        {
            lock (_lock)
            {
                if (!_tasks.TryGetValue(value.Stage, out var task))
                {
                    if (_current is { IsFinished: false } previous)
                    {
                        previous.Value = previous.MaxValue;
                        previous.StopTask();
                    }

                    task = context.AddTask(StageName(value.Stage), autoStart: true);
                    _tasks[value.Stage] = task;
                    _current = task;
                }

                var description = new System.Text.StringBuilder(Markup.Escape(StageName(value.Stage)));
                if (value.Total is { } total)
                {
                    task.IsIndeterminate = false;
                    task.MaxValue = Math.Max(1, total);
                    task.Value = Math.Min(value.Completed, Math.Max(1, total));
                    description.Append(CultureInfo.InvariantCulture, $" [{Slate.ToMarkup()}]{N(value.Completed)}/{N(total)}");
                    if (value.Cached > 0)
                    {
                        description.Append(CultureInfo.InvariantCulture, $" · {N(value.Cached)} cached");
                    }

                    description.Append("[/]");
                    if (value.Completed >= total)
                    {
                        task.StopTask();
                    }
                }
                else
                {
                    task.IsIndeterminate = true;
                    description.Append(CultureInfo.InvariantCulture, $" [{Slate.ToMarkup()}]{N(value.Completed)}[/]");
                }

                if (value.Detail is not null)
                {
                    description.Append(CultureInfo.InvariantCulture, $" [{Slate.ToMarkup()}]· {Markup.Escape(value.Detail)}[/]");
                }

                task.Description = description.ToString();
            }
        }
    }

    /// <summary>Non-interactive terminals: one line per stage completion.</summary>
    private sealed class LineProgress(Ui ui) : IProgress<PipelineProgress>
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);

        public void Report(PipelineProgress value)
        {
            lock (_lock)
            {
                if (value.Total is { } total && value.Completed >= total && _done.Add(value.Stage))
                {
                    ui.Info($"{StageName(value.Stage)}: {N(value.Completed)}{(value.Cached > 0 ? $" ({N(value.Cached)} cached)" : string.Empty)}{(value.Detail is null ? string.Empty : $" · {value.Detail}")}");
                }
            }
        }
    }

    private sealed class JsonProgress(Ui ui) : IProgress<PipelineProgress>
    {
        private readonly Lock _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string? _stage;
        private long _last = long.MinValue;

        public void Report(PipelineProgress value)
        {
            lock (_lock)
            {
                var now = _clock.ElapsedMilliseconds;
                var final = value.Total is { } total && value.Completed >= total;
                if (value.Stage == _stage && !final && now - _last < 500)
                {
                    return;
                }

                _stage = value.Stage;
                _last = now;
                ui.Emit("progress", value);
            }
        }
    }
}
