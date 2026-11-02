using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Gear()
    {
        var lensDist = D(DistributionIds.Lens);
        var bodyDist = D(DistributionIds.Body);
        var lensYear = M(MatrixIds.LensByYear);
        Grid(() =>
        {
            if (_lenses.Count > 0)
            {
                LensLedger();
            }
            else if (lensDist is not null)
            {
                Figure("c-12", "Lenses", SubFor(lensDist), () => Charts.BarList(_sb, BarItems(lensDist), Charts.W12, lensDist.Title), () => DistTable(lensDist));
            }

            if (_bodies.Count > 0)
            {
                BodyLedger();
            }
            else if (bodyDist is not null)
            {
                Figure("c-6", "Bodies", SubFor(bodyDist), () => Charts.BarList(_sb, BarItems(bodyDist), Charts.W6, bodyDist.Title), () => DistTable(bodyDist));
            }

            if (lensYear is not null)
            {
                HeatmapFigure(_bodies.Count > 0 || bodyDist is not null ? "c-6" : "c-12", lensYear, "Lenses over the years", "Captures per lens and year · darker means more",
                    _bodies.Count > 0 || bodyDist is not null ? Charts.W6 : Charts.W12, new HeatmapOptions { MaxCellHeight = 22 });
            }
        });
    }

    private void LensLedger()
    {
        var total = _lenses.Sum(l => Math.Max(l.Captures, 0));
        var ordered = _lenses.OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
        Figure("c-12", "Lenses", Plural(ordered.Count, "lens", "lenses") + " · " + Fmt.Int(total) + " captures with a known lens · medians in 35 mm-equivalent focal length", () =>
        {
            _sb.Append("<div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Lens</th><th>Share of captures</th><th class=\"r\">Focal</th><th class=\"r\">Aperture</th>")
                .Append("<th class=\"r\">ISO</th><th>Wide open</th><th>Top subject</th><th class=\"r\">In use</th></tr></thead><tbody>");
            foreach (var l in ordered)
            {
                var share = l.Share > 0 ? l.Share : total > 0 ? (double)l.Captures / total : 0;
                _sb.Append("<tr data-k=\"").Append(LensKey(l.Key)).Append("\"><td class=\"name\">").AppendEsc(l.Display).Append("<span class=\"dim\">").AppendEsc(LensSpecText(l)).Append("</span></td>")
                    .Append("<td class=\"nw\">");
                Charts.MiniBar(_sb, share, false, 110, 7, l.Display + ": " + Fmt.Pct(share));
                _sb.Append(Fmt.Pct(share)).Append(" <span class=\"dim\" style=\"display:inline\">· ").Append(Fmt.Int(l.Captures)).Append("</span></td>")
                    .Append("<td class=\"r nw\">").Append(l.MedianFocal35 is { } f ? Fmt.Focal(f) : "–").Append("</td>")
                    .Append("<td class=\"r nw\">").Append(l.MedianFNumber is { } a ? Fmt.FNum(a) : "–").Append("</td>")
                    .Append("<td class=\"r nw\">").Append(l.MedianIso is { } iso ? Fmt.Int(iso) : "–").Append("</td><td class=\"nw\">");
                if (l.MaxApertureWide is not null || l.WideOpenShare > 0)
                {
                    Charts.MiniBar(_sb, l.WideOpenShare, false, 70, 7, "Wide open: " + Fmt.Pct(l.WideOpenShare));
                    _sb.Append(Fmt.Pct(l.WideOpenShare));
                }
                else
                {
                    _sb.Append('–');
                }

                _sb.Append("</td><td>").AppendEsc(string.IsNullOrWhiteSpace(l.TopGenre) ? "–" : GenreName(l.TopGenre)).Append("</td><td class=\"r nw\">")
                    .Append(Period(l.First, l.Last)).Append("</td></tr>");
            }

            _sb.Append("</tbody></table></div>");
        }, note: "Wide open means within ⅓ stop of the lens' maximum aperture at that focal length. Hover a row to find the lens in other charts.");
    }

    private void BodyLedger()
    {
        var total = _bodies.Sum(b => Math.Max(b.Captures, 0));
        var ordered = _bodies.OrderByDescending(b => b.Captures).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
        Figure("c-6", "Bodies", Plural(ordered.Count, "body", "bodies") + " · " + Fmt.Int(total) + " captures", () =>
        {
            _sb.Append("<div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Body</th><th>Share of captures</th><th class=\"r\">In use</th></tr></thead><tbody>");
            foreach (var b in ordered)
            {
                var share = b.Share > 0 ? b.Share : total > 0 ? (double)b.Captures / total : 0;
                _sb.Append("<tr><td class=\"name\">").AppendEsc(b.Display).Append("<span class=\"dim\">").AppendEsc(BodySpecText(b)).Append("</span></td><td class=\"nw\">");
                Charts.MiniBar(_sb, share, false, 90, 7, b.Display + ": " + Fmt.Pct(share));
                _sb.Append(Fmt.Pct(share)).Append(" <span class=\"dim\" style=\"display:inline\">· ").Append(Fmt.Int(b.Captures)).Append("</span></td><td class=\"r nw\">")
                    .Append(Period(b.First, b.Last)).Append("</td></tr>");
            }

            _sb.Append("</tbody></table></div>");
        });
    }

    private static string LensSpecText(LensUsage l)
    {
        var parts = new List<string>();
        var range = Fmt.FocalRange(l.FocalMinMm, l.FocalMaxMm);
        if (range != "–")
        {
            if (l.FocalMin35 is { } a && l.FocalMinMm is { } b && Math.Abs(a - b) > 1)
            {
                range += " (" + Fmt.FocalRange(l.FocalMin35, l.FocalMax35) + " eq.)";
            }

            parts.Add(range);
        }

        var ap = Fmt.ApertureRange(l.MaxApertureWide, l.MaxApertureTele);
        if (ap != "–")
        {
            parts.Add(ap);
        }

        parts.Add(l.IsFixed ? "built-in" : l.IsZoom ? "zoom" : "prime");
        if (l.SpecSource == LensSpecSource.Observed && (l.FocalMinMm is not null || l.MaxApertureWide is not null))
        {
            parts.Add("range as observed");
        }

        return string.Join(" · ", parts);
    }

    private static string BodySpecText(BodyUsage b)
    {
        var parts = new List<string> { DeviceName(b.Device) };
        if (!string.IsNullOrWhiteSpace(b.Format))
        {
            parts.Add(b.Format!);
        }

        if (b.CropFactor is { } c && double.IsFinite(c) && c > 0)
        {
            parts.Add(Fmt.Num(c, "0.0#") + "× crop");
        }

        return string.Join(" · ", parts);
    }

    private static string DeviceName(DeviceClass d) => d switch
    {
        DeviceClass.InterchangeableLens => "Interchangeable lens",
        DeviceClass.FixedLens => "Fixed lens",
        DeviceClass.Phone => "Phone",
        DeviceClass.Drone => "Drone",
        DeviceClass.Scanner => "Scanner",
        DeviceClass.Software => "Software",
        _ => "Unknown device",
    };

    private static string Period(DateTime? first, DateTime? last)
    {
        if (first is { } f && last is { } l)
        {
            return f.Year == l.Year ? f.Year.ToString(Fmt.Inv) : f.Year.ToString(Fmt.Inv) + "–" + l.Year.ToString(Fmt.Inv);
        }

        return first?.Year.ToString(Fmt.Inv) ?? last?.Year.ToString(Fmt.Inv) ?? "–";
    }

    /// <summary>Owned-lens 35 mm-eq ranges; interchangeable lenses unless only fixed ones are known.</summary>
    private List<LensRange> LensRanges(IEnumerable<LensUsage> lenses, int max = 12)
    {
        return lenses
            .Where(l => l.FocalMin35 is { } a && l.FocalMax35 is { } b && double.IsFinite(a) && double.IsFinite(b) && a > 0 && b >= a)
            .OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal)
            .Take(max)
            .Select(l => new LensRange(LensKey(l.Key), l.Display, l.FocalMin35!.Value, l.FocalMax35!.Value))
            .ToList();
    }

    private IEnumerable<LensUsage> InterchangeableLenses() => _lenses.Any(l => !l.IsFixed) ? _lenses.Where(l => !l.IsFixed) : _lenses;

    private void Focal()
    {
        var f = D(DistributionIds.Focal35);
        var native = D(DistributionIds.FocalNative);
        var fa = M(MatrixIds.FocalByAperture);
        var peaks = (_p.FocalPeaks ?? []).Where(p => p is not null && double.IsFinite(p.Focal35Mm) && p.Focal35Mm > 0).ToList();
        Grid(() =>
        {
            if (f is not null)
            {
                var ranges = LensRanges(InterchangeableLenses());
                var sub = SubFor(f) + " · logarithmic axis";
                if (ranges.Count > 0)
                {
                    sub += " · shaded bands and lanes are the lenses you own";
                }

                Figure("c-12", "Focal length, 35 mm-equivalent", sub, () =>
                {
                    Charts.FocalHistogram(_sb, f, ranges, peaks, Charts.W12);
                    FocalStats(f, peaks);
                }, () => DistTable(f, highlight: (b, _) => peaks.Any(p => b.Lower is { } lo && b.Upper is { } hi && p.Focal35Mm >= lo && p.Focal35Mm < hi)));
            }

            if (native is not null)
            {
                Figure(fa is null ? "c-12" : "c-6", native.Title, SubFor(native) + " · as recorded, before crop-factor conversion",
                    () => Charts.Columns(_sb, native, fa is null ? Charts.W12 : Charts.W6, 190), () => DistTable(native));
            }

            if (fa is not null)
            {
                HeatmapFigure(native is null ? "c-12" : "c-6", fa, "Focal length × aperture", "Each row is normalised to its own total",
                    native is null ? Charts.W12 : Charts.W6, new HeatmapOptions { RowNormalize = true, MaxCellHeight = 22 });
            }
        });
    }

    private void FocalStats(Distribution f, IReadOnlyList<FocalPeak> peaks)
    {
        _sb.Append("<p class=\"stat\">");
        if (_sig.MedianFocal35 is { } med)
        {
            _sb.Append("<span>Median <b>").Append(Fmt.Focal(med)).Append("</b></span>");
        }

        if (f.Stats is { Count: > 0 } st && double.IsFinite(st.P10) && double.IsFinite(st.P90) && st.P90 > 0)
        {
            _sb.Append("<span>Middle 80% <b>").Append(Fmt.FocalRange(st.P10, st.P90)).Append("</b></span>");
        }

        if (_sig.FocalSpreadStops is { } spread && double.IsFinite(spread))
        {
            _sb.Append("<span>Spread <b>").Append(Fmt.Num(spread, "0.0")).Append(" stops</b> (inter-quartile)</span>");
        }

        if (peaks.Count > 0)
        {
            _sb.Append("<span>Peaks <b>").AppendEsc(string.Join(", ", peaks.OrderByDescending(p => p.Share).ThenBy(p => p.Focal35Mm).Take(4)
                .Select(p => Fmt.Focal(p.Focal35Mm) + " (" + Fmt.Pct(p.Share) + ")"))).Append("</b></span>");
        }

        _sb.Append("</p>");
    }

    private void Aperture()
    {
        var ap = D(DistributionIds.Aperture);
        var stops = D(DistributionIds.StopsFromMax);
        var ga = M(MatrixIds.GenreByAperture);
        var lensItems = _lenses
            .Where(l => !l.IsFixed && l.Captures > 0 && (l.MaxApertureWide is not null || l.WideOpenShare > 0))
            .OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal).Take(10).ToList();
        Grid(() =>
        {
            if (ap is not null)
            {
                Figure(stops is null ? "c-12" : "c-8", "Aperture", SubFor(ap) + (_sig.MedianFNumber is { } m ? " · median " + Fmt.FNum(m) : string.Empty),
                    () => Charts.Columns(_sb, ap, stops is null ? Charts.W12 : Charts.W8, 220), () => DistTable(ap));
            }

            if (stops is not null)
            {
                var wide = WideOpenBin(stops);
                var total = Charts.Total(stops);
                var wideShare = _sig.SampleCount > 0 && _sig.ApertureCeilingShare > 0 ? _sig.ApertureCeilingShare : wide >= 0 ? Charts.Share(stops.Bins[wide], total) : 0;
                Figure(ap is null ? "c-12" : "c-4", "Stops from the lens' maximum", "0 = wide open · " + Fmt.Int(total) + " captures", () =>
                {
                    Charts.Columns(_sb, stops, ap is null ? Charts.W12 : Charts.W4, 220, new ColumnOptions { Accent = (_, i) => i == wide });
                    _sb.Append("<span class=\"big acc\">").Append(Fmt.Pct(wideShare)).Append("</span><p class=\"sub\">of frames at the widest aperture the lens allows — the aperture ceiling</p>");
                }, () => DistTable(stops, highlight: (_, i) => i == wide));
            }

            if (lensItems.Count > 0)
            {
                var max = lensItems.Max(l => l.WideOpenShare);
                var items = lensItems.Select(l => new BarItem(l.Display, l.Captures, l.WideOpenShare, LensKey(l.Key), l.WideOpenShare >= 0.4 && l.WideOpenShare >= max - 1e-9,
                    l.Display + ": " + Fmt.Pct(l.WideOpenShare) + " wide open (" + Fmt.ApertureRange(l.MaxApertureWide, l.MaxApertureTele) + ")", Fmt.Pct(l.WideOpenShare))).ToList();
                Figure("c-6", "Wide open, by lens", "Share of each lens' frames within ⅓ stop of its maximum aperture",
                    () => Charts.BarList(_sb, items, Charts.W6, "Wide-open share by lens"),
                    () =>
                    {
                        _sb.Append("<thead><tr><th>Lens</th><th class=\"r\">Max aperture</th><th class=\"r\">Captures</th><th class=\"r\">Wide open</th></tr></thead><tbody>");
                        foreach (var l in lensItems)
                        {
                            _sb.Append("<tr><td>").AppendEsc(l.Display).Append("</td><td class=\"r nw\">").Append(Fmt.ApertureRange(l.MaxApertureWide, l.MaxApertureTele))
                                .Append("</td><td class=\"r\">").Append(Fmt.Int(l.Captures)).Append("</td><td class=\"r\">").Append(Fmt.Pct(l.WideOpenShare)).Append("</td></tr>");
                        }

                        _sb.Append("</tbody>");
                    });
            }

            if (ga is not null)
            {
                HeatmapFigure(lensItems.Count > 0 ? "c-6" : "c-12", ga, "Aperture by subject", "Each row is normalised to its own total",
                    lensItems.Count > 0 ? Charts.W6 : Charts.W12, new HeatmapOptions { RowNormalize = true, MaxCellHeight = 22, YLabel = GenreName });
            }
        });
    }

    private static int WideOpenBin(Distribution d)
    {
        for (var i = 0; i < d.Bins.Count; i++)
        {
            var b = d.Bins[i];
            if (b.Label == "0" || b.Key == "0")
            {
                return i;
            }
        }

        for (var i = 0; i < d.Bins.Count; i++)
        {
            if (d.Bins[i] is { Lower: { } lo, Upper: { } hi } && lo <= 0 && hi <= 0.34)
            {
                return i;
            }
        }

        return d.Bins.Count > 0 ? 0 : -1;
    }
}
