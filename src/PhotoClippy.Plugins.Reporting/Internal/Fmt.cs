using System.Globalization;
using System.Text;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Invariant formatting and HTML/XML-safe encoding helpers.</summary>
internal static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Encodes text for HTML content, attributes and inline SVG (the output is also valid XML).</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var clean = true;
        foreach (var c in s)
        {
            if (c is '<' or '>' or '&' or '"' or '\'' or '\uFFFE' or '\uFFFF' || (c < 0x20 && c is not ('\t' or '\n' or '\r')) || char.IsSurrogate(c))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 16);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                case '\t' or '\n' or '\r': sb.Append(c); break;
                case < (char)0x20 or '\uFFFE' or '\uFFFF': break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else
                    {
                        sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    public static StringBuilder AppendEsc(this StringBuilder sb, string? s) => sb.Append(Esc(s));

    /// <summary>Appends a coordinate rounded to one decimal.</summary>
    public static StringBuilder N(this StringBuilder sb, double v)
    {
        if (!double.IsFinite(v))
        {
            return sb.Append('0');
        }

        var r = Math.Round(v, 1);
        if (r == 0)
        {
            return sb.Append('0');
        }

        if (r == Math.Floor(r) && Math.Abs(r) < 1e9)
        {
            return sb.Append((long)r);
        }

        return sb.Append(r.ToString("0.#", Inv));
    }

    public static string Int(double v) => double.IsFinite(v) ? Math.Round(v).ToString("N0", Inv) : "–";

    public static string Num(double v, string format = "0.##") => double.IsFinite(v) ? v.ToString(format, Inv) : "–";

    public static string Pct(double share)
    {
        if (!double.IsFinite(share))
        {
            return "–";
        }

        var v = share * 100;
        if (v == 0)
        {
            return "0%";
        }

        if (Math.Abs(v) < 0.1)
        {
            return v > 0 ? "<0.1%" : "−<0.1%";
        }

        return (Math.Abs(v) < 9.95 ? v.ToString("0.#", Inv) : v.ToString("0", Inv)) + "%";
    }

    public static string Focal(double mm) => double.IsFinite(mm) ? mm.ToString(mm >= 100 ? "0" : "0.#", Inv) + " mm" : "–";

    public static string FocalRange(double? min, double? max)
    {
        if (min is not { } a)
        {
            return max is { } b ? Focal(b) : "–";
        }

        if (max is not { } c || c <= a + 0.5)
        {
            return Focal(a);
        }

        return a.ToString(a >= 100 ? "0" : "0.#", Inv) + "–" + Focal(c);
    }

    public static string FNum(double f) => double.IsFinite(f) ? "f/" + f.ToString(f >= 10 ? "0" : "0.#", Inv) : "–";

    public static string ApertureRange(double? wide, double? tele)
    {
        if (wide is not { } w)
        {
            return "–";
        }

        return tele is { } t && Math.Abs(t - w) > 0.05 ? FNum(w) + "–" + t.ToString("0.#", Inv) : FNum(w);
    }

    public static string Iso(double iso) => double.IsFinite(iso) ? "ISO " + Math.Round(iso).ToString("N0", Inv) : "–";

    public static string Ev(double ev) => double.IsFinite(ev) ? "EV " + ev.ToString("0.#", Inv) : "–";

    public static string Seconds(double s)
    {
        if (!double.IsFinite(s))
        {
            return "–";
        }

        if (s < 1)
        {
            return (s * 1000).ToString("0", Inv) + " ms";
        }

        if (s < 120)
        {
            return s.ToString(s < 10 ? "0.0" : "0", Inv) + " s";
        }

        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1
            ? string.Create(Inv, $"{(int)ts.TotalHours} h {ts.Minutes} min")
            : string.Create(Inv, $"{(int)ts.TotalMinutes} min {ts.Seconds} s");
    }

    public static string Bytes(long bytes)
    {
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < ByteUnits.Length - 1)
        {
            v /= 1000;
            u++;
        }

        return v.ToString(u == 0 || v >= 100 ? "0" : "0.#", Inv) + " " + ByteUnits[u];
    }

    public static string Month(DateTime d) => MonthNames[d.Month - 1] + " " + d.Year.ToString(Inv);

    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd", Inv);

    public static string Utc(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", Inv) + " UTC";

    public static string DateRange(DateTime? first, DateTime? last)
    {
        if (first is { } f && last is { } l)
        {
            return f.Year == l.Year && f.Month == l.Month ? Month(f) : Month(f) + " – " + Month(l);
        }

        return first is { } a ? "from " + Month(a) : last is { } b ? "until " + Month(b) : "dates unknown";
    }

    /// <summary>Parses "yyyy-MM" (or "yyyy-MM-…") month labels.</summary>
    public static bool TryYearMonth(string? label, out int year, out int month)
    {
        year = month = 0;
        return label is { Length: >= 7 } && label[4] == '-'
            && int.TryParse(label.AsSpan(0, 4), NumberStyles.None, Inv, out year)
            && int.TryParse(label.AsSpan(5, 2), NumberStyles.None, Inv, out month)
            && month is >= 1 and <= 12;
    }

    public static string MonthLabel(string label) => TryYearMonth(label, out var y, out var m) ? MonthNames[m - 1] + " " + y.ToString(Inv) : label;

    /// <summary>Rough text width for the system UI font stack.</summary>
    public static double TextWidth(ReadOnlySpan<char> s, double fontSize)
    {
        double w = 0;
        foreach (var c in s)
        {
            w += c switch
            {
                'i' or 'l' or 'j' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' or 'I' or ' ' => 0.28,
                'f' or 't' or 'r' or '(' or ')' or '/' or '-' or '1' => 0.36,
                'm' or 'w' or 'M' or 'W' or '%' or '@' => 0.86,
                >= '0' and <= '9' => 0.6,
                >= 'A' and <= 'Z' => 0.66,
                '·' or '•' => 0.3,
                '–' or '—' or '×' or '°' => 0.6,
                _ => 0.54,
            };
        }

        return w * fontSize;
    }

    public static double TextWidth(string? s, double fontSize) => s is null ? 0 : TextWidth(s.AsSpan(), fontSize);

    /// <summary>Truncates <paramref name="s"/> with an ellipsis so it fits <paramref name="maxWidth"/>.</summary>
    public static string Fit(string? s, double maxWidth, double fontSize)
    {
        if (string.IsNullOrEmpty(s) || TextWidth(s, fontSize) <= maxWidth)
        {
            return s ?? string.Empty;
        }

        if (maxWidth < fontSize * 1.5)
        {
            return string.Empty;
        }

        var target = maxWidth - (fontSize * 0.7);
        var len = s.Length;
        while (len > 1 && TextWidth(s.AsSpan(0, len), fontSize) > target)
        {
            len--;
        }

        return s[..len].TrimEnd() + "…";
    }

    public static string Two(int n) => n.ToString("00", Inv);
}
