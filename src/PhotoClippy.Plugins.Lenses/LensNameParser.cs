using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoClippy.Plugins.Lenses;

/// <summary>Reads focal range and maximum aperture from a lens model name such as <c>XF18-55mmF2.8-4 R LM OIS</c> or <c>EF 70-200mm f/2.8L</c>.</summary>
internal static partial class LensNameParser
{
    public readonly record struct Spec(double FocalMin, double FocalMax, double? ApertureWide, double? ApertureTele);

    public static bool TryParse(string? name, out Spec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var focal = FocalRegex().Match(name);
        if (!focal.Success || !TryNumber(focal.Groups["min"].Value, out var min) || min <= 0)
        {
            return false;
        }

        var max = focal.Groups["max"].Success && TryNumber(focal.Groups["max"].Value, out var m) ? m : min;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        double? wide = null;
        double? tele = null;
        var aperture = ApertureRegex().Match(name, focal.Index + focal.Length);
        if (!aperture.Success)
        {
            // A few names put the aperture first, e.g. "1:2.8 50mm".
            aperture = ApertureRegex().Match(name);
        }

        if (aperture.Success && TryNumber(aperture.Groups["min"].Value, out var a) && a is > 0.5 and < 64)
        {
            wide = a;
            tele = aperture.Groups["max"].Success && TryNumber(aperture.Groups["max"].Value, out var b) && b >= a && b < 64 ? b : a;
        }

        spec = new Spec(min, max, wide, tele);
        return true;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"(?<![\d.])(?<min>\d+(?:\.\d+)?)(?:\s*-\s*(?<max>\d+(?:\.\d+)?))?\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FocalRegex();

    // "f/2.8", "f2.8", "F1.4" (also glued: "55mmF2.8"), "1:2.8".
    [GeneratedRegex(@"(?:\bf\s*/\s*|(?<=[\s\dm])F\s*/?\s*|\bf(?=\d)|\b1\s*:\s*)(?<min>\d+(?:\.\d+)?)(?:\s*-\s*(?<max>\d+(?:\.\d+)?))?", RegexOptions.CultureInvariant)]
    private static partial Regex ApertureRegex();
}
