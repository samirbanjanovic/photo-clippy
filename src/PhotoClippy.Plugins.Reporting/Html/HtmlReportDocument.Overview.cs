using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Hero()
    {
        _sb.Append("<section class=\"hero\" id=\"").Append(ReportSections.Overview).Append("\" aria-label=\"Overview\">");
        if (_headline is { } h)
        {
            _sb.Append(h.Tone == InsightTone.Attention ? "<div class=\"headline att\">" : "<div class=\"headline\">").Append("<p class=\"lbl\">").AppendEsc(h.Title);
            if (!string.IsNullOrWhiteSpace(h.Metric))
            {
                _sb.Append(" · ").AppendEsc(h.Metric);
            }

            _sb.Append("</p><p class=\"h\">").AppendEsc(h.Text).Append("</p></div>");
        }
        else
        {
            _sb.Append("<div class=\"headline\"><p class=\"lbl\">In one sentence</p><p class=\"h\">").AppendEsc(FallbackHeadline()).Append("</p></div>");
        }

        _sb.Append("<dl class=\"kpis\">");
        Kpi("Captures in scope", _lib.CapturesInScope > 0 ? Fmt.Int(_lib.CapturesInScope) : "–", CapturesSub(), false);
        var zooms = _lenses.Count(l => l.IsZoom);
        Kpi("Lenses", _lenses.Count > 0 ? Fmt.Int(_lenses.Count) : "–",
            _lenses.Count > 0 ? Plural(zooms, "zoom", "zooms") + " · " + Plural(_lenses.Count - zooms, "prime", "primes") : null, false);
        var topBody = _bodies.OrderByDescending(b => b.Captures).ThenBy(b => b.Key, StringComparer.Ordinal).FirstOrDefault();
        Kpi("Bodies", _bodies.Count > 0 ? Fmt.Int(_bodies.Count) : "–", topBody is null ? null : "mostly " + topBody.Display, false);
        Kpi("Median aperture", _sig.MedianFNumber is { } f ? Fmt.FNum(f) : "–",
            _sig.SampleCount > 0 ? Fmt.Pct(_sig.ApertureCeilingShare) + " at the lens limit" : null, false);
        var focal = FocalSummary.Dominant(_p);
        Kpi(FocalSummary.MedianLabel, FocalSummary.Median(_p), focal.Caption, false);
        var (genre, genreSub) = TopGenre();
        Kpi("Top subject", genre, genreSub, genre != "–");
        _sb.Append("</dl><p class=\"note focal-explanation\">").AppendEsc(FocalSummary.Explanation).Append("</p>");
        Insights(ReportSections.Overview);
        _sb.Append("</section>\n");
    }

    private void Kpi(string label, string value, string? sub, bool word)
    {
        _sb.Append("<div class=\"kpi\"><dt>").AppendEsc(label).Append("</dt><dd").Append(word ? " class=\"word\">" : ">").AppendEsc(value).Append("</dd>");
        if (!string.IsNullOrWhiteSpace(sub))
        {
            _sb.Append("<dd class=\"sub\">").AppendEsc(sub).Append("</dd>");
        }

        _sb.Append("</div>");
    }

    private static string Plural(int n, string one, string many) => Fmt.Int(n) + " " + (n == 1 ? one : many);

    private string? CapturesSub()
    {
        if (_lib.Captures > _lib.CapturesInScope && _lib.Captures > 0)
        {
            return "of " + Fmt.Int(_lib.Captures) + " captures";
        }

        return _lib.FilesSeen > 0 ? Plural(_lib.FilesSeen, "file seen", "files seen") : null;
    }

    private (string Value, string? Sub) TopGenre()
    {
        if (!_dist.TryGetValue(DistributionIds.Genre, out var d) || !Charts.HasData(d))
        {
            return ("–", null);
        }

        var total = Charts.Total(d);
        var best = d.Bins.Select((b, i) => (b, i)).OrderByDescending(t => t.b.Count).ThenBy(t => t.i).First().b;
        return (GenreName(best.Key ?? best.Label), Fmt.Pct(Charts.Share(best, total)) + " of classified frames");
    }

    private string FallbackHeadline()
    {
        if (_lib.CapturesInScope <= 0 && !_dist.Values.Any(Charts.HasData))
        {
            return "Not enough data for a headline yet — point the analyzer at a library with photos and run it again.";
        }

        var parts = new List<string>();
        var (focal, _) = FocalSummary.Dominant(_p);
        if (focal is not null)
        {
            parts.Add("most often around " + focal);
        }
        else if (FocalSummary.Median(_p) is { } median && median != "–")
        {
            parts.Add("with a median focal length of " + median);
        }

        if (_sig.MedianFNumber is { } f)
        {
            parts.Add("typically at " + Fmt.FNum(f));
        }

        var scope = _lib.CapturesInScope > 0 ? "Across " + Fmt.Int(_lib.CapturesInScope) + " captures, you shoot " : "You shoot ";
        return parts.Count == 0 ? "Your library has been analysed; details follow section by section." : scope + string.Join(" and ", parts) + ".";
    }
}
