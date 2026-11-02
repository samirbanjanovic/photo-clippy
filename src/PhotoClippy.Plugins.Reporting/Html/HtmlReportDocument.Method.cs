using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal sealed partial class HtmlReportDocument
{
    private void Method()
    {
        var run = _m.Run;
        var deviceDist = D(DistributionIds.DeviceClass);
        Grid(() =>
        {
            Figure("c-6", "Library", LibrarySub(), () =>
            {
                _sb.Append("<dl class=\"kv\">");
                Kv("Folders scanned", Plural(_lib.Roots.Count, "folder", "folders"));
                Kv("Files seen", Fmt.Int(_lib.FilesSeen));
                Kv("Files with metadata", Fmt.Int(_lib.FilesWithMetadata));
                Kv("Captures", Fmt.Int(_lib.Captures) + (_lib.RawPlusJpeg > 0 ? " · " + Fmt.Int(_lib.RawPlusJpeg) + " RAW+JPEG pairs merged" : string.Empty));
                Kv("In scope", Fmt.Int(_lib.CapturesInScope) + " · " + ScopeText(_lib.Scope));
                Kv("Frames analysed", Fmt.Int(_lib.FramesAnalyzed));
                Kv("Date range", Fmt.DateRange(_lib.FirstCapture, _lib.LastCapture));
                if (_lib.TotalBytes > 0)
                {
                    Kv("Total size", Fmt.Bytes(_lib.TotalBytes));
                }

                _sb.Append("</dl>");
                var roots = _lib.Roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
                if (roots.Count > 0)
                {
                    _sb.Append("<p class=\"sl\">Folders</p><ul class=\"ev small\">");
                    foreach (var r in roots.Take(20))
                    {
                        _sb.Append("<li><code>").AppendEsc(r).Append("</code></li>");
                    }

                    if (roots.Count > 20)
                    {
                        _sb.Append("<li>and ").Append(Fmt.Int(roots.Count - 20)).Append(" more</li>");
                    }

                    _sb.Append("</ul>");
                }
            });
            QualityFigure();
            CountsFigure("c-6", "File kinds", "Files by kind", _lib.FileKinds, k => k);
            if (deviceDist is not null)
            {
                Figure("c-6", "Device classes", SubFor(deviceDist), () => Charts.BarList(_sb, BarItems(deviceDist), Charts.W6, deviceDist.Title), () => DistTable(deviceDist));
            }
            else
            {
                CountsFigure("c-6", "Device classes", "Captures by kind of camera", _lib.DeviceClasses, DeviceLabel);
            }
        });

        Grid(() =>
        {
            Figure("c-6", "This run", run.ToolVersion is { Length: > 0 } v ? "photo-clippy " + v : null, () =>
            {
                _sb.Append("<dl class=\"kv\">");
                Kv("Run", run.RunId);
                Kv("Started", Fmt.Utc(run.StartedUtc));
                if (run.FinishedUtc > run.StartedUtc)
                {
                    Kv("Finished", Fmt.Utc(run.FinishedUtc));
                    Kv("Duration", Fmt.Seconds((run.FinishedUtc - run.StartedUtc).TotalSeconds));
                }

                if (!string.IsNullOrWhiteSpace(run.Host))
                {
                    Kv("Host", run.Host);
                }

                _sb.Append("</dl>");
                var options = (run.Options ?? new Dictionary<string, string>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                if (options.Count > 0)
                {
                    _sb.Append("<p class=\"sl\">Options</p><dl class=\"kv small\">");
                    foreach (var (k, val) in options)
                    {
                        _sb.Append("<dt><code>").AppendEsc(k).Append("</code></dt><dd>").AppendEsc(val ?? string.Empty).Append("</dd>");
                    }

                    _sb.Append("</dl>");
                }
            });

            var timings = (run.Timings ?? new Dictionary<string, double>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value) && kv.Value >= 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (timings.Count > 0)
            {
                var total = timings.Sum(t => t.Value);
                var items = timings.Select(t => new BarItem(t.Key, t.Value, total > 0 ? t.Value / total : 0, Title: t.Key + ": " + Fmt.Seconds(t.Value), Value: Fmt.Seconds(t.Value))).ToList();
                Figure("c-6", "Timings", "Seconds per stage · " + Fmt.Seconds(total) + " in total", () => Charts.BarList(_sb, items, Charts.W6, "Timings"), () =>
                {
                    _sb.Append("<thead><tr><th>Stage</th><th class=\"r\">Seconds</th></tr></thead><tbody>");
                    foreach (var (k, s) in timings)
                    {
                        _sb.Append("<tr><td>").AppendEsc(k).Append("</td><td class=\"r\">").Append(Fmt.Num(s, "0.###")).Append("</td></tr>");
                    }

                    _sb.Append("</tbody>");
                });
            }

            var plugins = (run.Plugins ?? []).Where(p => p is not null).OrderBy(p => p.Kind).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            if (plugins.Count > 0)
            {
                Figure("c-12", "Plugins", Plural(plugins.Count, "plugin", "plugins") + " took part in this run", () =>
                {
                    _sb.Append("<div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Plugin</th><th>Kind</th><th>Contract</th><th>Settings</th><th>Capabilities</th></tr></thead><tbody>");
                    foreach (var p in plugins)
                    {
                        _sb.Append("<tr><td class=\"name\"><code>").AppendEsc(p.Id).Append('@').AppendEsc(p.Version).Append("</code><span class=\"dim\">").AppendEsc(p.Name)
                            .Append("</span></td><td>").Append(p.Kind.ToString()).Append("</td><td>").AppendEsc(p.ContractVersion).Append("</td><td><code>")
                            .AppendEsc(string.IsNullOrWhiteSpace(p.Fingerprint) ? "–" : p.Fingerprint).Append("</code></td><td>")
                            .AppendEsc(p.Capabilities is { Count: > 0 } c ? string.Join(", ", c.Order(StringComparer.Ordinal)) : "–").Append("</td></tr>");
                    }

                    _sb.Append("</tbody></table></div>");
                });
            }
        });

        MoreGrid("More data", _ => true, true, true);
    }

    private string LibrarySub()
    {
        var parts = new List<string> { Plural(_lib.CapturesInScope, "capture", "captures") + " in scope" };
        if (_lib.FirstCapture is not null || _lib.LastCapture is not null)
        {
            parts.Add(Fmt.DateRange(_lib.FirstCapture, _lib.LastCapture));
        }

        return string.Join(" · ", parts);
    }

    private static string ScopeText(string? scope) => scope switch
    {
        "interchangeable" => "interchangeable-lens cameras only",
        "all" => "all cameras, phones included",
        null or "" => "–",
        _ => scope,
    };

    private static string DeviceLabel(string key) => Enum.TryParse<DeviceClass>(key, true, out var dc) ? DeviceName(dc) : key;

    private void CountsFigure(string cls, string title, string sub, IReadOnlyDictionary<string, int>? counts, Func<string, string> label)
    {
        var list = (counts ?? new Dictionary<string, int>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return;
        }

        double total = list.Sum(kv => (double)kv.Value);
        var items = list.Select(kv => new BarItem(label(kv.Key), kv.Value, kv.Value / total)).ToList();
        Figure(cls, title, sub + " · " + Fmt.Int(total) + " in total", () => Charts.BarList(_sb, items, Charts.W6, title), () => BarTable(items, title));
    }

    private void QualityFigure()
    {
        var q = _q;
        var basis = _lib.CapturesInScope > 0 ? _lib.CapturesInScope : _lib.Captures;
        var rows = new (string Label, int Count)[]
        {
            ("Focal length missing", q.MissingFocal),
            ("Aperture missing", q.MissingAperture),
            ("ISO missing", q.MissingIso),
            ("Shutter speed missing", q.MissingShutter),
            ("Lens unknown", q.MissingLens),
            ("Date missing", q.MissingDate),
            ("Body unknown", q.MissingBody),
            ("Metadata errors", q.MetadataErrors),
            ("Could not decode", q.DecodeFailed),
            ("Not decodable (format)", q.NotDecodable),
            ("Excluded as junk", q.JunkExcluded),
        };
        Figure("c-6", "Data quality", basis > 0 ? "Counts out of " + Fmt.Int(basis) + " captures" : "Counts", () =>
        {
            _sb.Append("<div class=\"ovx\"><table class=\"ledger\"><thead><tr><th>Check</th><th class=\"r\">Count</th><th class=\"r\">Share</th></tr></thead><tbody>");
            foreach (var (label, count) in rows)
            {
                var share = basis > 0 ? (double)Math.Max(count, 0) / basis : 0;
                _sb.Append("<tr><td>").AppendEsc(label).Append("</td><td class=\"r\">").Append(Fmt.Int(count)).Append("</td><td class=\"r nw")
                    .Append(share >= 0.25 ? " hot" : string.Empty).Append("\">").Append(basis > 0 ? Fmt.Pct(share) : "–").Append("</td></tr>");
            }

            _sb.Append("</tbody></table></div><dl class=\"kv small\">");
            Kv("Subject labels", GenreSourceText(q.GenreSource));
            if (q.MeanGenreConfidence is { } c && double.IsFinite(c))
            {
                Kv("Mean label confidence", Fmt.Pct(Clamp01(c)));
            }

            _sb.Append("</dl>");
            var notes = (q.Notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (notes.Count > 0)
            {
                _sb.Append("<ul class=\"ev small\">");
                foreach (var n in notes)
                {
                    _sb.Append("<li>").AppendEsc(n).Append("</li>");
                }

                _sb.Append("</ul>");
            }
        }, note: "Shares of 25 % or more are marked; charts that depend on a missing field simply leave those captures out.");
    }

    private static string GenreSourceText(string? source) => source switch
    {
        null or "" or "none" => "none — no subject classifier ran",
        _ => source,
    };
}
