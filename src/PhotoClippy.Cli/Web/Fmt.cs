using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Cli.Web;

/// <summary>Display formatting shared by the web pages.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string N(long value) => value.ToString("N0", C);

    public static string Pct(double share) => (share * 100).ToString(share is > 0 and < 0.01 ? "0.0" : "0", C) + "%";

    public static string Num(double? value, string format = "0.#") => value is { } v ? v.ToString(format, C) : "—";

    public static string Focal(double? mm) => mm is { } v ? v.ToString("0", C) + " mm" : "—";

    public static string Aperture(double? f) => f is { } v ? "f/" + v.ToString("0.0#", C) : "—";

    public static string Iso(double? iso) => iso is { } v ? "ISO " + v.ToString("0", C) : "—";

    public static string Date(DateTime? value) => value is { } d ? d.ToString("d MMM yyyy", C) : "—";

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        >= 1L << 30 => (bytes.Value / (double)(1L << 30)).ToString("0.0", C) + " GB",
        >= 1L << 20 => (bytes.Value / (double)(1L << 20)).ToString("0", C) + " MB",
        >= 1L << 10 => (bytes.Value / (double)(1L << 10)).ToString("0", C) + " KB",
        _ => bytes.Value.ToString(C) + " B",
    };

    public static string Ago(DateTimeOffset? when)
    {
        if (when is not { } w)
        {
            return "never";
        }

        var span = DateTimeOffset.UtcNow - w;
        return span.TotalSeconds switch
        {
            < 0 => "just now",
            < 60 => "just now",
            < 3600 => $"{(int)span.TotalMinutes} min ago",
            < 86400 => $"{(int)span.TotalHours} h ago",
            _ => $"{(int)span.TotalDays} d ago",
        };
    }

    public static string Until(DateTimeOffset? when)
    {
        if (when is not { } w)
        {
            return "—";
        }

        var span = w - DateTimeOffset.UtcNow;
        return span.TotalSeconds switch
        {
            < 60 => "in a moment",
            < 3600 => $"in {(int)Math.Ceiling(span.TotalMinutes)} min",
            < 86400 => $"in {span.TotalHours.ToString("0.#", C)} h",
            _ => $"in {span.TotalDays.ToString("0.#", C)} d",
        };
    }

    public static string Stage(string? stage) => Ui.StageName(stage ?? "discover");

    /// <summary>Canonical shorthand that <see cref="LensSpec.ParseList"/> reads back, including weight, price and a custom label.</summary>
    public static string Shorthand(LensSpec lens)
    {
        var text = LensSpec.Shorthand(lens.FocalMinMm, lens.FocalMaxMm, lens.MaxApertureWide, lens.MaxApertureTele);
        if (lens.WeightG is { } g)
        {
            text += " " + g.ToString("0", C) + "g";
        }

        if (lens.PriceUsd is { } p)
        {
            text += " $" + p.ToString("0", C);
        }

        if (!string.IsNullOrWhiteSpace(lens.Name) && lens.Name != lens.ShortName && !lens.Name.Contains('"', StringComparison.Ordinal))
        {
            text += $" \"{lens.Name}\"";
        }

        return text;
    }

    /// <summary>Candidate text from the UI (one or more per line, comma separated) as request entries.</summary>
    public static IReadOnlyList<string> CandidateEntries(string? text) =>
        [.. (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
