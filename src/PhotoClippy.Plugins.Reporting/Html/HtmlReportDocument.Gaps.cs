using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Gaps()
    {
        var focal = D(DistributionIds.Focal35);
        var (set, why) = GapLenses();
        var ranges = LensRanges(set);
        Grid(() =>
        {
            if (ranges.Count == 0)
            {
                return;
            }

            var cov = Charts.Coverage(focal, ranges);
            var sub = "Top lane: where you shoot (35 mm-equivalent, logarithmic). Below: each lens' range and the union of everything you own.";
            Figure("c-12", "Coverage versus use", sub, () =>
            {
                Charts.CoverageChart(_sb, focal, ranges, cov, Charts.W12);
                if (cov.HasFocalData)
                {
                    _sb.Append("<p class=\"stat\"><span>Inside owned ranges <b>").Append(Fmt.Pct(cov.Covered)).Append("</b></span>")
                        .Append("<span>Wider than your widest <b>").Append(Fmt.Pct(cov.Below)).Append("</b></span>")
                        .Append("<span>Longer than your longest <b>").Append(Fmt.Pct(cov.Above)).Append("</b></span>");
                    var between = cov.Gaps.Where(g => !g.Edge).Sum(g => g.Share);
                    if (cov.Gaps.Any(g => !g.Edge))
                    {
                        _sb.Append("<span>In gaps between lenses <b>").Append(Fmt.Pct(between)).Append("</b></span>");
                    }

                    _sb.Append("</p>");
                }
            }, () => CoverageTable(ranges, cov), why);
        });

        var pressure = Pressure();
        Grid(() =>
        {
            if (pressure.Count > 0)
            {
                var strongest = pressure.MaxBy(p => p.Share);
                var items = pressure
                    .Select(p => new BarItem(p.Label, p.Share * Math.Max(_sig.SampleCount, 0), p.Share, Accent: p == strongest && p.Share >= 0.05,
                        Title: p.Label + ": " + Fmt.Pct(p.Share) + " of captures — " + p.Hint, Value: Fmt.Pct(p.Share)))
                    .ToList();
                Figure("c-6", "Pressure signals", SubForSignals() + " · the larger the bar, the more often your current kit is at a limit", () =>
                {
                    Charts.BarList(_sb, items, Charts.W6, "Pressure signals");
                    _sb.Append("<dl class=\"kv small\">");
                    foreach (var p in pressure)
                    {
                        Kv(p.Label, p.Hint);
                    }

                    _sb.Append("</dl>");
                }, () =>
                {
                    _sb.Append("<thead><tr><th>Signal</th><th class=\"r\">Share</th><th>Meaning</th></tr></thead><tbody>");
                    foreach (var p in pressure)
                    {
                        _sb.Append("<tr><td>").AppendEsc(p.Label).Append("</td><td class=\"r\">").Append(Fmt.Pct(p.Share)).Append("</td><td>").AppendEsc(p.Hint).Append("</td></tr>");
                    }

                    _sb.Append("</tbody>");
                });
            }

            var lenses = set.Where(l => l.Captures > 0).OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal).Take(12).ToList();
            if (lenses.Count > 0)
            {
                Figure(pressure.Count > 0 ? "c-6" : "c-12", "At the limits, by lens", "Share of each lens' frames at an end stop, wide open, or light-starved", () =>
                {
                    _sb.Append("<div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Lens</th><th class=\"r\">Wide end</th><th class=\"r\">Tele end</th><th class=\"r\">Wide open</th><th class=\"r\">Light-starved</th></tr></thead><tbody>");
                    foreach (var l in lenses)
                    {
                        _sb.Append("<tr data-k=\"").Append(LensKey(l.Key)).Append("\"><td class=\"name\">").AppendEsc(l.Display).Append("<span class=\"dim\">")
                            .AppendEsc(LensSpecText(l)).Append("</span></td>");
                        PressureCell(l.IsZoom ? l.WideEndShare : null);
                        PressureCell(l.IsZoom ? l.TeleEndShare : null);
                        PressureCell(l.WideOpenShare);
                        PressureCell(l.LightStarvedShare);
                        _sb.Append("</tr>");
                    }

                    _sb.Append("</tbody></table></div>");
                }, note: "End-stop shares apply to zooms only. Values of 40 % or more are marked — a hint that the lens is often asked for more than it has.");
            }
        });
    }

    private void PressureCell(double? share)
    {
        if (share is not { } s || !double.IsFinite(s))
        {
            _sb.Append("<td class=\"r dim\">–</td>");
            return;
        }

        var hot = s >= 0.4;
        _sb.Append("<td class=\"r nw").Append(hot ? " hot" : string.Empty).Append("\">").Append(Fmt.Pct(s)).Append("</td>");
    }

    private string SubForSignals() => _sig.SampleCount > 0 ? Fmt.Int(_sig.SampleCount) + " captures with the needed data" : "Share of in-scope captures";

    private sealed record PressureSignal(string Label, double Share, string Hint);

    private List<PressureSignal> Pressure()
    {
        var all = new List<PressureSignal>
        {
            new("Aperture ceiling", _sig.ApertureCeilingShare, "shot wide open — a faster lens would give more light or blur"),
            new("Light-starved", _sig.LightStarvedShare, "wide open and at high ISO or a slow shutter"),
            new("High ISO", _sig.HighIsoShare, "sensitivity pushed high to keep the shutter fast enough"),
            new("Slow shutter", _sig.SlowShutterShare, "shutter slower than is safe hand-held"),
            new("Wide end", _sig.WideEndShare, "zoom racked to its widest — you may want wider"),
            new("Tele end", _sig.TeleEndShare, "zoom racked to its longest — you may want more reach"),
        };
        var clean = all.Where(p => double.IsFinite(p.Share) && p.Share > 0).Select(p => p with { Share = Math.Clamp(p.Share, 0, 1) }).ToList();
        return clean.Count == 0 ? [] : all.Select(p => p with { Share = double.IsFinite(p.Share) ? Math.Clamp(p.Share, 0, 1) : 0 }).ToList();
    }

    private (List<LensUsage> Set, string Why) GapLenses()
    {
        if (_p.Target is { } t && !string.IsNullOrWhiteSpace(t.BodyKey))
        {
            var onBody = _lenses.Where(l => (l.Bodies ?? []).Contains(t.BodyKey, StringComparer.Ordinal)).ToList();
            if (onBody.Count > 0)
            {
                var body = string.IsNullOrWhiteSpace(t.BodyDisplay) ? t.BodyKey : t.BodyDisplay;
                return (onBody, "Lenses used on " + body + ", the system recommendations are made for" + (string.IsNullOrWhiteSpace(t.Reason) ? "." : " (" + t.Reason + ")."));
            }
        }

        if (_lenses.Any(l => !l.IsFixed))
        {
            var fixedCount = _lenses.Count(l => l.IsFixed);
            return (_lenses.Where(l => !l.IsFixed).ToList(), fixedCount > 0
                ? "Interchangeable lenses only; " + Plural(fixedCount, "fixed lens", "fixed lenses") + " (compacts, phones) are left out."
                : "All interchangeable lenses in the library.");
        }

        return (_lenses.ToList(), "All lenses in the library.");
    }

    private void CoverageTable(IReadOnlyList<LensRange> ranges, CoverageResult cov)
    {
        _sb.Append("<thead><tr><th>Range</th><th>Kind</th><th class=\"r\">Share of captures</th></tr></thead><tbody>");
        foreach (var r in ranges.OrderBy(r => r.Min).ThenBy(r => r.Max).ThenBy(r => r.Label, StringComparer.Ordinal))
        {
            _sb.Append("<tr><td>").Append(Fmt.FocalRange(r.Min, r.Max)).Append("</td><td>Lens: ").AppendEsc(r.Label).Append("</td><td class=\"r\">–</td></tr>");
        }

        foreach (var g in cov.Gaps)
        {
            var kind = !g.Edge ? "Gap between lenses" : cov.Union.Count > 0 && g.From < cov.Union[0].Min ? "Wider than your widest" : "Longer than your longest";
            _sb.Append("<tr class=\"hl\"><td>").Append(Fmt.FocalRange(g.From, g.To)).Append("</td><td>").Append(kind).Append("</td><td class=\"r\">")
                .Append(Fmt.Pct(g.Share)).Append("</td></tr>");
        }

        _sb.Append("</tbody>");
    }
}
