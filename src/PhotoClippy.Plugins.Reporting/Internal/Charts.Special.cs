using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed record CoverageResult(
    IReadOnlyList<(double Min, double Max)> Union,
    IReadOnlyList<CoverageGap> Gaps,
    double Covered,
    double Below,
    double Above,
    bool HasFocalData);

internal static partial class Charts
{
    private static readonly string[] HueNames = ["rose", "red", "orange", "yellow", "lime", "green", "teal", "cyan", "blue", "indigo", "violet", "magenta"];

    /// <summary>Matrix heatmap; cell opacity ∝ √value so small counts stay visible.</summary>
    public static void Heatmap(StringBuilder sb, Matrix m, double width, HeatmapOptions? o = null)
    {
        o ??= new HeatmapOptions();
        var cols = m.XLabels.Count;
        var rows = m.YLabels.Count;
        string Xl(int i) => o.XLabel is null ? m.XLabels[i] : o.XLabel(m.XLabels[i]);
        string Yl(int i) => o.YLabel is null ? m.YLabels[i] : o.YLabel(m.YLabels[i]);
        double labelW = 0;
        for (var y = 0; y < rows; y++)
        {
            labelW = Math.Max(labelW, Fmt.TextWidth(Yl(y), 11));
        }

        labelW = Math.Min(width * 0.32, labelW + 14);
        var cellW = (width - labelW) / Math.Max(cols, 1);
        var cellH = Math.Clamp(cellW * 0.75, 14, o.MaxCellHeight);
        var top = string.IsNullOrEmpty(m.YTitle) ? 4.0 : 20.0;
        var gridH = rows * cellH;
        var height = top + gridH + 22 + (string.IsNullOrEmpty(m.XTitle) ? 0 : 16);
        var svg = new Svg(sb, width, height, m.Title, "hm");
        if (!string.IsNullOrEmpty(m.YTitle))
        {
            svg.Text(labelW - 8, 11, Fmt.Fit(m.YTitle, labelW + 60, TickFont), "tk ttl", "end");
        }

        var total = MatrixTotal(m);
        var gmax = MatrixMax(m);
        var showValues = cellW >= 34 && cellH >= 16;
        for (var y = 0; y < rows; y++)
        {
            double rowMax = 0;
            double rowSum = 0;
            for (var x = 0; x < cols; x++)
            {
                var v = Val(m, y, x);
                rowMax = Math.Max(rowMax, v);
                rowSum += v;
            }

            var cy = top + (y * cellH);
            var yl = Yl(y);
            svg.Text(labelW - 8, cy + (cellH / 2) + 4, Fmt.Fit(yl, labelW - 12, 11), "lb sm", "end", yl + ": " + Count(rowSum));
            for (var x = 0; x < cols; x++)
            {
                var v = Val(m, y, x);
                var cx = labelW + (x * cellW);
                var xl = Xl(x);
                var title = yl + " · " + xl + ": " + Count(v) + (total > 0 ? " (" + Fmt.Pct(v / total) + " of all" + (o.RowNormalize && rowSum > 0 ? ", " + Fmt.Pct(v / rowSum) + " of row" : string.Empty) + ")" : string.Empty);
                svg.Group(title: title);
                if (v <= 0)
                {
                    svg.Rect(cx + 0.5, cy + 0.5, cellW - 1, cellH - 1, "z");
                }
                else
                {
                    var r = o.RowNormalize ? v / rowMax : v / gmax;
                    var op = 0.1 + (0.9 * Math.Sqrt(r));
                    svg.Rect(cx + 0.5, cy + 0.5, cellW - 1, cellH - 1, "hc", opacity: op);
                    if (showValues)
                    {
                        var share = o.RowNormalize ? v / rowSum : v / total;
                        svg.Text(cx + (cellW / 2), cy + (cellH / 2) + 3.5, Fmt.Pct(share), op > 0.55 ? "ti xs" : "vl xs", "middle");
                    }
                }

                svg.EndGroup();
            }
        }

        XLabels(svg, cols, labelW, cellW, top + gridH + 14, Xl, false);
        if (!string.IsNullOrEmpty(m.XTitle))
        {
            svg.Text(labelW + ((width - labelW) / 2), height - 3, m.XTitle, "tk ttl", "middle");
        }

        svg.End();
    }

    /// <summary>3×3 subject-placement grid drawn as a 3:2 frame with thirds lines.</summary>
    public static void Placement(StringBuilder sb, Matrix m, double width)
    {
        var cols = Math.Max(m.XLabels.Count, 1);
        var rows = Math.Max(m.YLabels.Count, 1);
        var fh = Math.Round(width * 2 / 3);
        var svg = new Svg(sb, width, fh + 1, m.Title, "pg");
        var total = MatrixTotal(m);
        var max = MatrixMax(m);
        var cw = width / cols;
        var ch = fh / rows;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var v = Val(m, y, x);
                var share = total > 0 ? v / total : 0;
                var name = (y < m.YLabels.Count ? m.YLabels[y] : string.Empty) + " · " + (x < m.XLabels.Count ? m.XLabels[x] : string.Empty);
                svg.Group(title: name + ": " + Fmt.Pct(share) + " (" + Count(v) + ")");
                var op = max > 0 && v > 0 ? 0.08 + (0.92 * Math.Sqrt(v / max)) : 0;
                if (v > 0)
                {
                    svg.Rect((x * cw) + 1, (y * ch) + 1, cw - 2, ch - 2, "hc", opacity: op);
                }
                else
                {
                    svg.Rect((x * cw) + 1, (y * ch) + 1, cw - 2, ch - 2, "z");
                }

                svg.Text((x * cw) + (cw / 2), (y * ch) + (ch / 2) + 5, Fmt.Pct(share), op > 0.55 ? "ti big" : "vl big", "middle");
                svg.EndGroup();
            }
        }

        for (var i = 1; i < 3; i++)
        {
            svg.Line(width * i / 3, 0, width * i / 3, fh, "th");
            svg.Line(0, fh * i / 3, width, fh * i / 3, "th");
        }

        svg.Rect(0.5, 0.5, width - 1, fh - 1, "fr");
        svg.End();
    }

    private static bool Bounded(Bin b) =>
        b.Lower is { } l && b.Upper is { } u && double.IsFinite(l) && double.IsFinite(u) && l > 0 && u > l;

    /// <summary>Log-axis focal histogram with owned-lens ranges as soft bands and lanes, and peak markers.</summary>
    public static void FocalHistogram(StringBuilder sb, Distribution d, IReadOnlyList<LensRange> lenses, IReadOnlyList<FocalPeak> peaks, double width)
    {
        var bins = d.Bins.Where(Bounded).ToList();
        if (bins.Count < 2 || bins.Count < d.Bins.Count / 2)
        {
            Columns(sb, d, width, 220);
            return;
        }

        var lo = bins.Min(b => b.Lower!.Value);
        var hi = bins.Max(b => b.Upper!.Value);
        foreach (var l in lenses)
        {
            lo = Math.Min(lo, l.Min);
            hi = Math.Max(hi, l.Max);
        }

        lo = Math.Max(lo / 1.08, 4);
        hi = Math.Min(hi * 1.08, 5000);
        var lnLo = Math.Log(lo);
        var span = Math.Log(hi) - lnLo;
        double X(double v) => (Math.Log(Math.Clamp(v, lo, hi)) - lnLo) / span * width;

        const double top = 28;
        const double plotH = 168;
        const double baseY = top + plotH;
        const double laneTop = baseY + 34;
        const double laneH = 18;
        var ordered = lenses.OrderBy(l => l.Min).ThenBy(l => l.Max).ThenBy(l => l.Label, StringComparer.Ordinal).ToList();
        var height = ordered.Count == 0 ? baseY + 22 : laneTop + (ordered.Count * laneH) + 2;
        var svg = new Svg(sb, width, height, d.Title, "fh");

        foreach (var l in ordered)
        {
            var (a, b) = l.Min >= l.Max * 0.999 ? (l.Min / 1.03, l.Max * 1.03) : (l.Min, l.Max);
            svg.Rect(X(a), top, X(b) - X(a), plotH, "band");
        }

        var bounded = bins.Sum(b => Clean(b.Count));
        double maxDensity = 0;
        foreach (var b in bins)
        {
            maxDensity = Math.Max(maxDensity, Clean(b.Count) / Math.Log(b.Upper!.Value / b.Lower!.Value));
        }

        var total = Total(d);
        foreach (var b in bins)
        {
            var c = Clean(b.Count);
            var x1 = X(b.Lower!.Value);
            var x2 = X(b.Upper!.Value);
            svg.Group(title: FocalBinLabel(b) + ": " + Count(c) + " (" + Fmt.Pct(Share(b, total)) + ")");
            svg.Rect(x1, top, x2 - x1, plotH, "hit");
            if (c > 0 && maxDensity > 0)
            {
                var h = Math.Max(c / Math.Log(b.Upper!.Value / b.Lower!.Value) / maxDensity * plotH, 1);
                svg.Rect(x1 + 0.5, baseY - h, Math.Max(x2 - x1 - 1, 0.8), h, "m");
            }

            svg.EndGroup();
        }

        svg.Line(0, baseY, width, baseY, "ax");
        var lastX = double.NegativeInfinity;
        foreach (var t in FocalTicks)
        {
            if (t < lo || t > hi)
            {
                continue;
            }

            var x = X(t);
            if (x - lastX < 30)
            {
                continue;
            }

            lastX = x;
            svg.Line(x, baseY, x, baseY + 4, "ax");
            svg.TextClamped(x, baseY + 16, Fmt.Num(t), "tk", TickFont);
        }

        var placed = new List<(double L, double R)>();
        foreach (var p in peaks.Where(p => double.IsFinite(p.Focal35Mm) && p.Focal35Mm > 0).OrderByDescending(p => p.Share).ThenBy(p => p.Focal35Mm).Take(6))
        {
            var x = X(p.Focal35Mm);
            var text = Fmt.Focal(p.Focal35Mm) + (p.Share > 0 ? " · " + Fmt.Pct(p.Share) : string.Empty);
            var title = "Peak at " + Fmt.Focal(p.Focal35Mm) + " (" + Fmt.FocalRange(p.LowerMm, p.UpperMm) + ", " + Fmt.Pct(p.Share) + ")";
            svg.Line(x, top - 8, x, baseY, "pk", title);
            var tw = Fmt.TextWidth(text, TickFont);
            var l = Math.Clamp(x - (tw / 2), 0, width - tw) - 4;
            var r = l + tw + 8;
            if (placed.Exists(q => l < q.R && r > q.L))
            {
                continue;
            }

            placed.Add((l, r));
            svg.TextClamped(x, top - 12, text, "vl pkl", TickFont, title);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var l = ordered[i];
            var cy = laneTop + (i * laneH) + (laneH / 2);
            var range = Fmt.FocalRange(l.Min, l.Max);
            svg.Group(dataKey: l.DataKey, title: l.Label + ": " + range + " (35 mm-eq)");
            var x1 = X(l.Min);
            var x2 = X(l.Max);
            if (x2 - x1 < 3)
            {
                svg.Circle(x1, cy, 3.5, "m");
                x2 = x1 + 4;
                x1 -= 4;
            }
            else
            {
                svg.Rect(x1, cy - 3, x2 - x1, 6, "m");
            }

            var text = l.Label + "  " + range;
            var tw = Fmt.TextWidth(text, 11);
            if (x2 + 8 + tw <= width)
            {
                svg.Text(x2 + 8, cy + 4, text, "lb sm");
            }
            else
            {
                svg.Text(Math.Max(x1 - 8, tw), cy + 4, Fmt.Fit(text, Math.Max(x1 - 8, 60), 11), "lb sm", "end");
            }

            svg.EndGroup();
        }

        svg.End();
    }

    public static string FocalBinLabel(Bin b) =>
        Bounded(b) ? Fmt.FocalRange(b.Lower, b.Upper) : b.Label;

    /// <summary>Monthly area timeline; missing months are filled with zero.</summary>
    public static void Timeline(StringBuilder sb, Distribution d, double width, double height)
    {
        var points = new SortedDictionary<int, double>();
        foreach (var b in d.Bins)
        {
            if (!Fmt.TryYearMonth(b.Label, out var yr, out var mo))
            {
                Columns(sb, d, width, height, new ColumnOptions { XLabel = Fmt.MonthLabel });
                return;
            }

            var key = (yr * 12) + mo - 1;
            points[key] = points.GetValueOrDefault(key) + Clean(b.Count);
        }

        if (points.Count < 2)
        {
            Columns(sb, d, width, height, new ColumnOptions { XLabel = Fmt.MonthLabel });
            return;
        }

        var first = points.Keys.First();
        var n = Math.Min(points.Keys.Last() - first + 1, 2400);
        var values = new double[n];
        foreach (var (k, v) in points)
        {
            if (k - first < n)
            {
                values[k - first] = v;
            }
        }

        var total = values.Sum();
        var max = values.Max();
        const double top = 24;
        const double bottom = 22;
        var plotH = height - top - bottom;
        var baseY = top + plotH;
        var step = width / (n - 1);
        double X(int i) => i * step;
        double Y(int i) => max > 0 ? baseY - (values[i] / max * plotH) : baseY;
        string Label(int i) => Fmt.Month(new DateTime(((first + i) / 12), ((first + i) % 12) + 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var svg = new Svg(sb, width, height, d.Title, "tl");
        var area = new PathData().M(X(0), baseY);
        var line = new PathData();
        for (var i = 0; i < n; i++)
        {
            area.L(X(i), Y(i));
            if (i == 0)
            {
                line.M(X(i), Y(i));
            }
            else
            {
                line.L(X(i), Y(i));
            }
        }

        area.L(X(n - 1), baseY).Z();
        svg.Path(area.ToString(), "ar");
        svg.Path(line.ToString(), "ln");
        svg.Line(0, baseY, width, baseY, "ax");

        var lastX = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            if ((first + i) % 12 != 0)
            {
                continue;
            }

            var x = X(i);
            svg.Line(x, baseY, x, baseY + 4, "ax");
            if (x - lastX >= 38)
            {
                svg.TextClamped(x, baseY + 16, ((first + i) / 12).ToString(Fmt.Inv), "tk", TickFont);
                lastX = x;
            }
        }

        for (var i = 0; i < n; i++)
        {
            svg.Group(title: Label(i) + ": " + Count(values[i]) + (total > 0 ? " (" + Fmt.Pct(values[i] / total) + ")" : string.Empty));
            svg.Rect(Math.Max(X(i) - (step / 2), 0), top, Math.Min(step, width), plotH, "hit");
            svg.EndGroup();
        }

        var peak = Array.IndexOf(values, max);
        if (peak >= 0 && max > 0)
        {
            svg.Circle(X(peak), Y(peak), 3, "dot");
            svg.TextClamped(X(peak), Y(peak) - 9, Label(peak) + " · " + Count(max), "vl", TickFont);
        }

        svg.End();
    }

    /// <summary>Palette ribbon sized by weight. Returns false when no valid colour exists.</summary>
    public static bool Palette(StringBuilder sb, IReadOnlyList<PaletteColor> palette, double width)
    {
        var valid = new List<(string Hex, double Weight)>();
        foreach (var c in palette)
        {
            if (ColorMath.NormalizeHex(c.Hex) is { } hex && double.IsFinite(c.Weight) && c.Weight > 0)
            {
                valid.Add((hex, c.Weight));
            }
        }

        if (valid.Count == 0)
        {
            return false;
        }

        var sum = valid.Sum(v => v.Weight);
        const double h = 76;
        var svg = new Svg(sb, width, h + 38, "Palette", "pal");
        double x = 0;
        foreach (var (hex, weight) in valid)
        {
            var w = weight / sum * width;
            var share = weight / sum;
            svg.Group(title: hex.ToUpperInvariant() + " · " + Fmt.Pct(share));
            svg.Rect(x, 0, Math.Max(w - 2, 1), h, "sw", fill: hex);
            if (w >= 58)
            {
                svg.Text(x, h + 16, hex.ToUpperInvariant(), "lb sm mono");
                svg.Text(x, h + 31, Fmt.Pct(share), "tk");
            }

            svg.EndGroup();
            x += w;
        }

        svg.End();
        return true;
    }

    private static double BinCentre(Bin b, int i, int n, double span)
    {
        if (b.Lower is { } l && b.Upper is { } u && double.IsFinite(l) && double.IsFinite(u) && u > l)
        {
            return (l + u) / 2;
        }

        var w = span / Math.Max(n, 1);
        return double.TryParse(b.Label, System.Globalization.NumberStyles.Float, Fmt.Inv, out var v) && double.IsFinite(v)
            ? v + (w / 2)
            : (i * w) + (w / 2);
    }

    public static string HueName(double centreDegrees)
    {
        var deg = ((centreDegrees % 360) + 360) % 360;
        return HueNames[(int)(deg / 30) % 12];
    }

    /// <summary>Hue histogram (OKLCh hue bins) with each bar drawn in its own hue and named below.</summary>
    public static void HueStrip(StringBuilder sb, Distribution d, double width)
    {
        var n = d.Bins.Count;
        var svg = new Svg(sb, width, 172, d.Title, "hue");
        var centres = new double[n];
        for (var i = 0; i < n; i++)
        {
            centres[i] = BinCentre(d.Bins[i], i, n, 360);
        }

        DrawColumns(svg, d, 0, width, 22, 104, new ColumnOptions(), i => ColorMath.OkLch(0.68, 0.12, centres[i]));
        var slot = width / Math.Max(n, 1);
        var names = slot >= Fmt.TextWidth("magenta", 11) + 4;
        for (var i = 0; i < n; i++)
        {
            var cx = (i + 0.5) * slot;
            svg.Rect((i * slot) + 1, 132, slot - 2, 5, "sw", fill: ColorMath.OkLch(0.68, 0.12, centres[i]));
            if (names)
            {
                svg.Text(cx, 152, HueName(centres[i]), "lb sm", "middle");
            }

            svg.Text(cx, 167, Fmt.Num(Math.Round(centres[i])) + "°", "tk", "middle");
        }

        svg.End();
    }

    /// <summary>Tone (lightness) histogram with a neutral grey scale beneath the bars.</summary>
    public static void Tone(StringBuilder sb, Distribution d, double width)
    {
        var n = d.Bins.Count;
        var svg = new Svg(sb, width, 172, d.Title, "tone");
        DrawColumns(svg, d, 0, width, 22, 104, new ColumnOptions { MaxValueLabels = 2 }, null);
        var slot = width / Math.Max(n, 1);
        for (var i = 0; i < n; i++)
        {
            var c = BinCentre(d.Bins[i], i, n, 1);
            var l = c > 1.5 ? c / 100 : c;
            svg.Rect(i * slot, 132, slot + 0.3, 10, "sw", Fmt.Pct(Math.Clamp(l, 0, 1)) + " lightness", fill: ColorMath.Gray(Math.Clamp(l, 0, 1)));
        }

        svg.Text(0, 160, "shadows", "tk");
        svg.Text(width / 2, 160, "midtones", "tk", "middle");
        svg.Text(width, 160, "highlights", "tk", "end");
        svg.End();
    }

    /// <summary>Single stacked bar with direct labels; labels that do not fit are left to the tooltip and data table.</summary>
    public static bool Stacked(StringBuilder sb, IReadOnlyList<BarItem> items, double width, string label)
    {
        var valid = items.Where(i => Clean(i.Count) > 0).ToList();
        var total = valid.Sum(i => Clean(i.Count));
        if (total <= 0)
        {
            return false;
        }

        const double barH = 18;
        var svg = new Svg(sb, width, barH + 42, label, "stk");
        double x = 0;
        var rows = new[] { double.NegativeInfinity, double.NegativeInfinity };
        for (var i = 0; i < valid.Count; i++)
        {
            var it = valid[i];
            var share = Clean(it.Count) / total;
            var w = share * width;
            svg.Group(dataKey: it.DataKey, title: it.Label + ": " + Count(it.Count) + " (" + Fmt.Pct(share) + ")");
            svg.Rect(x, 0, Math.Max(w - 2, 1), barH, it.Accent ? "m a" : "f" + ((i % 5) + 1).ToString(Fmt.Inv));
            var text = it.Label + " " + Fmt.Pct(share);
            var tw = Fmt.TextWidth(text, 11);
            var tx = Math.Min(x, width - tw);
            for (var r = 0; r < rows.Length; r++)
            {
                if (tx >= rows[r] + 10)
                {
                    if (r > 0)
                    {
                        svg.Line(x + 0.5, barH + 2, x + 0.5, barH + 4 + (r * 16), "lead");
                    }

                    svg.Text(tx, barH + 15 + (r * 16), text, it.Accent ? "lb sm at" : "lb sm");
                    rows[r] = tx + tw;
                    break;
                }
            }

            svg.EndGroup();
            x += w;
        }

        svg.End();
        return true;
    }

    private static (double A, double B) LensSpan(LensRange l) =>
        l.Min >= l.Max * 0.999 ? (l.Min / 1.1, l.Max * 1.1) : (l.Min * 0.98, l.Max * 1.02);

    private static double ShareIn(IReadOnlyList<Bin> bins, double total, double a, double b)
    {
        if (total <= 0 || b <= a)
        {
            return 0;
        }

        double s = 0;
        foreach (var bin in bins)
        {
            var l = bin.Lower!.Value;
            var u = bin.Upper!.Value;
            var lo = Math.Max(a, l);
            var hi = Math.Min(b, u);
            if (hi > lo)
            {
                s += Clean(bin.Count) / total * (Math.Log(hi / lo) / Math.Log(u / l));
            }
        }

        return s;
    }

    /// <summary>Union of owned lens ranges (primes ±10 %) and the share of shooting that falls outside it.</summary>
    public static CoverageResult Coverage(Distribution? focal, IReadOnlyList<LensRange> lenses)
    {
        var spans = lenses.Select(LensSpan).Where(s => double.IsFinite(s.A) && double.IsFinite(s.B) && s.A > 0 && s.B > s.A).OrderBy(s => s.A).ThenBy(s => s.B).ToList();
        var union = new List<(double Min, double Max)>();
        foreach (var (a, b) in spans)
        {
            if (union.Count > 0 && a <= union[^1].Max)
            {
                union[^1] = (union[^1].Min, Math.Max(union[^1].Max, b));
            }
            else
            {
                union.Add((a, b));
            }
        }

        var bins = focal?.Bins.Where(Bounded).ToList() ?? [];
        var total = bins.Sum(b => Clean(b.Count));
        var gaps = new List<CoverageGap>();
        if (union.Count == 0 || total <= 0)
        {
            return new CoverageResult(union, gaps, 0, 0, 0, total > 0);
        }

        var minF = bins.Min(b => b.Lower!.Value);
        var maxF = bins.Max(b => b.Upper!.Value);
        var below = ShareIn(bins, total, minF, union[0].Min);
        var above = ShareIn(bins, total, union[^1].Max, maxF);
        if (below >= 0.005)
        {
            gaps.Add(new CoverageGap(minF, union[0].Min, below, true));
        }

        for (var i = 0; i + 1 < union.Count; i++)
        {
            if (union[i + 1].Min / union[i].Max > 1.15)
            {
                gaps.Add(new CoverageGap(union[i].Max, union[i + 1].Min, ShareIn(bins, total, union[i].Max, union[i + 1].Min), false));
            }
        }

        if (above >= 0.005)
        {
            gaps.Add(new CoverageGap(union[^1].Max, maxF, above, true));
        }

        var covered = union.Sum(u => ShareIn(bins, total, u.Min, u.Max));
        return new CoverageResult(union, gaps, Math.Min(covered, 1), below, above, true);
    }

    /// <summary>Focal coverage chart: where you shoot (density strip) against owned lens ranges and their union.</summary>
    public static void CoverageChart(StringBuilder sb, Distribution? focal, IReadOnlyList<LensRange> lenses, CoverageResult cov, double width)
    {
        const string shootLabel = "Where you shoot";
        const string coverLabel = "Owned coverage";
        var ordered = lenses.OrderBy(l => l.Min).ThenBy(l => l.Max).ThenBy(l => l.Label, StringComparer.Ordinal).ToList();
        var labelW = Math.Max(Fmt.TextWidth(shootLabel, 11), Fmt.TextWidth(coverLabel, 11));
        foreach (var l in ordered)
        {
            labelW = Math.Max(labelW, Fmt.TextWidth(l.Label, 11));
        }

        labelW = Math.Min(width * 0.28, labelW + 16);
        var bins = focal?.Bins.Where(Bounded).ToList() ?? [];
        var lo = double.PositiveInfinity;
        var hi = 0.0;
        foreach (var b in bins)
        {
            lo = Math.Min(lo, b.Lower!.Value);
            hi = Math.Max(hi, b.Upper!.Value);
        }

        foreach (var l in ordered)
        {
            var (a, b) = LensSpan(l);
            lo = Math.Min(lo, a);
            hi = Math.Max(hi, b);
        }

        if (!double.IsFinite(lo) || hi <= lo)
        {
            lo = 10;
            hi = 600;
        }

        lo = Math.Max(lo / 1.06, 4);
        hi = Math.Min(hi * 1.06, 5000);
        var lnLo = Math.Log(lo);
        var span = Math.Log(hi) - lnLo;
        var plotW = width - labelW;
        double X(double v) => labelW + ((Math.Log(Math.Clamp(v, lo, hi)) - lnLo) / span * plotW);

        const double top = 26;
        const double shootH = 22;
        const double laneH = 18;
        var lanesTop = top + shootH + 10;
        var coverY = lanesTop + (ordered.Count * laneH) + 8;
        var axisY = coverY + 22;
        var height = axisY + 22;
        var svg = new Svg(sb, width, height, "Focal coverage", "cov");

        foreach (var g in cov.Gaps)
        {
            var x1 = X(g.From);
            var x2 = X(g.To);
            var text = Fmt.FocalRange(g.From, g.To) + " · " + Fmt.Pct(g.Share);
            var title = (g.Edge ? (g.From < cov.Union[0].Min ? "Wider than your widest lens: " : "Longer than your longest lens: ") : "Gap between lenses: ") + text + " of captures";
            svg.Group(title: title);
            svg.Rect(x1, top - 6, x2 - x1, axisY - top + 6, "gapb");
            if (g.Share >= 0.001)
            {
                svg.TextClamped((x1 + x2) / 2, top - 12, text, "vl at", TickFont);
            }

            svg.EndGroup();
        }

        svg.Text(labelW - 10, top + (shootH / 2) + 4, shootLabel, "lb sm", "end");
        double maxD = 0;
        foreach (var b in bins)
        {
            maxD = Math.Max(maxD, Clean(b.Count) / Math.Log(b.Upper!.Value / b.Lower!.Value));
        }

        var total = bins.Sum(b => Clean(b.Count));
        foreach (var b in bins)
        {
            var dns = Clean(b.Count) / Math.Log(b.Upper!.Value / b.Lower!.Value);
            var x1 = X(b.Lower!.Value);
            var x2 = X(b.Upper!.Value);
            var title = FocalBinLabel(b) + ": " + (total > 0 ? Fmt.Pct(Clean(b.Count) / total) : "0%");
            if (dns > 0 && maxD > 0)
            {
                svg.Rect(x1, top, x2 - x1 + 0.3, shootH, "hc", title, 0.06 + (0.94 * Math.Sqrt(dns / maxD)));
            }
        }

        if (bins.Count == 0)
        {
            svg.Text(labelW, top + (shootH / 2) + 4, "no focal-length data", "tk");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var l = ordered[i];
            var cy = lanesTop + (i * laneH) + (laneH / 2);
            var (a, b) = LensSpan(l);
            svg.Group(dataKey: l.DataKey, title: l.Label + ": " + Fmt.FocalRange(l.Min, l.Max) + (l.Min >= l.Max * 0.999 ? " (counted as " + Fmt.FocalRange(Math.Round(a), Math.Round(b)) + ")" : string.Empty));
            svg.Text(labelW - 10, cy + 4, Fmt.Fit(l.Label, labelW - 14, 11), "lb sm", "end");
            if (l.Min >= l.Max * 0.999)
            {
                svg.Rect(X(a), cy - 3, X(b) - X(a), 6, "m pr");
                svg.Circle(X(l.Min), cy, 3.5, "m");
            }
            else
            {
                svg.Rect(X(l.Min), cy - 3, Math.Max(X(l.Max) - X(l.Min), 2), 6, "m");
            }

            svg.EndGroup();
        }

        svg.Text(labelW - 10, coverY + 4, coverLabel, "lb sm", "end");
        foreach (var (min, max) in cov.Union)
        {
            svg.Rect(X(min), coverY - 4, X(max) - X(min), 8, "cv", "Covered: " + Fmt.FocalRange(Math.Round(min), Math.Round(max)));
        }

        foreach (var g in cov.Gaps)
        {
            svg.Rect(X(g.From), coverY - 1.5, X(g.To) - X(g.From), 3, "m a");
        }

        svg.Line(labelW, axisY, width, axisY, "ax");
        var lastX = double.NegativeInfinity;
        foreach (var t in FocalTicks)
        {
            if (t < lo || t > hi)
            {
                continue;
            }

            var x = X(t);
            if (x - lastX < 30)
            {
                continue;
            }

            lastX = x;
            svg.Line(x, axisY, x, axisY + 4, "ax");
            svg.TextClamped(x, axisY + 16, Fmt.Num(t), "tk", TickFont);
        }

        svg.End();
    }
}
