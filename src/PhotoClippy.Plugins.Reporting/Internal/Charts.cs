using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed record BarItem(string Label, double Count, double Share, string? DataKey = null, bool Accent = false, string? Title = null, string? Value = null);

internal sealed record LensRange(string DataKey, string Label, double Min, double Max);

internal sealed record CoverageGap(double From, double To, double Share, bool Edge);

internal sealed record ColumnOptions
{
    public Func<Bin, int, bool>? Accent { get; init; }
    public Func<string, string>? XLabel { get; init; }
    public bool Compact { get; init; }
    public bool ValueLabels { get; init; } = true;
    public int? MaxValueLabels { get; init; }
}

internal sealed record HeatmapOptions
{
    public bool RowNormalize { get; init; }
    public double MaxCellHeight { get; init; } = 26;
    public Func<string, string>? XLabel { get; init; }
    public Func<string, string>? YLabel { get; init; }
}

/// <summary>Server-side SVG chart library. Every mark carries a native &lt;title&gt; tooltip.</summary>
internal static partial class Charts
{
    public const double W12 = 1020;
    public const double W8 = 668;
    public const double W6 = 494;
    public const double W4 = 318;
    public const double WSmall = 231;

    private const double TickFont = 10.5;
    private const double LabelFont = 12;

    private static readonly double[] FocalTicks = [8, 10, 12, 14, 16, 20, 24, 28, 35, 50, 70, 85, 100, 135, 200, 300, 400, 600, 800, 1200, 2000];

    public static double Share(Bin b, double total) => b.Share > 0 || total <= 0 ? b.Share : b.Count / total;

    public static bool HasData(Distribution? d) => d is not null && d.Bins.Count > 0 && Total(d) > 0;

    public static bool HasData(Matrix? m) => m is not null && m.XLabels.Count > 0 && m.YLabels.Count > 0 && MatrixMax(m) > 0;

    public static double Total(Distribution d)
    {
        double t = 0;
        foreach (var b in d.Bins)
        {
            if (double.IsFinite(b.Count) && b.Count > 0)
            {
                t += b.Count;
            }
        }

        return t;
    }

    public static double Val(Matrix m, int y, int x)
    {
        if (y < 0 || y >= m.Values.Count)
        {
            return 0;
        }

        var row = m.Values[y];
        if (row is null || x < 0 || x >= row.Count)
        {
            return 0;
        }

        var v = row[x];
        return double.IsFinite(v) && v > 0 ? v : 0;
    }

    public static double MatrixMax(Matrix m)
    {
        double max = 0;
        for (var y = 0; y < m.YLabels.Count; y++)
        {
            for (var x = 0; x < m.XLabels.Count; x++)
            {
                max = Math.Max(max, Val(m, y, x));
            }
        }

        return max;
    }

    public static double MatrixTotal(Matrix m)
    {
        double t = 0;
        for (var y = 0; y < m.YLabels.Count; y++)
        {
            for (var x = 0; x < m.XLabels.Count; x++)
            {
                t += Val(m, y, x);
            }
        }

        return t;
    }

    public static string Count(double v)
    {
        if (!double.IsFinite(v))
        {
            return "–";
        }

        return Math.Abs(v - Math.Round(v)) < 1e-9 ? Fmt.Int(v) : Fmt.Num(v, "#,0.##");
    }

    private static double Clean(double v) => double.IsFinite(v) && v > 0 ? v : 0;

    /// <summary>Vertical histogram with direct value labels and thinned category labels.</summary>
    public static void Columns(StringBuilder sb, Distribution d, double width, double height, ColumnOptions? o = null)
    {
        o ??= new ColumnOptions();
        var padTop = o.Compact ? 14.0 : 20.0;
        var padBottom = o.Compact ? 16.0 : 22.0;
        var svg = new Svg(sb, width, height, d.Title);
        var plotH = height - padTop - padBottom;
        DrawColumns(svg, d, 0, width, padTop, plotH, o, null);
        XLabels(svg, d.Bins.Count, 0, width / Math.Max(d.Bins.Count, 1), padTop + plotH + 15, i => o.XLabel is null ? d.Bins[i].Label : o.XLabel(d.Bins[i].Label), o.Compact);
        svg.End();
    }

    private static void DrawColumns(Svg svg, Distribution d, double x0, double width, double top, double plotH, ColumnOptions o, Func<int, string?>? fill)
    {
        var bins = d.Bins;
        var n = bins.Count;
        var total = Total(d);
        var baseY = top + plotH;
        var slot = width / Math.Max(n, 1);
        var barW = slot < 4 ? slot : slot * (n > 40 ? 0.86 : 0.72);
        double max = 0;
        foreach (var b in bins)
        {
            max = Math.Max(max, Clean(b.Count));
        }

        if (max <= 0)
        {
            svg.Line(x0, baseY, x0 + width, baseY, "ax");
            return;
        }

        var unit = string.IsNullOrEmpty(d.Unit) ? string.Empty : " " + d.Unit;
        for (var i = 0; i < n; i++)
        {
            var b = bins[i];
            var c = Clean(b.Count);
            var x = x0 + (i * slot);
            var h = Math.Max(c / max * plotH, 1);
            var accent = o.Accent?.Invoke(b, i) == true;
            svg.Group(title: b.Label + unit + ": " + Count(c) + " (" + Fmt.Pct(Share(b, total)) + ")");
            svg.Rect(x, top, slot, plotH, "hit");
            if (c > 0)
            {
                var f = fill?.Invoke(i);
                svg.Rect(x + ((slot - barW) / 2), baseY - h, barW, h, f is null ? (accent ? "m a" : "m") : "sw", fill: f);
            }

            svg.EndGroup();
        }

        svg.Line(x0, baseY, x0 + width, baseY, "ax");
        if (!o.ValueLabels)
        {
            return;
        }

        var order = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (Clean(bins[i].Count) > 0)
            {
                order.Add(i);
            }
        }

        order.Sort((a, b) =>
        {
            var cmp = Clean(bins[b].Count).CompareTo(Clean(bins[a].Count));
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        var limit = o.MaxValueLabels ?? (o.Compact ? 1 : n <= 16 ? n : 3);
        var placed = new List<(double L, double R)>();
        foreach (var i in order)
        {
            if (placed.Count >= limit)
            {
                break;
            }

            var b = bins[i];
            var text = Fmt.Pct(Share(b, total));
            var tw = Fmt.TextWidth(text, TickFont);
            var cx = x0 + ((i + 0.5) * slot);
            if (n <= 16 && !o.Compact && tw > slot + 6)
            {
                continue;
            }

            var l = cx - (tw / 2) - 3;
            var r = cx + (tw / 2) + 3;
            if (placed.Exists(p => l < p.R && r > p.L))
            {
                continue;
            }

            placed.Add((l, r));
            var y = baseY - (Clean(b.Count) / max * plotH) - 5;
            var accent = o.Accent?.Invoke(b, i) == true;
            svg.TextClamped(cx, Math.Max(y, 10), text, accent ? "vl at" : "vl", TickFont);
        }
    }

    private static void XLabels(Svg svg, int n, double x0, double slot, double y, Func<int, string> label, bool compact)
    {
        if (n == 0)
        {
            return;
        }

        if (compact)
        {
            var half = (n * slot / 2) - 6;
            svg.Text(x0, y, Fmt.Fit(label(0), half, TickFont), "tk");
            if (n > 1)
            {
                svg.Text(x0 + (n * slot), y, Fmt.Fit(label(n - 1), half, TickFont), "tk", "end");
            }

            return;
        }

        double maxW = 0;
        for (var i = 0; i < n; i++)
        {
            maxW = Math.Max(maxW, Fmt.TextWidth(label(i), TickFont));
        }

        var step = Math.Max(1, (int)Math.Ceiling((Math.Min(maxW, 90) + 8) / slot));
        var lastRight = double.NegativeInfinity;
        for (var i = 0; i < n; i += step)
        {
            var text = Fmt.Fit(label(i), Math.Max((slot * step) - 6, 24), TickFont);
            var w = Fmt.TextWidth(text, TickFont);
            var cx = x0 + ((i + 0.5) * slot);
            var left = Math.Max(0, cx - (w / 2));
            if (left < lastRight + 4)
            {
                continue;
            }

            lastRight = Math.Min(svg.Width, cx + (w / 2));
            svg.TextClamped(cx, y, text, "tk", TickFont);
        }
    }

    /// <summary>Horizontal bar list with direct labels and values.</summary>
    public static void BarList(StringBuilder sb, IReadOnlyList<BarItem> items, double width, string label, double rowHeight = 24)
    {
        double labelW = 0;
        double maxShare = 0;
        foreach (var it in items)
        {
            labelW = Math.Max(labelW, Fmt.TextWidth(it.Label, LabelFont));
            maxShare = Math.Max(maxShare, Clean(it.Share));
        }

        labelW = Math.Min(width * 0.42, labelW + 14);
        const double valueW = 104;
        var barMax = Math.Max(20, width - labelW - valueW);
        var height = (items.Count * rowHeight) + 2;
        var svg = new Svg(sb, width, height, label);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var y = i * rowHeight;
            var share = Clean(it.Share);
            var w = maxShare > 0 ? share / maxShare * barMax : 0;
            var value = it.Value ?? (Count(it.Count) + " · " + Fmt.Pct(share));
            svg.Group(dataKey: it.DataKey, title: it.Title ?? (it.Label + ": " + value));
            svg.Rect(0, y, width, rowHeight, "hit");
            svg.Text(0, y + (rowHeight / 2) + 4, Fmt.Fit(it.Label, labelW - 10, LabelFont), "lb");
            svg.Rect(labelW, y + (rowHeight / 2) - 5, Math.Max(w, share > 0 ? 1.5 : 0), 10, it.Accent ? "m a" : "m");
            svg.Text(labelW + w + 8, y + (rowHeight / 2) + 4, value, it.Accent ? "vl at" : "vl");
            svg.EndGroup();
        }

        svg.End();
    }

    /// <summary>Single-value bar on a 0..1 track, used in tables and cards.</summary>
    public static void MiniBar(StringBuilder sb, double value, bool accent, double width = 120, double height = 8, string? title = null)
    {
        var v = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var svg = new Svg(sb, width, height, title ?? Fmt.Pct(v), "mini");
        svg.Rect(0, 0, width, height, "trk");
        svg.Rect(0, 0, Math.Max(v * width, v > 0 ? 1.5 : 0), height, accent ? "m a" : "m", title);
        svg.End();
    }

    /// <summary>Score bar with a low–high bootstrap whisker on a 0..1 track.</summary>
    public static void ScoreBar(StringBuilder sb, double score, double low, double high, bool accent, double width = 300)
    {
        const double h = 22;
        var s = double.IsFinite(score) ? Math.Clamp(score, 0, 1) : 0;
        var lo = double.IsFinite(low) && low > 0 ? Math.Clamp(low, 0, 1) : s;
        var hi = double.IsFinite(high) && high > 0 ? Math.Clamp(high, 0, 1) : s;
        if (lo > hi)
        {
            (lo, hi) = (hi, lo);
        }

        var title = "Score " + Fmt.Num(s, "0.00") + " (range " + Fmt.Num(lo, "0.00") + "–" + Fmt.Num(hi, "0.00") + ")";
        var svg = new Svg(sb, width, h, title, "score");
        svg.Rect(0, 8, width, 6, "trk");
        svg.Rect(0, 8, Math.Max(s * width, 1.5), 6, accent ? "m a" : "m", title);
        if (hi - lo > 0.002)
        {
            svg.Line(lo * width, 11, hi * width, 11, "wh");
            svg.Line(lo * width, 5, lo * width, 17, "wh");
            svg.Line(hi * width, 5, hi * width, 17, "wh");
        }

        svg.Line(s * width, 2, s * width, 20, "wh");
        svg.End();
    }
}
