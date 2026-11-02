using System.Globalization;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Services;
using Spectre.Console;

namespace PhotoClippy.Cli;

/// <summary>Human-readable terminal summaries of a snapshot and a recommendation.</summary>
internal static class Presenter
{
    private static string Muted(string text) => $"[{Ui.Slate.ToMarkup()}]{Markup.Escape(text)}[/]";

    private static string F(double value, string format = "0") => value.ToString(format, CultureInfo.InvariantCulture);

    public static void Summary(Ui ui, AnalysisSnapshot snapshot)
    {
        if (!ui.ShowHuman)
        {
            return;
        }

        var p = snapshot.Profile;
        var lib = p.Library;
        ui.Heading("Your photography");
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        var span = lib.FirstCapture is { } first && lib.LastCapture is { } last ? $" · {first:yyyy-MM} → {last:yyyy-MM}" : string.Empty;
        grid.AddRow(Muted("Library"), Markup.Escape($"{Ui.N(lib.Captures)} captures, {Ui.N(lib.CapturesInScope)} in scope ({lib.Scope}){span}"));
        if (lib.RawPlusJpeg > 0 || lib.FramesAnalyzed > 0)
        {
            grid.AddRow(string.Empty, Muted($"{Ui.N(lib.RawPlusJpeg)} RAW+JPEG pairs merged · {Ui.N(lib.FramesAnalyzed)} frames analysed"));
        }

        if (p.Target is { } target)
        {
            grid.AddRow(Muted("Target body"), Markup.Escape($"{target.BodyDisplay} · {SensorFormats.Display(target.Format)} ({F(target.CropFactor, "0.0#")}×)"));
        }

        if (p.FocalPeaks.Count > 0)
        {
            grid.AddRow(Muted("Focal peaks"), Markup.Escape(string.Join("  ·  ", p.FocalPeaks.Take(4).Select(pk => $"{F(pk.Focal35Mm)}mm {Ui.Pct(pk.Share)}"))) + Muted("  (35mm eq.)"));
        }

        var s = p.Signals;
        if (s.MedianFNumber is { } fn)
        {
            grid.AddRow(Muted("Aperture"), Markup.Escape($"median f/{F(fn, "0.0#")} · {Ui.Pct(s.ApertureCeilingShare)} at max aperture · {Ui.Pct(s.LightStarvedShare)} light-starved"));
        }

        if (s.MedianIso is { } iso)
        {
            grid.AddRow(Muted("Light"), Markup.Escape($"median ISO {F(iso)} · {Ui.Pct(s.HighIsoShare)} high ISO · {Ui.Pct(s.SlowShutterShare)} below 1/focal{(s.MedianEv100 is { } ev ? $" · median EV {F(ev, "0.#")}" : string.Empty)}"));
        }

        if (p.Distributions.GetValueOrDefault(DistributionIds.Genre) is { } genres)
        {
            var top = genres.Bins.Where(b => b.Key != "unclassified" && b.Count > 0).OrderByDescending(b => b.Count).Take(4).ToList();
            if (top.Count > 0)
            {
                grid.AddRow(Muted("Subjects"), Markup.Escape(string.Join("  ·  ", top.Select(b => $"{b.Label} {Ui.Pct(b.Share)}"))) + Muted($"  ({p.Quality.GenreSource})"));
            }
        }

        ui.Write(grid);
        LensTable(ui, p);
        Notes(ui, p.Quality.Notes);
    }

    private static void LensTable(Ui ui, ShootingProfile p)
    {
        var lenses = p.Lenses.Where(l => l.Captures > 0).Take(10).ToList();
        if (lenses.Count == 0)
        {
            return;
        }

        ui.Write(Text.NewLine);
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("Lens"))
            .AddColumn(new TableColumn("Frames").RightAligned())
            .AddColumn(new TableColumn("Share").RightAligned())
            .AddColumn(new TableColumn("Median focal").RightAligned())
            .AddColumn(new TableColumn("At max ap.").RightAligned())
            .AddColumn(new TableColumn("Mostly"));
        foreach (var l in lenses)
        {
            table.AddRow(
                Markup.Escape(l.Display) + (l.IsFixed ? Muted(" (fixed)") : string.Empty),
                Ui.N(l.Captures),
                Ui.Pct(l.Share),
                l.MedianFocal35 is { } mf ? $"{F(mf)}mm" : "–",
                Ui.Pct(l.WideOpenShare),
                Markup.Escape(l.TopGenre ?? "–"));
        }

        ui.Write(table);
    }

    public static void Recommendation(Ui ui, RecommendationSet? set)
    {
        if (!ui.ShowHuman || set is null)
        {
            return;
        }

        ui.Heading($"Next lens · {set.Target.BodyDisplay} · preset '{set.Preset}'");
        if (set.Ranked.Count == 0)
        {
            ui.Info("No candidates could be ranked.");
            Notes(ui, set.Notes);
            return;
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn(new TableColumn("Lens"))
            .AddColumn(new TableColumn("Score").RightAligned())
            .AddColumn(new TableColumn("P(best)").RightAligned())
            .AddColumn(new TableColumn("Why"));
        foreach (var r in set.Ranked)
        {
            var color = r.Rank == 1 ? Ui.Sage : r.AlreadyOwned ? Ui.Slate : Color.Default;
            var name = $"[{color.ToMarkup()}]{Markup.Escape(r.Lens.Name)}[/]"
                + (r.Lens.Source == LensSpecSource.Suggested ? Muted(" (archetype)") : string.Empty)
                + (r.AlreadyOwned ? Muted(" (owned)") : string.Empty);
            var why = r.Verdict ?? r.Evidence.FirstOrDefault() ?? string.Empty;
            if (r.RedundantWith is not null && r.Redundancy >= 0.5)
            {
                why += $" Overlaps {r.RedundantWith}.";
            }

            table.AddRow(
                F(r.Rank),
                name,
                $"{F(r.Score * 100)} {Muted($"{F(r.ScoreLow * 100)}–{F(r.ScoreHigh * 100)}")}",
                Ui.Pct(r.ProbabilityBest),
                Markup.Escape(why.Trim()));
        }

        ui.Write(table);
        if (set.Headline is not null)
        {
            ui.Console.MarkupLine($"[bold]{Markup.Escape(set.Headline)}[/]");
        }

        if (set.PresetWinners.Count > 0)
        {
            ui.Info("Winner by preset: " + string.Join("  ·  ", set.PresetWinners.Select(kv => $"{kv.Key} → {kv.Value}")));
        }

        Notes(ui, set.Notes);
    }

    public static void Insights(Ui ui, IEnumerable<Insight> insights, int max = 6)
    {
        if (!ui.ShowHuman)
        {
            return;
        }

        var picked = insights.Where(i => i.Tone != InsightTone.Neutral && i.Section != ReportSections.Method).Take(max).ToList();
        if (picked.Count == 0)
        {
            return;
        }

        ui.Heading("Worth knowing");
        foreach (var insight in picked)
        {
            var mark = insight.Tone == InsightTone.Positive ? $"[{Ui.Sage.ToMarkup()}]●[/]" : $"[{Ui.Sand.ToMarkup()}]●[/]";
            ui.Console.MarkupLine($"{mark} [bold]{Markup.Escape(insight.Title)}[/]  {Markup.Escape(insight.Text)}");
        }
    }

    public static void Files(Ui ui, ReportOutcome outcome)
    {
        foreach (var failure in outcome.Failures)
        {
            ui.Warn($"Renderer {failure.RendererId} failed: {failure.Error}");
        }

        if (!ui.ShowHuman || outcome.Files.Count == 0)
        {
            return;
        }

        ui.Heading("Report");
        foreach (var file in outcome.Files)
        {
            var uri = new Uri(Path.GetFullPath(file)).AbsoluteUri;
            ui.Console.MarkupLine($"  [link={uri}]{Markup.Escape(file)}[/]");
        }
    }

    public static void Notes(Ui ui, IEnumerable<string> notes)
    {
        if (!ui.ShowHuman)
        {
            return;
        }

        foreach (var note in notes.Distinct().Take(8))
        {
            ui.Console.MarkupLine(Muted("note: " + note));
        }
    }

    public static void Bodies(Ui ui, AnalysisSnapshot snapshot)
    {
        if (!ui.ShowHuman || snapshot.Systems.Count == 0)
        {
            return;
        }

        ui.Heading("Bodies (selectable with --body)");
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
            .AddColumn("Body").AddColumn("Key").AddColumn("Format").AddColumn(new TableColumn("Lenses").RightAligned());
        foreach (var system in snapshot.Systems)
        {
            var lensCount = system.BodyKey is { } key && snapshot.OwnedByBody.TryGetValue(key, out var owned) ? owned.Count : 0;
            var isTarget = snapshot.Profile.Target?.BodyKey == system.BodyKey;
            table.AddRow(
                Markup.Escape(system.BodyDisplay ?? system.BodyKey ?? "?") + (isTarget ? $" [{Ui.Sage.ToMarkup()}](target)[/]" : string.Empty),
                Muted(system.BodyKey ?? string.Empty),
                Markup.Escape(SensorFormats.Display(system.Format)),
                F(lensCount));
        }

        ui.Write(table);
    }
}
