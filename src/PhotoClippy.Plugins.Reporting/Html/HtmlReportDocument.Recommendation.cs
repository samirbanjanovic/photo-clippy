using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private const string CandidatesCommand = "photo-clippy recommend --candidates \"24/1.4, 24-70/2.8\"";

    private static readonly Dictionary<string, string> CriterionFallbackNames = new(StringComparer.Ordinal)
    {
        [CriterionIds.Fit] = "Fit",
        [CriterionIds.LowLight] = "Low light",
        [CriterionIds.Isolation] = "Isolation",
        [CriterionIds.Reach] = "Reach",
        [CriterionIds.Genre] = "Subjects",
        [CriterionIds.Portability] = "Portability",
        [CriterionIds.Value] = "Value",
    };

    private void Recommendation()
    {
        var r = _m.Recommendation;
        var ranked = (r?.Ranked ?? []).Where(x => x?.Lens is not null).OrderBy(x => x.Rank).ThenByDescending(x => x.Score).ThenBy(x => x.Lens.Id, StringComparer.Ordinal).ToList();
        if (r is null || ranked.Count == 0)
        {
            Empty(r is null
                ? "Not enough data for a recommendation yet — no candidate lenses were supplied."
                : "Not enough data for a recommendation yet — none of the candidate lenses could be scored against this library.");
            _sb.Append("<p class=\"note\">List the lenses you are considering — as shorthand (focal/aperture) or by name — and run:</p><pre class=\"cli\"><code>")
                .AppendEsc(CandidatesCommand).Append("</code></pre>");
            if (r is not null)
            {
                SmallPrint(r);
            }

            return;
        }

        var criteria = CriteriaOrder(r, ranked);
        var head = !string.IsNullOrWhiteSpace(r.Headline) ? r.Headline! : ranked[0].Lens.Name + " fits the way you shoot best.";
        _sb.Append("<p class=\"rhead\">").AppendEsc(head).Append("</p>");
        Note(RecContext(r));
        _sb.Append("<div class=\"recs\">");
        foreach (var rec in ranked)
        {
            Card(r, rec, criteria, ReferenceEquals(rec, ranked[0]));
        }

        _sb.Append("</div>");

        Grid(() =>
        {
            var weights = criteria.Where(c => r.Weights.TryGetValue(c.Id, out var w) && double.IsFinite(w) && w > 0).ToList();
            if (weights.Count > 0)
            {
                var sum = weights.Sum(c => r.Weights[c.Id]);
                var items = weights.Select(c => new BarItem(c.Name, r.Weights[c.Id], sum > 0 ? r.Weights[c.Id] / sum : 0,
                    Title: c.Name + ": weight " + Fmt.Pct(sum > 0 ? r.Weights[c.Id] / sum : 0) + (string.IsNullOrWhiteSpace(c.Description) ? string.Empty : " — " + c.Description),
                    Value: Fmt.Pct(sum > 0 ? r.Weights[c.Id] / sum : 0))).ToList();
                Figure("c-6", "What counts", "Criterion weights under the “" + r.Preset + "” preset", () => Charts.BarList(_sb, items, Charts.W6, "Criterion weights"), () =>
                {
                    _sb.Append("<thead><tr><th>Criterion</th><th class=\"r\">Weight</th><th>Meaning</th></tr></thead><tbody>");
                    foreach (var c in weights)
                    {
                        _sb.Append("<tr><td>").AppendEsc(c.Name).Append("</td><td class=\"r\">").Append(Fmt.Num(r.Weights[c.Id], "0.###")).Append("</td><td>")
                            .AppendEsc(c.Description).Append("</td></tr>");
                    }

                    _sb.Append("</tbody>");
                });
            }

            var winners = (r.PresetWinners ?? new Dictionary<string, string>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (winners.Count > 0)
            {
                _sb.Append("<figure class=\"").Append(weights.Count > 0 ? "c-6" : "c-12").Append("\"><figcaption><h3>Winner by preset</h3><p class=\"sub\">")
                    .Append("Which candidate ranks first if you weigh the criteria differently</p></figcaption><div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Preset</th><th>Ranks first</th></tr></thead><tbody>");
                foreach (var (preset, id) in winners)
                {
                    var name = ranked.FirstOrDefault(x => string.Equals(x.Lens.Id, id, StringComparison.Ordinal))?.Lens.Name ?? id;
                    var current = string.Equals(preset, r.Preset, StringComparison.OrdinalIgnoreCase);
                    _sb.Append(current ? "<tr class=\"hl\">" : "<tr>").Append("<td>").AppendEsc(preset);
                    if (current)
                    {
                        _sb.Append(" <span class=\"badge\">this ranking</span>");
                    }

                    _sb.Append("</td><td class=\"name\">").AppendEsc(name).Append("</td></tr>");
                }

                _sb.Append("</tbody></table></div></figure>");
            }
        });
        SmallPrint(r);
    }

    private string RecContext(RecommendationSet r)
    {
        var parts = new List<string>();
        var target = r.Target;
        if (target is not null)
        {
            var body = !string.IsNullOrWhiteSpace(target.BodyDisplay) ? target.BodyDisplay : target.BodyKey;
            var sys = (string.IsNullOrWhiteSpace(body) ? string.Empty : "For " + body + " · ") + target.Format;
            if (double.IsFinite(target.CropFactor) && target.CropFactor > 0)
            {
                sys += " (crop ×" + Fmt.Num(target.CropFactor, "0.0#") + ")";
            }

            parts.Add(sys);
        }

        parts.Add(Plural(r.CandidateCount > 0 ? r.CandidateCount : r.Ranked.Count, "candidate", "candidates"));
        if (r.SampleCount > 0)
        {
            parts.Add("scored against " + Fmt.Int(r.SampleCount) + " of your captures");
        }

        if (!string.IsNullOrWhiteSpace(r.Preset))
        {
            parts.Add("“" + r.Preset + "” weighting");
        }

        return string.Join(" · ", parts) + ".";
    }

    private static List<CriterionInfo> CriteriaOrder(RecommendationSet r, IReadOnlyList<LensRecommendation> ranked)
    {
        var list = new List<CriterionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in r.Criteria ?? [])
        {
            if (c is not null && !string.IsNullOrWhiteSpace(c.Id) && seen.Add(c.Id))
            {
                list.Add(string.IsNullOrWhiteSpace(c.Name) ? c with { Name = CriterionName(c.Id) } : c);
            }
        }

        foreach (var id in ranked.SelectMany(x => x.Criteria?.Keys ?? []).Concat(r.Weights?.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                list.Add(new CriterionInfo(id, CriterionName(id), string.Empty));
            }
        }

        return list;
    }

    private static string CriterionName(string id) => CriterionFallbackNames.TryGetValue(id, out var n) ? n : id;

    private void Card(RecommendationSet r, LensRecommendation rec, IReadOnlyList<CriterionInfo> criteria, bool best)
    {
        var lens = rec.Lens;
        var key = "cand-" + LensKey(lens.Id);
        _sb.Append("<article class=\"rec").Append(best ? " best" : string.Empty).Append(rec.AlreadyOwned ? " owned" : string.Empty).Append("\" data-k=\"").Append(key)
            .Append("\"><div class=\"rank\" aria-label=\"Rank ").Append(rec.Rank.ToString(Fmt.Inv)).Append("\">").Append(rec.Rank.ToString("00", Fmt.Inv)).Append("</div><div>");
        _sb.Append("<h3>").AppendEsc(lens.Name).Append(" <span class=\"badge\">").Append(SourceText(lens.Source)).Append("</span>");
        if (rec.AlreadyOwned)
        {
            _sb.Append(" <span class=\"badge\">already owned</span>");
        }

        _sb.Append("</h3><p class=\"meta2\">").AppendEsc(CandidateSpec(lens, r.Target)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(rec.Verdict))
        {
            _sb.Append("<p class=\"verdict\">").AppendEsc(rec.Verdict).Append("</p>");
        }

        var lo = rec.ScoreLow > 0 ? rec.ScoreLow : rec.Score;
        var hi = rec.ScoreHigh > 0 ? rec.ScoreHigh : rec.Score;
        _sb.Append("<div class=\"scoreline\"><span>Score <b>").Append(Fmt.Num(Clamp01(rec.Score), "0.00")).Append("</b></span>");
        Charts.ScoreBar(_sb, rec.Score, rec.ScoreLow, rec.ScoreHigh, best);
        if (Math.Abs(hi - lo) > 0.002)
        {
            _sb.Append("<span>range ").Append(Fmt.Num(Clamp01(Math.Min(lo, hi)), "0.00")).Append("–").Append(Fmt.Num(Clamp01(Math.Max(lo, hi)), "0.00")).Append("</span>");
        }

        if (double.IsFinite(rec.ProbabilityBest) && rec.ProbabilityBest > 0)
        {
            _sb.Append("<span>#1 in ").Append(Fmt.Pct(Clamp01(rec.ProbabilityBest))).Append(" of resamples</span>");
        }

        _sb.Append("</div>");

        var scored = criteria.Where(c => rec.Criteria is not null && rec.Criteria.ContainsKey(c.Id)).ToList();
        if (scored.Count > 0)
        {
            _sb.Append("<div class=\"crit\">");
            foreach (var c in scored)
            {
                var v = Clamp01(rec.Criteria![c.Id]);
                _sb.Append("<div><span>").AppendEsc(c.Name).Append("<em>").Append(Fmt.Num(v, "0.00")).Append("</em></span>");
                Charts.MiniBar(_sb, v, false, 150, 6, lens.Name + " · " + c.Name + ": " + Fmt.Num(v, "0.00"));
                _sb.Append("</div>");
            }

            _sb.Append("</div>");
        }

        var cols = new (string Title, IReadOnlyList<string>? Items)[] { ("Evidence", rec.Evidence), ("Trade-offs", rec.TradeOffs), ("Wins instead when…", rec.WinsInsteadWhen) };
        if (cols.Any(c => c.Items is { Count: > 0 }))
        {
            _sb.Append("<div class=\"cols3\">");
            foreach (var (title, items) in cols)
            {
                var list = (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (list.Count == 0)
                {
                    continue;
                }

                _sb.Append("<div><h4>").AppendEsc(title).Append("</h4><ul class=\"ev\">");
                foreach (var s in list)
                {
                    _sb.Append("<li>").AppendEsc(s).Append("</li>");
                }

                _sb.Append("</ul></div>");
            }

            _sb.Append("</div>");
        }

        if (RedundancyText(rec) is { } red)
        {
            _sb.Append("<p class=\"redund\">").AppendEsc(red).Append("</p>");
        }

        if (_tables)
        {
            _sb.Append("<details class=\"data\"><summary>Data</summary><div class=\"tw\"><table><thead><tr><th>Measure</th><th class=\"r\">Value</th></tr></thead><tbody>")
                .Append("<tr><td>Score</td><td class=\"r\">").Append(Fmt.Num(rec.Score, "0.000")).Append("</td></tr>")
                .Append("<tr><td>Score range</td><td class=\"r\">").Append(Fmt.Num(rec.ScoreLow, "0.000")).Append("–").Append(Fmt.Num(rec.ScoreHigh, "0.000")).Append("</td></tr>")
                .Append("<tr><td>Probability of ranking first</td><td class=\"r\">").Append(Fmt.Pct(rec.ProbabilityBest)).Append("</td></tr>")
                .Append("<tr><td>Redundancy</td><td class=\"r\">").Append(Fmt.Num(rec.Redundancy, "0.00")).Append("</td></tr>");
            foreach (var c in scored)
            {
                _sb.Append("<tr><td>").AppendEsc(c.Name).Append("</td><td class=\"r\">").Append(Fmt.Num(rec.Criteria![c.Id], "0.000")).Append("</td></tr>");
            }

            foreach (var (k, v) in (rec.Metrics ?? new Dictionary<string, double>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                _sb.Append("<tr><td><code>").AppendEsc(k).Append("</code></td><td class=\"r\">").Append(Fmt.Num(v, "0.###")).Append("</td></tr>");
            }

            _sb.Append("</tbody></table></div></details>");
        }

        _sb.Append("</div></article>");
    }

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    private static string SourceText(LensSpecSource s) => s switch
    {
        LensSpecSource.User => "your candidate",
        LensSpecSource.Suggested => "suggested archetype",
        LensSpecSource.Exif => "owned · from EXIF",
        LensSpecSource.Observed => "owned · observed",
        _ => "candidate",
    };

    private static string CandidateSpec(LensSpec lens, TargetSystem? target)
    {
        var parts = new List<string>
        {
            Fmt.FocalRange(lens.FocalMinMm, lens.FocalMaxMm) + " " + Fmt.ApertureRange(lens.MaxApertureWide, lens.MaxApertureTele),
        };
        if (target is not null && double.IsFinite(target.CropFactor) && target.CropFactor > 0 && Math.Abs(target.CropFactor - 1) > 0.01)
        {
            parts.Add("≈ " + Fmt.FocalRange(lens.FocalMinMm * target.CropFactor, lens.FocalMaxMm * target.CropFactor) + " equivalent");
        }

        if (lens.WeightG is { } g && double.IsFinite(g) && g > 0)
        {
            parts.Add(Fmt.Int(g) + " g");
        }

        if (lens.PriceUsd is { } p && double.IsFinite(p) && p > 0)
        {
            parts.Add("$" + Fmt.Int(p));
        }

        return string.Join(" · ", parts);
    }

    private static string? RedundancyText(LensRecommendation rec)
    {
        if (rec.AlreadyOwned)
        {
            return "You already own this lens; it is ranked so you can see how it compares with the alternatives.";
        }

        var overlap = double.IsFinite(rec.Redundancy) ? Clamp01(rec.Redundancy) : 0;
        if (!string.IsNullOrWhiteSpace(rec.RedundantWith))
        {
            var pct = " (redundancy " + Fmt.Pct(overlap) + ": shared focal range, discounted for any extra speed)";
            return overlap switch
            {
                >= 0.4 => "Largely redundant with " + rec.RedundantWith + ", which you already own" + pct + "; the score is reduced accordingly.",
                >= 0.1 => "Partly redundant with " + rec.RedundantWith + ", which you already own" + pct + "; the score is reduced slightly.",
                _ => null,
            };
        }

        return overlap >= 0.2
            ? "Redundant with lenses you already own (redundancy " + Fmt.Pct(overlap) + "); the score is reduced accordingly."
            : null;
    }

    private void SmallPrint(RecommendationSet r)
    {
        var notes = (r.Notes ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var assumptions = (r.Assumptions ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (notes.Count == 0 && assumptions.Count == 0 && string.IsNullOrWhiteSpace(r.RecommenderId))
        {
            return;
        }

        _sb.Append("<div class=\"cols3 small prints\">");
        if (notes.Count > 0)
        {
            SmallList("Notes", notes);
        }

        if (assumptions.Count > 0)
        {
            SmallList("Assumptions", assumptions);
        }

        if (!string.IsNullOrWhiteSpace(r.RecommenderId))
        {
            _sb.Append("<div><h4>Recommender</h4><ul><li><code>").AppendEsc(r.RecommenderId).Append("</code></li></ul></div>");
        }

        _sb.Append("</div>");
    }

    private void SmallList(string title, IEnumerable<string> items)
    {
        _sb.Append("<div><h4>").AppendEsc(title).Append("</h4><ul>");
        foreach (var s in items)
        {
            _sb.Append("<li>").AppendEsc(s).Append("</li>");
        }

        _sb.Append("</ul></div>");
    }
}
