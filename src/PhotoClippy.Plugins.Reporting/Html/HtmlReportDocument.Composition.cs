using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Composition()
    {
        var placement = M(MatrixIds.Placement);
        var orientation = D(DistributionIds.Orientation);
        var aspect = D(DistributionIds.AspectRatio);
        var edges = D(DistributionIds.EdgeOrientation);
        var tilt = D(DistributionIds.HorizonTilt);
        var shapes = new List<(string Title, Distribution D)>();
        if (orientation is not null)
        {
            shapes.Add(("Orientation", orientation));
        }

        if (aspect is not null)
        {
            shapes.Add(("Aspect ratio", aspect));
        }

        if (edges is not null)
        {
            shapes.Add(("Dominant lines", edges));
        }

        Grid(() =>
        {
            if (placement is not null)
            {
                Figure(shapes.Count > 0 ? "c-4" : "c-6", "Where the subject sits", "Share of frames with the main subject in each ninth of the frame",
                    () => Charts.Placement(_sb, placement, Charts.W4), () => MatrixTable(placement));
            }

            if (shapes.Count > 0)
            {
                var w = placement is null ? Charts.W12 : Charts.W8;
                Figure(placement is null ? "c-12" : "c-8", "Frame shape", Fmt.Int(Charts.Total(shapes[0].D)) + " captures", () =>
                {
                    foreach (var (title, d) in shapes)
                    {
                        _sb.Append("<p class=\"sl\">").AppendEsc(title).Append("</p>");
                        Charts.Stacked(_sb, BarItems(d, 6), w, title);
                    }
                }, () => GroupedTable(shapes));
            }
        });

        var scores = new (string Id, string Title)[]
        {
            (DistributionIds.Thirds, "Rule of thirds"),
            (DistributionIds.Center, "Centred subject"),
            (DistributionIds.Symmetry, "Symmetry"),
            (DistributionIds.NegativeSpace, "Negative space"),
            (DistributionIds.Separation, "Subject separation"),
            (DistributionIds.SubjectArea, "Subject size"),
        };
        Grid(() =>
        {
            if (tilt is not null)
            {
                var level = LevelShare(tilt);
                Figure("c-12", "Horizon tilt", SubFor(tilt) + " · frames where a horizon or strong line was found", () =>
                {
                    Charts.Columns(_sb, tilt, Charts.W12, 150);
                    if (level is { } l)
                    {
                        _sb.Append("<p class=\"stat\"><span>Level within ±1° <b>").Append(Fmt.Pct(l)).Append("</b></span></p>");
                    }
                }, () => DistTable(tilt));
            }

            foreach (var (id, title) in scores)
            {
                if (D(id) is { } d)
                {
                    SmallMultiple("c-4", d, title);
                }
            }
        });
        MoreGrid("More on composition", id => id.StartsWith("composition.", StringComparison.Ordinal), true, false);
    }

    private static double? LevelShare(Distribution d)
    {
        var total = Charts.Total(d);
        if (total <= 0 || !d.Bins.Any(b => b.Lower is not null && b.Upper is not null))
        {
            return null;
        }

        double level = 0;
        foreach (var b in d.Bins)
        {
            if (b.Lower is { } lo && b.Upper is { } hi && lo >= -1.0001 && hi <= 1.0001)
            {
                level += Charts.Share(b, total);
            }
        }

        return level;
    }

    private void GroupedTable(IReadOnlyList<(string Title, Distribution D)> groups)
    {
        _sb.Append("<thead><tr><th>Value</th><th class=\"r\">Captures</th><th class=\"r\">Share</th></tr></thead>");
        foreach (var (title, d) in groups)
        {
            var total = Charts.Total(d);
            _sb.Append("<tbody><tr class=\"grp\"><th colspan=\"3\">").AppendEsc(title).Append("</th></tr>");
            foreach (var b in d.Bins)
            {
                _sb.Append("<tr><td>").AppendEsc(b.Label).Append("</td><td class=\"r\">").Append(Charts.Count(b.Count)).Append("</td><td class=\"r\">")
                    .Append(Fmt.Pct(Charts.Share(b, total))).Append("</td></tr>");
            }

            _sb.Append("</tbody>");
        }
    }

    private void Look()
    {
        var palette = (_p.Palette ?? []).Where(c => c is not null).ToList();
        var hue = D(DistributionIds.Hue);
        var tone = D(DistributionIds.Tone);
        Grid(() =>
        {
            if (palette.Count > 0)
            {
                var start = _sb.Length;
                var ok = false;
                Figure("c-12", "Palette", "The colours that recur across your pictures, widths proportional to how much of the frame they cover",
                    () => ok = Charts.Palette(_sb, palette, Charts.W12),
                    () =>
                    {
                        _sb.Append("<thead><tr><th>Colour</th><th>Hex</th><th class=\"r\">Weight</th></tr></thead><tbody>");
                        foreach (var c in palette.OrderByDescending(c => c.Weight).ThenBy(c => c.Hex, StringComparer.Ordinal))
                        {
                            var name = ColorMath.NormalizeHex(c.Hex) is { } hex ? ColorName(hex) : "–";
                            _sb.Append("<tr><td>").AppendEsc(name).Append("</td><td><code>").AppendEsc(c.Hex).Append("</code></td><td class=\"r\">")
                                .Append(Fmt.Pct(c.Weight)).Append("</td></tr>");
                        }

                        _sb.Append("</tbody>");
                    });
                if (!ok)
                {
                    _sb.Length = start;
                }
            }

            if (hue is not null)
            {
                Figure(tone is null ? "c-12" : "c-6", "Hue", SubFor(hue) + " · dominant hue of colourful frames",
                    () => Charts.HueStrip(_sb, hue, tone is null ? Charts.W12 : Charts.W6), () => DistTable(hue));
            }

            if (tone is not null)
            {
                Figure(hue is null ? "c-12" : "c-6", "Tone", SubFor(tone) + " · from shadows to highlights",
                    () => Charts.Tone(_sb, tone, hue is null ? Charts.W12 : Charts.W6), () => DistTable(tone));
            }
        });

        var smalls = new (string Id, string Title)[]
        {
            (DistributionIds.ColorKey, "Key"),
            (DistributionIds.Monochrome, "Monochrome"),
            (DistributionIds.Warmth, "Warmth"),
            (DistributionIds.Colorfulness, "Colourfulness"),
        };
        Grid(() =>
        {
            foreach (var (id, title) in smalls)
            {
                if (D(id) is { } d)
                {
                    SmallMultiple("c-3", d, title);
                }
            }
        });
        MoreGrid("More on colour and tone", id => id.StartsWith("color.", StringComparison.Ordinal), true, false);
    }

    private static string ColorName(string hex)
    {
        var (l, c, h) = ColorMath.OkLchOf(hex);
        if (c < 0.03)
        {
            return l < 0.3 ? "near black" : l > 0.88 ? "near white" : l < 0.6 ? "dark grey" : "light grey";
        }

        var tone = l < 0.45 ? "deep " : l > 0.8 ? "pale " : c < 0.07 ? "muted " : string.Empty;
        return tone + Charts.HueName(h);
    }

    private void Time()
    {
        var timeline = D(DistributionIds.Timeline);
        var hw = M(MatrixIds.HourByWeekday);
        var year = D(DistributionIds.Year);
        var hour = D(DistributionIds.Hour);
        var weekday = D(DistributionIds.Weekday);
        var month = D(DistributionIds.Month);
        Grid(() =>
        {
            if (timeline is not null)
            {
                Figure("c-12", "Over time", SubFor(timeline) + " · captures per month · " + Fmt.DateRange(_lib.FirstCapture, _lib.LastCapture),
                    () => Charts.Timeline(_sb, timeline, Charts.W12, 180), () => DistTable(timeline, Fmt.MonthLabel));
            }

            if (hw is not null)
            {
                HeatmapFigure(year is null ? "c-12" : "c-8", hw, "Hour × weekday", "Captures by local hour of the day and day of the week",
                    year is null ? Charts.W12 : Charts.W8, new HeatmapOptions { MaxCellHeight = 24 });
            }

            if (year is not null)
            {
                Figure(hw is null ? "c-12" : "c-4", "By year", SubFor(year), () => Charts.Columns(_sb, year, hw is null ? Charts.W12 : Charts.W4, 200), () => DistTable(year));
            }
        });
        Grid(() =>
        {
            if (hour is not null)
            {
                SmallMultiple("c-4", hour, "Hour of day");
            }

            if (weekday is not null)
            {
                SmallMultiple("c-4", weekday, "Day of week");
            }

            if (month is not null)
            {
                SmallMultiple("c-4", month, "Month");
            }
        });
    }
}
