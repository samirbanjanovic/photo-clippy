using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>
/// Writes four RFC 4180 CSV files (UTF-8 without BOM, invariant culture): <c>&lt;base&gt;-lenses.csv</c>, <c>-bodies.csv</c>,
/// <c>-distributions.csv</c> (long format, profile distributions then extensions) and <c>-recommendations.csv</c>.
/// Files are always written, with only a header row when there is no data.
/// </summary>
public sealed class CsvReportRenderer : IReportRenderer
{
    public const string PluginId = "builtin.report.csv";
    public const string PluginVersion = "1.0.0";

    private const string ListSeparator = " | ";

    public CsvReportRenderer()
    {
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "CSV export",
            Version = PluginVersion,
            Kind = ComponentKind.Report,
            Description = "Lens, body, distribution and recommendation tables as RFC 4180 CSV for spreadsheets and notebooks.",
            Capabilities = ["report.csv", "report.csv.lenses", "report.csv.bodies", "report.csv.distributions", "report.csv.recommendations"],
            Fingerprint = RenderIo.Fingerprint("csv-1;rfc4180;crlf;invariant"),
        };
    }

    public PluginManifest Manifest { get; }

    public string Format => "csv";

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(PluginHealth.Ok());

    public async Task<RenderResult> RenderAsync(ReportModel model, RenderTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dir = RenderIo.PrepareDirectory(target);
        var baseName = RenderIo.BaseName(target);
        var tables = new (string Suffix, CsvTable Table)[]
        {
            ("lenses", Lenses(model.Profile)),
            ("bodies", Bodies(model.Profile)),
            ("distributions", Distributions(model.Profile)),
            ("recommendations", Recommendations(model.Recommendation)),
        };

        var files = new List<string>(tables.Length);
        foreach (var (suffix, table) in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, baseName + "-" + suffix + ".csv");
            await RenderIo.WriteTextAsync(path, table.ToString(), cancellationToken).ConfigureAwait(false);
            files.Add(path);
        }

        return new RenderResult { Files = files };
    }

    internal static CsvTable Lenses(ShootingProfile? profile)
    {
        var t = new CsvTable(
        [
            "key", "display", "make", "is_zoom", "is_fixed", "focal_min_mm", "focal_max_mm", "focal_min_35", "focal_max_35",
            "max_aperture_wide", "max_aperture_tele", "spec_source", "captures", "share", "median_focal_35", "median_f_number", "median_iso",
            "wide_open_share", "wide_end_share", "tele_end_share", "light_starved_share", "top_genre", "bodies", "first", "last",
        ]);
        foreach (var l in (profile?.Lenses ?? []).Where(l => l is not null).OrderByDescending(l => l.Captures).ThenBy(l => l.Key, StringComparer.Ordinal))
        {
            t.Row(
            [
                l.Key, l.Display, l.Make, CsvTable.Bool(l.IsZoom), CsvTable.Bool(l.IsFixed), CsvTable.Num(l.FocalMinMm), CsvTable.Num(l.FocalMaxMm),
                CsvTable.Num(l.FocalMin35), CsvTable.Num(l.FocalMax35), CsvTable.Num(l.MaxApertureWide), CsvTable.Num(l.MaxApertureTele), l.SpecSource.ToString(),
                CsvTable.Int(l.Captures), CsvTable.Num(l.Share), CsvTable.Num(l.MedianFocal35), CsvTable.Num(l.MedianFNumber), CsvTable.Num(l.MedianIso),
                CsvTable.Num(l.WideOpenShare), CsvTable.Num(l.WideEndShare), CsvTable.Num(l.TeleEndShare), CsvTable.Num(l.LightStarvedShare), l.TopGenre,
                string.Join(';', (l.Bodies ?? []).Where(b => !string.IsNullOrEmpty(b))), CsvTable.Date(l.First), CsvTable.Date(l.Last),
            ]);
        }

        return t;
    }

    internal static CsvTable Bodies(ShootingProfile? profile)
    {
        var t = new CsvTable(["key", "display", "make", "format", "crop_factor", "device", "captures", "share", "first", "last"]);
        foreach (var b in (profile?.Bodies ?? []).Where(b => b is not null).OrderByDescending(b => b.Captures).ThenBy(b => b.Key, StringComparer.Ordinal))
        {
            t.Row(
            [
                b.Key, b.Display, b.Make, b.Format, CsvTable.Num(b.CropFactor), b.Device.ToString(), CsvTable.Int(b.Captures), CsvTable.Num(b.Share),
                CsvTable.Date(b.First), CsvTable.Date(b.Last),
            ]);
        }

        return t;
    }

    internal static CsvTable Distributions(ShootingProfile? profile)
    {
        var t = new CsvTable(["distribution_id", "title", "unit", "label", "key", "lower", "upper", "count", "share"]);
        foreach (var source in new[] { profile?.Distributions, profile?.Extensions })
        {
            foreach (var (key, d) in (source ?? new Dictionary<string, Distribution>()).Where(kv => kv.Key is not null && kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var id = string.IsNullOrEmpty(d.Id) ? key : d.Id;
                foreach (var bin in (d.Bins ?? []).Where(b => b is not null))
                {
                    t.Row([id, d.Title, d.Unit, bin.Label, bin.Key, CsvTable.Num(bin.Lower), CsvTable.Num(bin.Upper), CsvTable.Num(bin.Count), CsvTable.Num(bin.Share)]);
                }
            }
        }

        return t;
    }

    internal static CsvTable Recommendations(RecommendationSet? set)
    {
        var ranked = (set?.Ranked ?? []).Where(r => r?.Lens is not null).OrderBy(r => r.Rank).ThenBy(r => r.Lens.Id, StringComparer.Ordinal).ToList();
        var criteria = new List<string>();
        foreach (var id in (set?.Criteria ?? []).Where(c => c is not null).Select(c => c.Id)
                     .Concat(ranked.SelectMany(r => (r.Criteria ?? new Dictionary<string, double>()).Keys).Order(StringComparer.Ordinal)))
        {
            if (!string.IsNullOrEmpty(id) && !criteria.Contains(id, StringComparer.Ordinal))
            {
                criteria.Add(id);
            }
        }

        string[] fixedColumns =
        [
            "rank", "lens_id", "name", "source", "focal_min_mm", "focal_max_mm", "max_aperture_wide", "max_aperture_tele", "weight_g", "price_usd",
            "score", "score_low", "score_high", "probability_best", "redundancy", "redundant_with", "already_owned", "verdict",
        ];
        string[] tailColumns = ["evidence", "trade_offs", "wins_instead_when", "preset", "recommender_id"];
        var t = new CsvTable([.. fixedColumns, .. criteria.Select(c => "criterion_" + c), .. tailColumns]);
        foreach (var r in ranked)
        {
            var l = r.Lens;
            var row = new List<string?>
            {
                CsvTable.Int(r.Rank), l.Id, l.Name, l.Source.ToString(), CsvTable.Num(l.FocalMinMm), CsvTable.Num(l.FocalMaxMm), CsvTable.Num(l.MaxApertureWide),
                CsvTable.Num(l.MaxApertureTele), CsvTable.Num(l.WeightG), CsvTable.Num(l.PriceUsd), CsvTable.Num(r.Score), CsvTable.Num(r.ScoreLow),
                CsvTable.Num(r.ScoreHigh), CsvTable.Num(r.ProbabilityBest), CsvTable.Num(r.Redundancy), r.RedundantWith, CsvTable.Bool(r.AlreadyOwned), r.Verdict,
            };
            foreach (var c in criteria)
            {
                row.Add(r.Criteria is not null && r.Criteria.TryGetValue(c, out var v) ? CsvTable.Num(v) : string.Empty);
            }

            row.Add(Join(r.Evidence));
            row.Add(Join(r.TradeOffs));
            row.Add(Join(r.WinsInsteadWhen));
            row.Add(set!.Preset);
            row.Add(set.RecommenderId);
            t.Row(row);
        }

        return t;
    }

    private static string Join(IReadOnlyList<string>? items) => string.Join(ListSeparator, (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)));
}
