using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhotoClippy.Contracts;

/// <summary>
/// Where a lens description came from. Analysis never consults a lens database; a catalog pick
/// (<see cref="ILensCatalog"/>) enters as user shorthand.
/// </summary>
public enum LensSpecSource
{
    /// <summary>EXIF LensSpecification tag of an owned lens.</summary>
    Exif,
    /// <summary>Inferred from what was observed in EXIF (focal span, widest f-number used).</summary>
    Observed,
    /// <summary>Supplied by the user at run time (CLI, prompt, config or API).</summary>
    User,
    /// <summary>Spec archetype proposed by a recommender from the user's own focal peaks.</summary>
    Suggested,
}

/// <summary>
/// Optical description of a lens in native focal lengths (not 35 mm equivalents). Only focal range and
/// maximum aperture are required; weight and price are optional and only used when supplied.
/// </summary>
public sealed partial record LensSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double FocalMinMm { get; init; }
    public required double FocalMaxMm { get; init; }
    public required double MaxApertureWide { get; init; }
    public required double MaxApertureTele { get; init; }
    public double? WeightG { get; init; }
    public double? PriceUsd { get; init; }
    public LensSpecSource Source { get; init; } = LensSpecSource.User;

    [JsonIgnore]
    public bool IsZoom => FocalMaxMm > FocalMinMm + 0.5;

    /// <summary>Maximum aperture at a native focal length, interpolated in log-focal space.</summary>
    public double MaxApertureAt(double focalMm)
    {
        if (!IsZoom || focalMm <= FocalMinMm)
        {
            return MaxApertureWide;
        }

        if (focalMm >= FocalMaxMm)
        {
            return MaxApertureTele;
        }

        var t = Math.Log(focalMm / FocalMinMm) / Math.Log(FocalMaxMm / FocalMinMm);
        return Math.Exp(Math.Log(MaxApertureWide) + (t * (Math.Log(MaxApertureTele) - Math.Log(MaxApertureWide))));
    }

    public string ShortName => IsZoom
        ? string.Create(CultureInfo.InvariantCulture, $"{FocalMinMm:0.#}-{FocalMaxMm:0.#}mm f/{MaxApertureWide:0.0#}{(Math.Abs(MaxApertureTele - MaxApertureWide) > 0.05 ? string.Create(CultureInfo.InvariantCulture, $"-{MaxApertureTele:0.0#}") : string.Empty)}")
        : string.Create(CultureInfo.InvariantCulture, $"{FocalMinMm:0.#}mm f/{MaxApertureWide:0.0#}");

    /// <summary>Canonical shorthand, e.g. <c>24-70/2.8</c> or <c>100-400/4.5-5.6</c>.</summary>
    public static string Shorthand(double focalMin, double focalMax, double apertureWide, double apertureTele)
    {
        var sb = new StringBuilder();
        sb.Append(focalMin.ToString("0.#", CultureInfo.InvariantCulture));
        if (focalMax > focalMin + 0.5)
        {
            sb.Append('-').Append(focalMax.ToString("0.#", CultureInfo.InvariantCulture));
        }

        sb.Append('/').Append(apertureWide.ToString("0.0#", CultureInfo.InvariantCulture));
        if (Math.Abs(apertureTele - apertureWide) > 0.05)
        {
            sb.Append('-').Append(apertureTele.ToString("0.0#", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses user shorthand: <c>&lt;focal&gt;[-&lt;focal&gt;][mm] [f]/&lt;aperture&gt;[-&lt;aperture&gt;] [&lt;n&gt;g] [$&lt;n&gt;] ["label"]</c>.
    /// Examples: <c>35/1.2</c>, <c>24-70mm f/2.8</c>, <c>100-400/4.5-5.6</c>, <c>35/1.2 1060g $2800 "Z 35 S"</c>.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out LensSpec? spec, [NotNullWhen(false)] out string? error)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty lens description";
            return false;
        }

        var input = text.Trim();
        string? label = null;
        var quote = LabelRegex().Match(input);
        if (quote.Success)
        {
            label = quote.Groups["label"].Value.Trim();
            input = input.Remove(quote.Index, quote.Length);
        }

        var m = SpecRegex().Match(input);
        if (!m.Success)
        {
            error = $"'{text.Trim()}' is not a lens like 35/1.4, 24-70/2.8 or 100-400/4.5-5.6";
            return false;
        }

        var fMin = Num(m.Groups["fmin"].Value);
        var fMax = m.Groups["fmax"].Success ? Num(m.Groups["fmax"].Value) : fMin;
        var aWide = Num(m.Groups["amin"].Value);
        var aTele = m.Groups["amax"].Success ? Num(m.Groups["amax"].Value) : aWide;
        if (fMin <= 0 || fMax < fMin || fMax > 2400 || aWide < 0.7 || aWide > 32 || aTele < aWide || aTele > 32)
        {
            error = $"'{text.Trim()}' has an implausible focal range or aperture";
            return false;
        }

        double? weight = null, price = null;
        var rest = input.Remove(m.Index, m.Length);
        foreach (var token in rest.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().ToLowerInvariant();
            if (t.EndsWith("kg", StringComparison.Ordinal) && double.TryParse(t[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var kg))
            {
                weight = kg * 1000;
            }
            else if (t.EndsWith('g') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
            {
                weight = g;
            }
            else if (t.StartsWith('$') && double.TryParse(t[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            {
                price = p;
            }
            else if (t.EndsWith("usd", StringComparison.Ordinal) && double.TryParse(t[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var p2))
            {
                price = p2;
            }
            else
            {
                error = $"unrecognized token '{token}' in '{text.Trim()}' (expected e.g. 1060g, 1.1kg, $2800 or \"label\")";
                return false;
            }
        }

        var id = Shorthand(fMin, fMax, aWide, aTele);
        var built = new LensSpec
        {
            Id = id,
            Name = id,
            FocalMinMm = fMin,
            FocalMaxMm = fMax,
            MaxApertureWide = aWide,
            MaxApertureTele = aTele,
            WeightG = weight,
            PriceUsd = price,
            Source = LensSpecSource.User,
        };
        spec = built with { Name = string.IsNullOrWhiteSpace(label) ? built.ShortName : label };
        error = null;
        return true;
    }

    /// <summary>Parses a comma/semicolon separated list; commas inside quoted labels are kept.</summary>
    public static IReadOnlyList<LensSpec> ParseList(string? text, out IReadOnlyList<string> errors)
    {
        var result = new List<LensSpec>();
        var errs = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var current = new StringBuilder();
            var inQuote = false;
            foreach (var ch in text)
            {
                if (ch == '"')
                {
                    inQuote = !inQuote;
                }

                if (!inQuote && (ch == ',' || ch == ';'))
                {
                    Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            Add(current.ToString());
        }

        errors = errs;
        return result;

        void Add(string part)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                return;
            }

            if (TryParse(part, out var spec, out var error))
            {
                if (!result.Exists(r => r.Id == spec.Id))
                {
                    result.Add(spec);
                }
            }
            else
            {
                errs.Add(error);
            }
        }
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex("\"(?<label>[^\"]*)\"")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"(?<fmin>\d+(?:\.\d+)?)\s*(?:-\s*(?<fmax>\d+(?:\.\d+)?))?\s*(?:mm)?\s*(?:,\s*)?(?:f\s*/?|/)\s*(?<amin>\d+(?:\.\d+)?)(?:\s*-\s*(?<amax>\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpecRegex();
}

/// <summary>The camera body recommendations are made for. Derived from EXIF; no body database.</summary>
public sealed record TargetSystem
{
    public string? BodyKey { get; init; }
    public string? BodyDisplay { get; init; }
    /// <summary>full-frame | aps-c | mft | medium-format | small-sensor, derived from the crop factor.</summary>
    public required string Format { get; init; }
    /// <summary>Median FocalLengthIn35mmFilm / FocalLength of the body's captures (1 when unknown).</summary>
    public required double CropFactor { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A lens the user owns, as observed in EXIF on the target body.</summary>
public sealed record OwnedLens
{
    public required LensSpec Lens { get; init; }
    public required int Captures { get; init; }
    public string? LensKey { get; init; }
}
