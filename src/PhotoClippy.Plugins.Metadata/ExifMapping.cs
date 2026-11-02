using System.Globalization;
using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>Value conversions shared by the native and ExifTool extractors.</summary>
internal static class ExifMapping
{
    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy:MM:dd HH:mm", "yyyy-MM-dd HH:mm", "yyyy:MM:dd", "yyyy-MM-dd",
    ];

    /// <summary>Trims, removes NULs/control characters and collapses runs of whitespace; empty → null.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (c == '\0')
            {
                break;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static string? CleanSerial(string? value)
    {
        var s = Clean(value);
        if (s is null)
        {
            return null;
        }

        if (s.StartsWith("NO=", StringComparison.OrdinalIgnoreCase))
        {
            s = Clean(s[3..]);
        }

        if (s is null || IsPlaceholder(s))
        {
            return null;
        }

        return s;
    }

    /// <summary>True for values that carry no information ("0", "0000", "-----", "N/A", "Unknown").</summary>
    public static bool IsPlaceholder(string s)
    {
        var allFiller = true;
        foreach (var c in s)
        {
            if (c is not ('0' or '-' or ' ' or '.' or '_' or '*'))
            {
                allFiller = false;
                break;
            }
        }

        return allFiller
               || s.Equals("N/A", StringComparison.OrdinalIgnoreCase)
               || s.Equals("none", StringComparison.OrdinalIgnoreCase)
               || s.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for values that only state a focal length or range, e.g. "18.0-55.0mm" or "50 mm".</summary>
    public static bool IsFocalRangeOnly(string value)
    {
        var s = value.AsSpan().Trim();
        if (!s.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        s = s[..^2];
        var sawDigit = false;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
            }
            else if (c is not ('.' or '-' or ' '))
            {
                return false;
            }
        }

        return sawDigit;
    }

    /// <summary>
    /// Accepts decoded lens names only: rejects empty, placeholder and purely numeric values (undecoded lens ids).
    /// </summary>
    public static string? UsefulLensName(string? value)
    {
        var s = Clean(value);
        if (s is null || IsPlaceholder(s))
        {
            return null;
        }

        var hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                break;
            }
        }

        if (!hasLetter)
        {
            return null;
        }

        return s.StartsWith("(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal) ? null : s;
    }

    public static string? ExposureProgramName(int value) => value switch
    {
        1 => "manual",
        2 => "program",
        3 => "aperture-priority",
        4 => "shutter-priority",
        5 => "creative",
        6 => "action",
        7 => "portrait",
        8 => "landscape",
        9 => "bulb",
        _ => null,
    };

    public static string? MeteringModeName(int value) => value switch
    {
        1 => "average",
        2 => "center-weighted",
        3 => "spot",
        4 => "multi-spot",
        5 => "multi-segment",
        6 => "partial",
        255 => "other",
        _ => null,
    };

    public static string? WhiteBalanceName(int value) => value switch
    {
        0 => "auto",
        1 => "manual",
        _ => null,
    };

    /// <summary>Returns the value when finite and strictly positive.</summary>
    public static double? Positive(double? value) =>
        value is { } v && double.IsFinite(v) && v > 0 ? v : null;

    public static double? Finite(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;

    /// <summary>ApertureValue (APEX Av) → f-number: N = 2^(Av/2).</summary>
    public static double? FNumberFromApex(double? av)
    {
        if (av is not { } v || !double.IsFinite(v) || v < -2 || v > 32)
        {
            return null;
        }

        return Math.Round(Math.Pow(2, v / 2), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>ShutterSpeedValue (APEX Tv) → seconds: t = 2^-Tv.</summary>
    public static double? ExposureFromApex(double? tv)
    {
        if (tv is not { } v || !double.IsFinite(v) || v < -20 || v > 30)
        {
            return null;
        }

        // APEX values are stored rounded (Tv 12.97 = 1/8023 s), so snap to the nominal marked speed when within 3%;
        // like exiftool, speeds up to 1/4 s are 1/x and slower ones are decimal seconds.
        var t = Math.Pow(2, -v);
        foreach (var nominal in NominalShutterSpeeds)
        {
            if (Math.Abs(t - nominal) <= nominal * 0.03)
            {
                return nominal;
            }
        }

        if (t <= 0.25001)
        {
            var inverse = Math.Round(1 / t, MidpointRounding.AwayFromZero);
            return inverse > 0 ? 1 / inverse : t;
        }

        return Math.Round(t, 1, MidpointRounding.AwayFromZero);
    }

    private static readonly double[] NominalShutterSpeeds =
    [
        .. new double[]
        {
            32000, 25600, 20000, 16000, 12800, 10000, 8000, 6400, 6000, 5000, 4000, 3200, 3000, 2500, 2000, 1600, 1500,
            1250, 1000, 800, 750, 640, 500, 400, 350, 320, 250, 200, 180, 160, 125, 100, 90, 80, 60, 50, 45, 40, 30, 25,
            20, 15, 13, 10, 8, 6, 5, 4,
        }.Select(x => 1 / x),
        0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1, 1.3, 1.5, 1.6, 2, 2.5, 3, 3.2, 4, 5, 6, 8, 10, 13, 15, 20, 25, 30, 60,
    ];

    public static int? ValidIso(double? value) =>
        value is { } v && double.IsFinite(v) && v >= 1 && v <= 10_000_000 ? (int)Math.Round(v) : null;

    public static int? ValidOrientation(double? value) =>
        value is { } v && v >= 1 && v <= 8 && Math.Abs(v - Math.Round(v)) < 1e-9 ? (int)v : null;

    public static int? ValidRating(double? value) =>
        value is { } v && double.IsFinite(v) && v >= -1 && v <= 5 ? (int)Math.Round(v) : null;

    public static int? ValidDimension(double? value) =>
        value is { } v && double.IsFinite(v) && v >= 1 && v <= 1_000_000 ? (int)v : null;

    public static double? SubjectDistance(double? meters) =>
        meters is { } v && double.IsFinite(v) && v > 0 && v < 1e6 ? v : null;

    /// <summary>FocalPlaneXResolution with FocalPlaneResolutionUnit (1/2 = inch, 3 = cm, 4 = mm, 5 = µm) → pixels per mm.</summary>
    public static double? FocalPlanePerMm(double? resolution, int? unit)
    {
        if (Positive(resolution) is not { } r)
        {
            return null;
        }

        return (unit ?? 2) switch
        {
            1 or 2 => r / 25.4,
            3 => r / 10,
            4 => r,
            5 => r * 1000,
            _ => null,
        };
    }

    /// <summary>
    /// Builds a lens specification. Zero/non-finite apertures are unknown (null); both focal ends are required
    /// because <see cref="LensSpecification"/> has non-nullable focal members.
    /// </summary>
    public static LensSpecification? LensSpec(double? minFocal, double? maxFocal, double? fAtMin, double? fAtMax)
    {
        if (Positive(minFocal) is not { } min || Positive(maxFocal) is not { } max)
        {
            return null;
        }

        var aMin = Positive(fAtMin);
        var aMax = Positive(fAtMax);
        return min <= max ? new LensSpecification(min, max, aMin, aMax) : new LensSpecification(max, min, aMax, aMin);
    }

    /// <summary>Parses "24 70 2.8 4" (ExifTool) or "24/1 70/1 28/10 40/10" (XMP aux:LensInfo).</summary>
    public static LensSpecification? ParseLensInfo(string? value)
    {
        var s = Clean(value);
        if (s is null)
        {
            return null;
        }

        var parts = s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        return LensSpec(ParseNumber(parts[0]), ParseNumber(parts[1]), ParseNumber(parts[2]), ParseNumber(parts[3]));
    }

    /// <summary>Parses a decimal or "numerator/denominator" token; x/0 and garbage → null.</summary>
    public static double? ParseNumber(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var slash = token.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            if (double.TryParse(token.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.TryParse(token.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d != 0)
            {
                return Finite(n / d);
            }

            return null;
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Finite(v) : null;
    }

    /// <summary>Parses EXIF/XMP style date-times ("2024:05:01 13:45:10") into an unspecified-kind local time.</summary>
    public static DateTime? ParseDateTime(string? value)
    {
        var s = Clean(value);
        if (s is null || s.StartsWith("0000", StringComparison.Ordinal))
        {
            return null;
        }

        if (TryParseDate(s, out var dt))
        {
            return dt;
        }

        if (s.Length > 19 && TryParseDate(s[..19], out dt))
        {
            return dt;
        }

        if (s.Length > 16 && TryParseDate(s[..16], out dt))
        {
            return dt;
        }

        return null;

        static bool TryParseDate(string text, out DateTime result)
        {
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
                && result.Year > 1800)
            {
                result = DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
                return true;
            }

            return false;
        }
    }

    /// <summary>Returns an EXIF OffsetTime ("+02:00", "-05:30" or "Z") or null when malformed.</summary>
    public static string? ParseOffset(string? value)
    {
        var s = Clean(value);
        if (s is null)
        {
            return null;
        }

        if (s == "Z")
        {
            return "+00:00";
        }

        if (s.Length == 6 && (s[0] == '+' || s[0] == '-') && char.IsAsciiDigit(s[1]) && char.IsAsciiDigit(s[2])
            && s[3] == ':' && char.IsAsciiDigit(s[4]) && char.IsAsciiDigit(s[5]))
        {
            return s;
        }

        return null;
    }

    public static string? ParseSubSec(string? value)
    {
        var s = Clean(value);
        if (s is null)
        {
            return null;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return s;
    }

    /// <summary>Extracts an ISO value from a maker-note description such as "ISO 400", "400" or "Auto ISO 1600".</summary>
    public static int? IsoFromDescription(string? description)
    {
        var s = Clean(description);
        if (s is null)
        {
            return null;
        }

        int start = -1, end = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                start = i;
                end = i;
                while (end + 1 < s.Length && char.IsAsciiDigit(s[end + 1]))
                {
                    end++;
                }

                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var numericOnly = start == 0 && end == s.Length - 1;
        if (!numericOnly && !s.Contains("ISO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(s.AsSpan(start, end - start + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var iso)
            ? ValidIso(iso)
            : null;
    }

    public static bool ValidGps(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180
        && !(lat == 0 && lon == 0);
}
