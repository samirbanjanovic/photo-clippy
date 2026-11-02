using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Light()
    {
        var isoShutter = M(MatrixIds.IsoByShutter);
        var ev = D(DistributionIds.Ev100);
        var iso = D(DistributionIds.Iso);
        var shutter = D(DistributionIds.Shutter);
        var bias = D(DistributionIds.ExposureBias);
        var program = D(DistributionIds.ExposureProgram);
        var flash = D(DistributionIds.Flash);
        var hasSignals = _sig.SampleCount > 0;
        Grid(() =>
        {
            if (isoShutter is not null)
            {
                HeatmapFigure(hasSignals || flash is not null ? "c-8" : "c-12", isoShutter, "ISO × shutter speed", "Captures per combination · darker means more",
                    hasSignals || flash is not null ? Charts.W8 : Charts.W12, new HeatmapOptions { MaxCellHeight = 24 });
            }

            if (hasSignals || flash is not null)
            {
                Figure(isoShutter is null ? "c-12" : "c-4", "In numbers", hasSignals ? Fmt.Int(_sig.SampleCount) + " captures with exposure data" : null, () =>
                {
                    if (hasSignals)
                    {
                        LightSignals();
                    }

                    if (flash is not null)
                    {
                        _sb.Append("<p class=\"sl\">Flash</p>");
                        Charts.Stacked(_sb, BarItems(flash, 6), isoShutter is null ? Charts.W12 : Charts.W4, "Flash");
                    }
                }, flash is null ? null : () => DistTable(flash));
            }

            if (ev is not null)
            {
                Figure("c-6", "Exposure value (EV100)", SubFor(ev) + " · lower is darker", () =>
                {
                    Charts.Columns(_sb, ev, Charts.W6, 180);
                    _sb.Append("<p class=\"stat\"><span>≤ 6 <b>indoors, dusk</b></span><span>9–12 <b>shade, overcast</b></span><span>≥ 14 <b>bright sun</b></span></p>");
                }, () => DistTable(ev));
            }

            if (iso is not null)
            {
                Figure(ev is null ? "c-12" : "c-6", "ISO", SubFor(iso), () => Charts.Columns(_sb, iso, ev is null ? Charts.W12 : Charts.W6, 180), () => DistTable(iso));
            }

            if (shutter is not null)
            {
                SmallMultiple("c-4", shutter, "Shutter speed");
            }

            if (bias is not null)
            {
                SmallMultiple("c-4", bias, "Exposure compensation");
            }

            if (program is not null)
            {
                SmallMultiple("c-4", program, "Exposure program");
            }
        });
        MoreGrid("More from the metadata", id => id.StartsWith("metadata.", StringComparison.Ordinal), false, false);
    }

    private void LightSignals()
    {
        _sb.Append("<dl class=\"kv\">");
        if (_sig.MedianEv100 is { } ev && double.IsFinite(ev))
        {
            Kv("Typical light", Fmt.Ev(ev) + " · " + LightWord(ev));
        }

        if (_sig.MedianIso is { } iso && double.IsFinite(iso))
        {
            Kv("Median ISO", Fmt.Int(iso));
        }

        if (_sig.MedianFNumber is { } f && double.IsFinite(f))
        {
            Kv("Median aperture", Fmt.FNum(f));
        }

        Kv("High ISO", Fmt.Pct(_sig.HighIsoShare));
        Kv("Slow shutter", Fmt.Pct(_sig.SlowShutterShare));
        Kv("Light-starved", Fmt.Pct(_sig.LightStarvedShare));
        Kv("Flash fired", Fmt.Pct(_sig.FlashShare));
        _sb.Append("</dl><p class=\"note\">Light-starved: wide open and at high ISO or a slow shutter at the same time — the frames a faster lens would help most.</p>");
    }

    private void Kv(string key, string value) => _sb.Append("<dt>").AppendEsc(key).Append("</dt><dd>").AppendEsc(value).Append("</dd>");

    private static string LightWord(double ev) => ev switch
    {
        >= 14 => "bright sun",
        >= 12 => "daylight",
        >= 9 => "shade or overcast",
        >= 6 => "bright interiors, dusk",
        >= 3 => "interiors, evening",
        _ => "night",
    };

    private void Subjects()
    {
        var genre = D(DistributionIds.Genre);
        var group = D(DistributionIds.GenreGroup);
        var gf = M(MatrixIds.GenreByFocal);
        var lg = M(MatrixIds.LensByGenre);
        Grid(() =>
        {
            if (genre is not null)
            {
                var items = BarItems(genre, 12, b => GenreName(b.Key ?? b.Label));
                Figure(group is null ? "c-12" : "c-6", "Subjects", SubFor(genre) + " · one estimated subject per capture",
                    () => Charts.BarList(_sb, items, group is null ? Charts.W12 : Charts.W6, "Subjects"), () => DistTable(genre, l => GenreName(l)), GenreNote());
            }

            if (group is not null)
            {
                var items = BarItems(group, 10, b => GenreName(b.Key ?? b.Label));
                Figure(genre is null ? "c-12" : "c-6", "Subject groups", SubFor(group),
                    () => Charts.BarList(_sb, items, genre is null ? Charts.W12 : Charts.W6, "Subject groups"), () => DistTable(group, l => GenreName(l)));
            }

            if (gf is not null)
            {
                HeatmapFigure("c-12", gf, "Focal length by subject", "Each row is normalised to its own total · 35 mm-equivalent", Charts.W12,
                    new HeatmapOptions { RowNormalize = true, MaxCellHeight = 22, YLabel = GenreName });
            }

            if (lg is not null)
            {
                HeatmapFigure("c-12", lg, "Lens by subject", "Captures per lens and subject · darker means more", Charts.W12,
                    new HeatmapOptions { MaxCellHeight = 22, XLabel = GenreName });
            }
        });
    }

    private string? GenreNote()
    {
        var src = string.IsNullOrWhiteSpace(_q.GenreSource) || _q.GenreSource == "none" ? null : _q.GenreSource;
        if (src is null && _q.MeanGenreConfidence is null)
        {
            return "Subjects are estimated automatically and can be wrong for individual frames; treat the shares as a tendency.";
        }

        var s = "Subjects estimated by " + (src ?? "an automatic classifier");
        if (_q.MeanGenreConfidence is { } c && double.IsFinite(c))
        {
            s += " with a mean confidence of " + Fmt.Pct(c);
        }

        return s + ". Individual frames can be wrong; treat the shares as a tendency.";
    }
}
