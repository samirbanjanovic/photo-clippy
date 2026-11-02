using System.Globalization;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Services;
using Spectre.Console;

namespace PhotoClippy.Cli;

/// <summary>
/// Asks which lenses to evaluate, after the scan, with the detected body, owned lenses and focal peaks on screen.
/// Empty input lets the recommender propose archetypes that fill the gaps it finds.
/// </summary>
internal static class CandidatePrompt
{
    public static IReadOnlyList<LensSpec> Ask(Ui ui, AnalysisSnapshot snapshot, string? body)
    {
        var target = RecommendationRunner.ResolveTarget(snapshot, body);
        if (target is null)
        {
            ui.Warn("No interchangeable-lens body found in the library, so there is nothing to recommend lenses for.");
            return [];
        }

        var muted = Ui.Slate.ToMarkup();
        ui.Heading("Which lenses should I evaluate?");
        ui.Console.MarkupLine($"[{muted}]Body[/]        {Markup.Escape(target.BodyDisplay ?? target.BodyKey ?? "?")} [{muted}]· {Markup.Escape(SensorFormats.Display(target.Format))}{(snapshot.Systems.Count > 1 ? " · change with --body" : string.Empty)}[/]");
        if (target.BodyKey is { } key && snapshot.OwnedByBody.TryGetValue(key, out var owned) && owned.Count > 0)
        {
            ui.Console.MarkupLine($"[{muted}]You own[/]     {Markup.Escape(string.Join(", ", owned.Take(6).Select(o => o.Lens.Name)))}");
        }

        if (snapshot.Profile.FocalPeaks.Count > 0)
        {
            ui.Console.MarkupLine($"[{muted}]You shoot at[/] {Markup.Escape(string.Join(", ", snapshot.Profile.FocalPeaks.Take(4).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Focal35Mm:0}mm ({p.Share * 100:0}%)"))))} [{muted}](35mm eq.)[/]");
        }

        ui.Console.MarkupLine($"[{muted}]Shorthand: 24/1.4, 14-24/2.8, 24-70mm f/2.8, 100-400/4.5-5.6 — optional 1060g $2800 \"label\". Press Enter to let me suggest.[/]");
        while (true)
        {
            var text = ui.Console.Prompt(new TextPrompt<string>("[bold]Candidates[/]").AllowEmpty());
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            var specs = LensSpec.ParseList(text, out var errors);
            if (errors.Count == 0 && specs.Count > 0)
            {
                ui.Console.MarkupLine($"[{muted}]Evaluating {Markup.Escape(string.Join(", ", specs.Select(s => s.Name)))}[/]");
                return specs;
            }

            foreach (var error in errors)
            {
                ui.Console.MarkupLine($"[{Ui.Clay.ToMarkup()}]{Markup.Escape(error)}[/]");
            }
        }
    }
}
