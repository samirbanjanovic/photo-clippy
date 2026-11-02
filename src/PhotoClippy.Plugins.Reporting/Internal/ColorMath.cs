using System.Globalization;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>OKLab/OKLCh to sRGB conversion for hue and tone swatches, plus hex validation.</summary>
internal static class ColorMath
{
    /// <summary>Returns a normalized "#rrggbb" or null when <paramref name="hex"/> is not a valid color.</summary>
    public static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length == 8)
        {
            s = s[..6];
        }

        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return "#" + s.ToLowerInvariant();
    }

    public static (double R, double G, double B) ParseHex(string normalized)
    {
        var v = int.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    /// <summary>OKLab lightness (0..1) of an sRGB color.</summary>
    public static double Lightness(string normalized) => OkLab(normalized).L;

    /// <summary>OKLCh lightness, chroma and hue (degrees) of an sRGB color.</summary>
    public static (double L, double C, double H) OkLchOf(string normalized)
    {
        var (l, a, b) = OkLab(normalized);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return (l, Math.Sqrt((a * a) + (b * b)), h < 0 ? h + 360 : h);
    }

    private static (double L, double A, double B) OkLab(string normalized)
    {
        var (r, g, b) = ParseHex(normalized);
        double lr = ToLinear(r), lg = ToLinear(g), lb = ToLinear(b);
        var l = Math.Cbrt((0.4122214708 * lr) + (0.5363325363 * lg) + (0.0514459929 * lb));
        var m = Math.Cbrt((0.2119034982 * lr) + (0.6806995451 * lg) + (0.1073969566 * lb));
        var s = Math.Cbrt((0.0883024619 * lr) + (0.2817188376 * lg) + (0.6299787005 * lb));
        return ((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>OKLCh (hue in degrees) to sRGB hex; chroma is reduced until the color is in gamut.</summary>
    public static string OkLch(double lightness, double chroma, double hueDegrees)
    {
        var h = hueDegrees * Math.PI / 180;
        for (var c = chroma; c >= 0; c -= 0.005)
        {
            var (r, g, b) = OkLabToLinear(lightness, c * Math.Cos(h), c * Math.Sin(h));
            if (r is >= -0.0005 and <= 1.0005 && g is >= -0.0005 and <= 1.0005 && b is >= -0.0005 and <= 1.0005)
            {
                return Hex(r, g, b);
            }
        }

        var (r0, g0, b0) = OkLabToLinear(lightness, 0, 0);
        return Hex(r0, g0, b0);
    }

    /// <summary>Neutral gray with the given OKLab lightness.</summary>
    public static string Gray(double lightness)
    {
        var l = Math.Clamp(lightness, 0, 1);
        var lin = l * l * l;
        return Hex(lin, lin, lin);
    }

    private static (double R, double G, double B) OkLabToLinear(double L, double a, double b)
    {
        var l = L + (0.3963377774 * a) + (0.2158037573 * b);
        var m = L - (0.1055613458 * a) - (0.0638541728 * b);
        var s = L - (0.0894841775 * a) - (1.2914855480 * b);
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (
            (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
            (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
            (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double ToGamma(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
    }

    private static string Hex(double r, double g, double b)
    {
        static int To8(double v) => (int)Math.Round(ToGamma(v) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{To8(r):x2}{To8(g):x2}{To8(b):x2}");
    }
}
