using System.Globalization;
using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>
/// Builds the single-file HTML report. One instance renders one document; not thread-safe, never shared.
/// Every section is rendered defensively: a failure inside a section is replaced by a calm note.
/// </summary>
internal sealed partial class HtmlReportDocument
{
    private static readonly (string Id, string Title)[] Nav =
    [
        (ReportSections.Gear, "Gear"),
        (ReportSections.Focal, "Focal length"),
        (ReportSections.Aperture, "Aperture"),
        (ReportSections.Light, "Light"),
        (ReportSections.Subjects, "Subjects"),
        (ReportSections.Composition, "Composition"),
        (ReportSections.Color, "Look"),
        (ReportSections.Time, "Time"),
        (ReportSections.Gaps, "Gaps"),
        (ReportSections.Recommendation, "Recommendation"),
        (ReportSections.Method, "Method & data"),
    ];

    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportSections.Overview, ReportSections.Gear, ReportSections.Focal, ReportSections.Aperture, ReportSections.Light,
        ReportSections.Subjects, ReportSections.Composition, ReportSections.Color, ReportSections.Time, ReportSections.Gaps,
        ReportSections.Recommendation, ReportSections.Method,
    };

    private readonly ReportModel _m;
    private readonly ShootingProfile _p;
    private readonly LibrarySummary _lib;
    private readonly ShootingSignals _sig;
    private readonly DataQuality _q;
    private readonly GenreTaxonomy _tax;
    private readonly IReadOnlyList<LensUsage> _lenses;
    private readonly IReadOnlyList<BodyUsage> _bodies;
    private readonly string _theme;
    private readonly bool _tables;
    private readonly FontEmbedder.Result _fonts;
    private readonly StringBuilder _sb = new(256 * 1024);
    private readonly Dictionary<string, Distribution> _dist = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Matrix> _mat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Distribution> _ext = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedDist = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedMat = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedExt = new(StringComparer.Ordinal);
    private readonly List<Insight> _insights;
    private readonly Dictionary<string, string> _lensKeys = new(StringComparer.Ordinal);
    private Insight? _headline;

    public HtmlReportDocument(ReportModel model, string theme, bool dataTables, FontEmbedder.Result fonts)
    {
        _m = model;
        _p = model.Profile ?? new ShootingProfile { Library = new LibrarySummary() };
        _lib = _p.Library ?? new LibrarySummary();
        _sig = _p.Signals ?? new ShootingSignals();
        _q = _p.Quality ?? new DataQuality();
        _tax = model.Taxonomy ?? new GenreTaxonomy();
        _lenses = (_p.Lenses ?? []).Where(l => l is not null).ToList();
        _bodies = (_p.Bodies ?? []).Where(b => b is not null).ToList();
        _theme = theme is "light" or "dark" ? theme : "auto";
        _tables = dataTables;
        _fonts = fonts;
        _insights = (model.Insights ?? []).Where(i => i is not null).ToList();

        foreach (var (k, v) in _p.Distributions ?? new Dictionary<string, Distribution>())
        {
            if (k is not null && v is not null)
            {
                _dist[k] = Sanitize(v);
            }
        }

        foreach (var (k, v) in _p.Matrices ?? new Dictionary<string, Matrix>())
        {
            if (k is not null && v is not null)
            {
                _mat[k] = Sanitize(v);
            }
        }

        foreach (var (k, v) in _p.Extensions ?? new Dictionary<string, Distribution>())
        {
            if (k is not null && v is not null)
            {
                _ext[k] = Sanitize(v);
            }
        }

        var ordered = _lenses.OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            _lensKeys.TryAdd(ordered[i].Key ?? string.Empty, "l" + i.ToString(Fmt.Inv));
        }
    }

    public string Render()
    {
        _headline = PickHeadline();
        Head();
        _sb.Append("<body>");
        Header();
        NavBar();
        _sb.Append("<main id=\"top\"><div class=\"wrap\">");
        Hero();
        Section(ReportSections.Gear, "01", "Gear", "What you reach for: lenses and bodies by number of captures, and how each lens is used.", Gear, "Not enough data on gear yet — no lens or body information was found in the metadata.");
        Section(ReportSections.Focal, "02", "Focal length", "Where along the focal range you actually shoot, in 35 mm-equivalent terms, against the lenses you own.", Focal, "Not enough data on focal lengths yet — focal length was missing from the metadata.");
        Section(ReportSections.Aperture, "03", "Aperture", "How open you shoot, and how often you run into the widest aperture your lens allows.", Aperture, "Not enough data on apertures yet — the f-number was missing from the metadata.");
        Section(ReportSections.Light, "04", "Light", "The light you work in: exposure value, ISO and shutter speed, and how often you add your own.", Light, "Not enough data on exposure yet — ISO, shutter speed and aperture were missing from the metadata.");
        Section(ReportSections.Subjects, "05", "Subjects", "What you photograph, as estimated from the pictures themselves, and which focal lengths and lenses go with it.", Subjects, "Not enough data on subjects yet — no genre classifier ran for this report.");
        Section(ReportSections.Composition, "06", "Composition", "Where the subject sits in the frame, how the frame is shaped, and how much room you give it.", Composition, "Not enough data on composition yet — no frames were analysed for composition.");
        Section(ReportSections.Color, "07", "Look", "The colour and tone of your pictures: palette, hue, tonal range and key.", Look, "Not enough data on colour and tone yet — no frames were analysed for colour.");
        Section(ReportSections.Time, "08", "Time", "When you photograph: over the years, across the week and through the day.", Time, "Not enough data on time yet — capture dates were missing from the metadata.");
        Section(ReportSections.Gaps, "09", "Gaps", "Where your lenses leave gaps compared with where you shoot, and which limits you press against.", Gaps, "Not enough data to find gaps yet — this needs both lens specifications and focal lengths.");
        Section(ReportSections.Recommendation, "10", "Recommendation", "Candidate lenses ranked against your own shooting, with the evidence and the trade-offs.", Recommendation, null);
        Section(ReportSections.Method, "11", "Method & data quality", "What was analysed, what was missing, and how this report was produced.", Method, null);
        _sb.Append("</div></main>");
        Footer();
        _sb.Append("<script>").Append(ReportAssets.Script).Append("</script></body></html>\n");
        return _sb.ToString();
    }

    private void Head()
    {
        var stack = ReportAssets.FontStack;
        if (_fonts.Families.Count > 0)
        {
            stack = string.Join(",", _fonts.Families.Select(f => "\"" + f.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"")) + "," + stack;
        }

        _sb.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"").Append(_theme).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta name=\"color-scheme\" content=\"light dark\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; font-src data:\">")
            .Append("<meta name=\"generator\" content=\"photo-clippy ").AppendEsc(_m.Run?.ToolVersion).Append("\">")
            .Append("<title>").AppendEsc(Title).Append("</title><style>")
            .Append(_fonts.Css).Append(ReportAssets.Css(stack))
            .Append("</style><script>").Append(ReportAssets.BootScript).Append("</script></head>\n");
    }

    private string Title => string.IsNullOrWhiteSpace(_m.Title) ? "Photo habits" : _m.Title;

    private void Header()
    {
        _sb.Append("<header class=\"top\"><div class=\"wrap\"><p class=\"eyebrow\">Photo Clippy · shooting report</p><h1>")
            .AppendEsc(Title).Append("</h1><div class=\"meta\">");
        Span(Fmt.DateRange(_lib.FirstCapture, _lib.LastCapture));
        var roots = (_lib.Roots ?? []).Count;
        Span(roots == 1 ? "1 library root" : Fmt.Int(roots) + " library roots");
        Span(ScopeText());
        if (_m.Run is { } run)
        {
            Span("Run " + Fmt.Utc(run.StartedUtc));
        }

        _sb.Append("</div></div></header>\n");

        void Span(string text) => _sb.Append("<span>").AppendEsc(text).Append("</span>");
    }

    private string ScopeText() => string.Equals(_lib.Scope, "all", StringComparison.OrdinalIgnoreCase)
        ? "All devices in scope"
        : string.Equals(_lib.Scope, "interchangeable", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_lib.Scope)
            ? "Interchangeable-lens cameras in scope"
            : "Scope: " + _lib.Scope;

    private void NavBar()
    {
        _sb.Append("<nav class=\"toc\" aria-label=\"Sections\"><div class=\"wrap\"><ol>");
        for (var i = 0; i < Nav.Length; i++)
        {
            _sb.Append("<li><a href=\"#").Append(Nav[i].Id).Append("\"><span class=\"n\">").Append(Fmt.Two(i + 1)).Append("</span>")
                .Append(Nav[i].Title).Append("</a></li>");
        }

        _sb.Append("</ol><button id=\"theme\" type=\"button\" hidden>Theme</button></div></nav>\n");
    }

    private void Footer()
    {
        _sb.Append("<footer class=\"foot\"><div class=\"wrap\"><p>Generated by photo-clippy");
        if (_m.Run is { } run)
        {
            _sb.Append(' ').AppendEsc(run.ToolVersion).Append(" · run <span class=\"mono\">").AppendEsc(run.RunId).Append("</span> · ").AppendEsc(Fmt.Utc(run.StartedUtc));
        }

        _sb.Append(". A single self-contained file: no fonts, scripts or images are fetched from the network.</p></div></footer>\n");
    }

    private Insight? PickHeadline()
    {
        return _insights.FirstOrDefault(i => string.Equals(i.Section, ReportSections.Overview, StringComparison.OrdinalIgnoreCase))
            ?? _insights.FirstOrDefault(i => i.Tone == InsightTone.Attention)
            ?? _insights.FirstOrDefault(i => i.Tone == InsightTone.Positive)
            ?? _insights.FirstOrDefault();
    }

    private void Section(string id, string num, string title, string lede, Action body, string? emptyText)
    {
        var start = _sb.Length;
        try
        {
            SectionHead(id, num, title, lede);
            var mark = _sb.Length;
            Insights(id, string.Equals(id, ReportSections.Method, StringComparison.Ordinal));
            var afterInsights = _sb.Length;
            body();
            if (_sb.Length == afterInsights && emptyText is not null)
            {
                if (afterInsights == mark)
                {
                    Empty(emptyText);
                }
                else
                {
                    _sb.Append("<p class=\"empty\">").AppendEsc(emptyText).Append("</p>");
                }
            }

            _sb.Append("</section>\n");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _sb.Length = start;
            SectionHead(id, num, title, lede);
            Empty("This section could not be rendered from the data provided (" + ex.GetType().Name + ").");
            _sb.Append("</section>\n");
        }
    }

    private void SectionHead(string id, string num, string title, string lede)
    {
        _sb.Append("<section class=\"sec\" id=\"").Append(id).Append("\" aria-labelledby=\"h-").Append(id).Append("\"><div class=\"sh\"><p class=\"num\">")
            .Append(num).Append("</p><h2 id=\"h-").Append(id).Append("\">").AppendEsc(title).Append("</h2><p class=\"lede\">").AppendEsc(lede).Append("</p></div>");
    }

    private void Insights(string sectionId, bool includeUnknown = false)
    {
        var list = _insights.Where(i => !ReferenceEquals(i, _headline)
            && (string.Equals(i.Section, sectionId, StringComparison.OrdinalIgnoreCase)
                || (includeUnknown && (i.Section is null || !KnownSections.Contains(i.Section))))).ToList();
        if (list.Count == 0)
        {
            return;
        }

        _sb.Append("<div class=\"insights\">");
        foreach (var i in list)
        {
            var att = i.Tone == InsightTone.Attention;
            _sb.Append(att ? "<div class=\"ins att\">" : "<div class=\"ins\">").Append("<p class=\"tag\">")
                .Append(i.Tone switch { InsightTone.Attention => "Worth a look", InsightTone.Positive => "Strength", _ => "Note" })
                .Append("</p><h4>").AppendEsc(i.Title).Append("</h4>");
            if (!string.IsNullOrWhiteSpace(i.Metric))
            {
                _sb.Append("<span class=\"metric\">").AppendEsc(i.Metric).Append("</span>");
            }

            _sb.Append("<p>").AppendEsc(i.Text).Append("</p></div>");
        }

        _sb.Append("</div>");
    }

    private void Empty(string text) => _sb.Append("<p class=\"empty\">").AppendEsc(text).Append("</p>");

    private void Note(string text) => _sb.Append("<p class=\"note\">").AppendEsc(text).Append("</p>");

    /// <summary>Emits a 12-column grid only if the body produced any content.</summary>
    private void Grid(Action body)
    {
        var start = _sb.Length;
        _sb.Append("<div class=\"grid\">");
        var inner = _sb.Length;
        body();
        if (_sb.Length == inner)
        {
            _sb.Length = start;
            return;
        }

        _sb.Append("</div>");
    }

    private void Figure(string cls, string title, string? sub, Action chart, Action? table = null, string? note = null)
    {
        _sb.Append("<figure class=\"").Append(cls).Append("\"><figcaption><h3>").AppendEsc(title).Append("</h3>");
        if (!string.IsNullOrWhiteSpace(sub))
        {
            _sb.Append("<p class=\"sub\">").AppendEsc(sub).Append("</p>");
        }

        _sb.Append("</figcaption>");
        chart();
        if (!string.IsNullOrWhiteSpace(note))
        {
            Note(note);
        }

        if (_tables && table is not null)
        {
            _sb.Append("<details class=\"data\"><summary>Data</summary><div class=\"tw\"><table>");
            table();
            _sb.Append("</table></div></details>");
        }

        _sb.Append("</figure>");
    }

    /// <summary>Small-multiple figure for a distribution: compact columns plus its data table.</summary>
    private void SmallMultiple(string cls, Distribution d, string? title = null, Func<Bin, int, bool>? accent = null, Func<string, string>? xLabel = null)
    {
        Figure(cls + " sm", title ?? d.Title, SubFor(d), () =>
        {
            var w = cls switch { "c-3" => Charts.WSmall, "c-4" => Charts.W4, "c-6" => Charts.W6, _ => Charts.WSmall };
            Charts.Columns(_sb, d, w, 112, new ColumnOptions { Compact = true, Accent = accent, XLabel = xLabel, MaxValueLabels = 1 });
        }, () => DistTable(d, xLabel));
    }

    private static string SubFor(Distribution d)
    {
        var n = Charts.Total(d);
        var s = Fmt.Int(n) + (Math.Abs(n - 1) < 1e-9 ? " capture" : " captures");
        if (d.Stats is { Count: > 0 } st && d.Scale is AxisScale.Linear or AxisScale.Log && double.IsFinite(st.Median))
        {
            s += " · median " + Fmt.Num(st.Median, "0.##") + (string.IsNullOrEmpty(d.Unit) ? string.Empty : " " + d.Unit);
        }

        return s;
    }

    private void DistTable(Distribution d, Func<string, string>? label = null, Func<Bin, int, bool>? highlight = null)
    {
        var total = Charts.Total(d);
        var ranged = d.Scale is AxisScale.Linear or AxisScale.Log && d.Bins.Any(b => b.Lower is not null || b.Upper is not null);
        var unit = string.IsNullOrWhiteSpace(d.Unit) ? string.Empty : " (" + d.Unit + ")";
        _sb.Append("<thead><tr><th>").AppendEsc((ranged ? "Bin" : "Value") + unit).Append("</th>");
        if (ranged)
        {
            _sb.Append("<th class=\"r\">From</th><th class=\"r\">To</th>");
        }

        _sb.Append("<th class=\"r\">Captures</th><th class=\"r\">Share</th></tr></thead><tbody>");
        for (var i = 0; i < d.Bins.Count; i++)
        {
            var b = d.Bins[i];
            _sb.Append(highlight?.Invoke(b, i) == true ? "<tr class=\"hl\">" : "<tr>").Append("<td class=\"nw\">").AppendEsc(label is null ? b.Label : label(b.Label)).Append("</td>");
            if (ranged)
            {
                _sb.Append("<td class=\"r\">").Append(b.Lower is { } lo ? Fmt.Num(lo, "0.###") : "–").Append("</td><td class=\"r\">")
                    .Append(b.Upper is { } hi ? Fmt.Num(hi, "0.###") : "–").Append("</td>");
            }

            _sb.Append("<td class=\"r\">").Append(Charts.Count(b.Count)).Append("</td><td class=\"r\">").Append(Fmt.Pct(Charts.Share(b, total))).Append("</td></tr>");
        }

        _sb.Append("</tbody>");
    }

    private void MatrixTable(Matrix m, Func<string, string>? xLabel = null, Func<string, string>? yLabel = null)
    {
        var corner = (m.YTitle ?? string.Empty) + (m.XTitle is null ? string.Empty : " \\ " + m.XTitle);
        _sb.Append("<thead><tr><th>").AppendEsc(corner.Trim()).Append("</th>");
        foreach (var x in m.XLabels)
        {
            _sb.Append("<th class=\"r\">").AppendEsc(xLabel is null ? x : xLabel(x)).Append("</th>");
        }

        _sb.Append("<th class=\"r\">Total</th></tr></thead><tbody>");
        for (var y = 0; y < m.YLabels.Count; y++)
        {
            _sb.Append("<tr><td class=\"nw\">").AppendEsc(yLabel is null ? m.YLabels[y] : yLabel(m.YLabels[y])).Append("</td>");
            double row = 0;
            for (var x = 0; x < m.XLabels.Count; x++)
            {
                var v = Charts.Val(m, y, x);
                row += double.IsFinite(v) ? v : 0;
                _sb.Append("<td class=\"r\">").Append(v > 0 ? Charts.Count(v) : "·").Append("</td>");
            }

            _sb.Append("<td class=\"r\">").Append(Charts.Count(row)).Append("</td></tr>");
        }

        _sb.Append("</tbody>");
    }

    private void BarTable(IReadOnlyList<BarItem> items, string head)
    {
        _sb.Append("<thead><tr><th>").AppendEsc(head).Append("</th><th class=\"r\">Captures</th><th class=\"r\">Share</th></tr></thead><tbody>");
        foreach (var it in items)
        {
            _sb.Append("<tr><td>").AppendEsc(it.Label).Append("</td><td class=\"r\">").Append(Charts.Count(it.Count)).Append("</td><td class=\"r\">")
                .Append(Fmt.Pct(it.Share)).Append("</td></tr>");
        }

        _sb.Append("</tbody>");
    }

    private void HeatmapFigure(string cls, Matrix m, string? title, string? sub, double width, HeatmapOptions? o = null, string? note = null)
    {
        Figure(cls, title ?? m.Title, sub, () => Charts.Heatmap(_sb, m, width, o), () => MatrixTable(m, o?.XLabel, o?.YLabel), note);
    }

    private Distribution? D(string id)
    {
        _usedDist.Add(id);
        return _dist.TryGetValue(id, out var d) && Charts.HasData(d) ? d : null;
    }

    private Matrix? M(string id)
    {
        _usedMat.Add(id);
        return _mat.TryGetValue(id, out var m) && Charts.HasData(m) ? m : null;
    }

    /// <summary>Converts a distribution into bar items; the tail beyond <paramref name="top"/> is folded into "Other".</summary>
    private static List<BarItem> BarItems(Distribution d, int top = 12, Func<Bin, string>? label = null)
    {
        var total = Charts.Total(d);
        var items = new List<BarItem>();
        double restCount = 0;
        double restShare = 0;
        for (var i = 0; i < d.Bins.Count; i++)
        {
            var b = d.Bins[i];
            if (!(b.Count > 0))
            {
                continue;
            }

            if (items.Count < top)
            {
                items.Add(new BarItem(label is null ? b.Label : label(b), b.Count, Charts.Share(b, total)));
            }
            else
            {
                restCount += b.Count;
                restShare += Charts.Share(b, total);
            }
        }

        if (restCount > 0)
        {
            items.Add(new BarItem("Everything else", restCount, restShare));
        }

        return items;
    }

    private string GenreName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Unknown";
        }

        var g = _tax.Genres is null ? null : _tax.Find(id);
        if (g is not null && !string.IsNullOrWhiteSpace(g.Name))
        {
            return g.Name;
        }

        var s = id.Replace('-', ' ').Replace('_', ' ');
        return s.Length == 0 ? id : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private string LensKey(string? key) => key is not null && _lensKeys.TryGetValue(key, out var k) ? k : "l-";

    private static Distribution Sanitize(Distribution d)
    {
        if (d.Bins is null || d.Bins.Any(b => b is null))
        {
            return d with { Bins = d.Bins?.Where(b => b is not null).ToList() ?? [] };
        }

        return d;
    }

    private static Matrix Sanitize(Matrix m)
    {
        if (m.XLabels is null || m.YLabels is null || m.Values is null || m.Values.Any(r => r is null))
        {
            return m with
            {
                XLabels = m.XLabels ?? [],
                YLabels = m.YLabels ?? [],
                Values = m.Values?.Select(r => r ?? (IReadOnlyList<double>)[]).ToList() ?? [],
            };
        }

        return m;
    }

    /// <summary>Renders a "More" grid of small multiples for unused distributions whose key matches one of the prefixes.</summary>
    private void MoreGrid(string heading, Func<string, bool> match, bool includeCoreDistributions, bool includeMatrices)
    {
        var dists = new List<Distribution>();
        foreach (var key in _ext.Keys.Order(StringComparer.Ordinal))
        {
            if (!_usedExt.Contains(key) && match(key))
            {
                _usedExt.Add(key);
                if (Charts.HasData(_ext[key]))
                {
                    dists.Add(_ext[key]);
                }
            }
        }

        if (includeCoreDistributions)
        {
            foreach (var key in _dist.Keys.Order(StringComparer.Ordinal))
            {
                if (!_usedDist.Contains(key) && match(key))
                {
                    _usedDist.Add(key);
                    if (Charts.HasData(_dist[key]))
                    {
                        dists.Add(_dist[key]);
                    }
                }
            }
        }

        var mats = new List<Matrix>();
        if (includeMatrices)
        {
            foreach (var key in _mat.Keys.Order(StringComparer.Ordinal))
            {
                if (!_usedMat.Contains(key) && match(key))
                {
                    _usedMat.Add(key);
                    if (Charts.HasData(_mat[key]))
                    {
                        mats.Add(_mat[key]);
                    }
                }
            }
        }

        if (dists.Count == 0 && mats.Count == 0)
        {
            return;
        }

        var count = dists.Count + mats.Count;
        _sb.Append("<details class=\"more\"><summary><h3>").AppendEsc(heading).Append("</h3><span class=\"dim\">")
            .Append(count.ToString(CultureInfo.InvariantCulture)).Append(count == 1 ? " chart" : " charts").Append(" · raw analyzer signals</span></summary>");
        Grid(() =>
        {
            foreach (var d in dists)
            {
                SmallMultiple("c-3", d);
            }
        });
        Grid(() =>
        {
            foreach (var m in mats)
            {
                HeatmapFigure("c-6", m, null, null, Charts.W6);
            }
        });
        _sb.Append("</details>");
    }
}
