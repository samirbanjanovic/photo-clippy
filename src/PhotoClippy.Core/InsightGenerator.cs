using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core;

/// <summary>Rule-based, human-readable findings for each report section. Deterministic; no LLM involved.</summary>
public static class InsightGenerator
{
    public static IReadOnlyList<Insight> Generate(ShootingProfile profile, RecommendationSet? recommendation, GenreTaxonomy taxonomy)
    {
        var list = new List<Insight>();
        Overview(profile, list);
        Gear(profile, list);
        Focal(profile, list);
        Aperture(profile, list);
        Light(profile, list);
        Subjects(profile, taxonomy, list);
        Composition(profile, list);
        Color(profile, list);
        Time(profile, list);
        Gaps(profile, list);
        Recommendation(recommendation, list);
        Method(profile, list);
        return list;
    }

    private static string Pct(double share) => (share * 100).ToString(share < 0.1 && share > 0 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static string Num(double value, string format = "0") => value.ToString(format, CultureInfo.InvariantCulture);

    private static Insight I(string section, string title, string text, string? metric = null, InsightTone tone = InsightTone.Neutral) =>
        new() { Section = section, Title = title, Text = text, Metric = metric, Tone = tone };

    private static Distribution? D(ShootingProfile p, string id) => p.Distributions.GetValueOrDefault(id);

    private static Bin? Top(Distribution? d, Func<Bin, bool>? filter = null) =>
        d?.Bins.Where(b => filter?.Invoke(b) ?? true).OrderByDescending(b => b.Count).FirstOrDefault();

    private static void Overview(ShootingProfile p, List<Insight> list)
    {
        var lib = p.Library;
        if (lib.Captures == 0)
        {
            list.Add(I(ReportSections.Overview, "No photos found", "No readable photos were found under the given folders.", null, InsightTone.Attention));
            return;
        }

        var span = lib.FirstCapture is { } first && lib.LastCapture is { } last ? $" between {first:MMM yyyy} and {last:MMM yyyy}" : string.Empty;
        list.Add(I(ReportSections.Overview, "Library", $"{Num(lib.Captures, "N0")} captures from {Num(lib.FilesSeen, "N0")} files{span}; {Num(lib.CapturesInScope, "N0")} are included in the shooting statistics ({(lib.Scope == "all" ? "all devices" : "interchangeable-lens cameras")}).", Num(lib.CapturesInScope, "N0")));
        if (lib.RawPlusJpeg > 0)
        {
            list.Add(I(ReportSections.Overview, "RAW + JPEG", $"{Num(lib.RawPlusJpeg, "N0")} RAW+JPEG pairs were merged so each shutter press counts once.", Num(lib.RawPlusJpeg, "N0")));
        }

        if (p.Target is { } target)
        {
            list.Add(I(ReportSections.Overview, "Target system", $"Recommendations are made for the {target.BodyDisplay} ({FormatName(target.Format)}, crop {Num(target.CropFactor, "0.0#")}×).", target.BodyDisplay));
        }
    }

    private static string FormatName(string format) => Normalization.SensorFormats.Display(format);

    private static void Gear(ShootingProfile p, List<Insight> list)
    {
        var lenses = p.Lenses.Where(l => !l.IsFixed && !l.Key.StartsWith(Normalization.CaptureBuilder.UnknownLensPrefix, StringComparison.Ordinal)).ToList();
        if (lenses.Count == 0)
        {
            return;
        }

        var total = lenses.Sum(l => l.Captures);
        var top = lenses[0];
        var share = total > 0 ? (double)top.Captures / total : 0;
        list.Add(I(ReportSections.Gear, "Workhorse lens", $"The {top.Display} took {Pct(share)} of your interchangeable-lens photos{(top.TopGenre is null ? string.Empty : $", mostly {top.TopGenre.ToLowerInvariant()}")}.", Pct(share), share > 0.6 ? InsightTone.Attention : InsightTone.Neutral));

        if (p.Signals.PrimeShare > 0 || p.Signals.ZoomShare > 0)
        {
            list.Add(I(ReportSections.Gear, "Primes vs zooms", $"{Pct(p.Signals.PrimeShare)} of frames were shot on primes and {Pct(p.Signals.ZoomShare)} on zooms.", Pct(p.Signals.PrimeShare)));
        }

        var idle = lenses.Where(l => total > 0 && (double)l.Captures / total < 0.03 && l.Captures > 0).Select(l => l.Display).ToList();
        if (idle.Count > 0 && lenses.Count > 2)
        {
            list.Add(I(ReportSections.Gear, "Rarely used", $"{string.Join(", ", idle.Take(3))} {(idle.Count == 1 ? "accounts" : "account")} for under 3% of frames each — candidates to sell or trade.", idle.Count.ToString(CultureInfo.InvariantCulture), InsightTone.Attention));
        }
    }

    private static void Focal(ShootingProfile p, List<Insight> list)
    {
        if (p.FocalPeaks.Count > 0)
        {
            var peaks = p.FocalPeaks.Take(3).Select(pk => $"{Num(pk.Focal35Mm)}mm ({Pct(pk.Share)})");
            list.Add(I(ReportSections.Focal, "Favourite focal lengths", $"Your shots cluster around {string.Join(", ", peaks)} (35 mm equivalent).", $"{Num(p.FocalPeaks[0].Focal35Mm)}mm", InsightTone.Positive));
        }

        if (p.Signals.FocalSpreadStops is { } spread && p.Signals.MedianFocal35 is { } median)
        {
            var text = spread < 0.6
                ? $"Half of your frames sit within {Num(spread, "0.0")} stops of focal length around {Num(median)}mm — a single prime would cover most of your shooting."
                : spread > 2
                    ? $"Your middle 50% of frames span {Num(spread, "0.0")} stops of focal length (median {Num(median)}mm): you use a wide range, which favours zooms or a two-prime kit."
                    : $"Your middle 50% of frames span {Num(spread, "0.0")} stops of focal length around a median of {Num(median)}mm.";
            list.Add(I(ReportSections.Focal, "Focal range", text, $"{Num(median)}mm"));
        }

        if (p.Signals.WideEndShare >= 0.3)
        {
            list.Add(I(ReportSections.Focal, "Wide end", $"{Pct(p.Signals.WideEndShare)} of zoom frames are at the widest setting — you often want to go wider.", Pct(p.Signals.WideEndShare), InsightTone.Attention));
        }

        if (p.Signals.TeleEndShare >= 0.3)
        {
            list.Add(I(ReportSections.Focal, "Long end", $"{Pct(p.Signals.TeleEndShare)} of zoom frames are at the longest setting — you often want more reach.", Pct(p.Signals.TeleEndShare), InsightTone.Attention));
        }
    }

    private static void Aperture(ShootingProfile p, List<Insight> list)
    {
        if (p.Signals.MedianFNumber is { } fn)
        {
            var top = Top(D(p, DistributionIds.Aperture));
            list.Add(I(ReportSections.Aperture, "Typical aperture", $"Median aperture is f/{Num(fn, "0.0#")}{(top is null ? string.Empty : $"; the single most used setting is {top.Label} ({Pct(top.Share)})")}.", $"f/{Num(fn, "0.0#")}"));
        }

        if (p.Signals.ApertureCeilingShare > 0)
        {
            var share = p.Signals.ApertureCeilingShare;
            list.Add(I(ReportSections.Aperture, "At the ceiling", $"{Pct(share)} of frames were shot within ⅓ stop of the lens' maximum aperture.{(share >= 0.4 ? " A faster lens would give you light or blur you are already reaching for." : string.Empty)}", Pct(share), share >= 0.4 ? InsightTone.Attention : InsightTone.Neutral));
        }
    }

    private static void Light(ShootingProfile p, List<Insight> list)
    {
        var s = p.Signals;
        if (s.MedianEv100 is { } ev)
        {
            var where = ev < 6 ? "mostly dim interiors and night" : ev < 10 ? "often indoor or evening light" : ev < 13 ? "a mix of overcast and open shade" : "mostly bright daylight";
            list.Add(I(ReportSections.Light, "Typical light", $"Median scene brightness is EV {Num(ev, "0.#")} — {where}.", $"EV {Num(ev, "0.#")}"));
        }

        if (s.LightStarvedShare > 0)
        {
            list.Add(I(ReportSections.Light, "Light-starved", $"{Pct(s.LightStarvedShare)} of frames were wide open and still needed high ISO or a slow shutter.", Pct(s.LightStarvedShare), s.LightStarvedShare >= 0.15 ? InsightTone.Attention : InsightTone.Neutral));
        }

        if (s.HighIsoShare > 0)
        {
            list.Add(I(ReportSections.Light, "High ISO", $"{Pct(s.HighIsoShare)} of frames used high ISO{(s.MedianIso is { } iso ? $"; median ISO is {Num(iso)}" : string.Empty)}.", Pct(s.HighIsoShare), s.HighIsoShare >= 0.25 ? InsightTone.Attention : InsightTone.Neutral));
        }

        if (s.SlowShutterShare > 0.1)
        {
            list.Add(I(ReportSections.Light, "Slow shutter", $"{Pct(s.SlowShutterShare)} of frames were slower than 1/focal-length — stabilisation or a brighter lens helps here.", Pct(s.SlowShutterShare), InsightTone.Attention));
        }
    }

    private static void Subjects(ShootingProfile p, GenreTaxonomy taxonomy, List<Insight> list)
    {
        var genres = D(p, DistributionIds.Genre);
        var top = Top(genres, b => b.Key != "unclassified");
        if (top is null)
        {
            return;
        }

        var second = genres!.Bins.Where(b => b.Key != "unclassified" && b.Key != top.Key).OrderByDescending(b => b.Count).FirstOrDefault();
        list.Add(I(ReportSections.Subjects, "What you shoot", $"{top.Label} is your most common subject ({Pct(top.Share)}){(second is null ? string.Empty : $", followed by {second.Label.ToLowerInvariant()} ({Pct(second.Share)})")}.", Pct(top.Share), InsightTone.Positive));

        if (p.Matrices.TryGetValue(MatrixIds.GenreByFocal, out var m) && top.Key is { } key)
        {
            var row = m.YLabels.ToList().IndexOf(top.Label);
            if (row >= 0 && m.Values[row].Sum() > 0)
            {
                var values = m.Values[row];
                var best = Enumerable.Range(0, values.Count).OrderByDescending(i => values[i]).First();
                var affinity = taxonomy.Find(key)?.Affinity;
                var hint = affinity is null ? string.Empty : $" Typical for the genre: {Num(affinity.FocalMin35)}–{Num(affinity.FocalMax35)}mm.";
                list.Add(I(ReportSections.Subjects, $"{top.Label} focal length", $"You shoot {top.Label.ToLowerInvariant()} most often at {m.XLabels[best]}.{hint}", m.XLabels[best]));
            }
        }
    }

    private static void Composition(ShootingProfile p, List<Insight> list)
    {
        var thirds = D(p, DistributionIds.Thirds)?.Stats?.Median;
        var center = D(p, DistributionIds.Center)?.Stats?.Median;
        if (thirds is { } t && center is { } c)
        {
            var text = t > c + 0.1 ? "You tend to place subjects on rule-of-thirds power points." : c > t + 0.1 ? "You tend to centre your subjects." : "Your subject placement mixes centred and off-centre framing.";
            list.Add(I(ReportSections.Composition, "Placement", text, null));
        }

        if (p.Signals.MedianSeparation is { } sep)
        {
            list.Add(I(ReportSections.Composition, "Subject separation", sep > 0.55 ? "Subjects are usually well separated from the background (shallow depth of field)." : sep < 0.3 ? "Most frames have deep focus with little subject–background separation." : "Subject separation is moderate.", Num(sep, "0.00")));
        }

        var tilt = D(p, DistributionIds.HorizonTilt);
        if (tilt is { Total: > 0 })
        {
            var level = tilt.Bins.Where(b => b.Label == "0°").Sum(b => b.Share);
            if (level < 0.7)
            {
                list.Add(I(ReportSections.Composition, "Horizons", $"Only {Pct(level)} of detected horizons are within ½° of level.", Pct(level), InsightTone.Attention));
            }
        }
    }

    private static void Color(ShootingProfile p, List<Insight> list)
    {
        var mono = D(p, DistributionIds.Monochrome)?.Bins.FirstOrDefault(b => b.Key == "mono");
        if (mono is { Share: > 0.05 })
        {
            list.Add(I(ReportSections.Color, "Monochrome", $"{Pct(mono.Share)} of your images are black and white.", Pct(mono.Share)));
        }

        var key = Top(D(p, DistributionIds.ColorKey));
        if (key is not null)
        {
            list.Add(I(ReportSections.Color, "Tonal key", $"{key.Label} images dominate ({Pct(key.Share)}).", Pct(key.Share)));
        }

        if (D(p, DistributionIds.Warmth)?.Stats?.Median is { } warmth && Math.Abs(warmth) > 0.08)
        {
            list.Add(I(ReportSections.Color, "Colour temperature", warmth > 0 ? "Your images lean warm." : "Your images lean cool.", Num(warmth, "+0.00;-0.00")));
        }
    }

    private static void Time(ShootingProfile p, List<Insight> list)
    {
        var hour = Top(D(p, DistributionIds.Hour));
        var weekday = Top(D(p, DistributionIds.Weekday));
        if (hour is not null && weekday is not null)
        {
            list.Add(I(ReportSections.Time, "When you shoot", $"Your busiest hour is {hour.Label}:00 and your busiest day is {weekday.Label}.", $"{hour.Label}:00"));
        }

        var weekend = D(p, DistributionIds.Weekday)?.Bins.Where(b => b.Key is "Sat" or "Sun").Sum(b => b.Share);
        if (weekend is > 0.5)
        {
            list.Add(I(ReportSections.Time, "Weekend shooter", $"{Pct(weekend.Value)} of your photos are taken on weekends.", Pct(weekend.Value)));
        }

        var year = Top(D(p, DistributionIds.Year));
        if (year is not null)
        {
            list.Add(I(ReportSections.Time, "Most productive year", $"{year.Label} was your most productive year ({Num(year.Count, "N0")} captures).", year.Label));
        }
    }

    private static void Gaps(ShootingProfile p, List<Insight> list)
    {
        var target = p.Target;
        if (target is null || p.FocalPeaks.Count == 0)
        {
            return;
        }

        var owned = p.Lenses.Where(l => !l.IsFixed && l.FocalMin35 is not null && l.FocalMax35 is not null && l.Bodies.Contains(target.BodyDisplay ?? string.Empty)).ToList();
        if (owned.Count == 0)
        {
            return;
        }

        var covered = (double f) => owned.Any(l => f >= l.FocalMin35!.Value * 0.97 && f <= l.FocalMax35!.Value * 1.03);
        foreach (var peak in p.FocalPeaks.Where(pk => !covered(pk.Focal35Mm)).Take(2))
        {
            list.Add(I(ReportSections.Gaps, "Uncovered focal length", $"{Pct(peak.Share)} of your frames cluster around {Num(peak.Focal35Mm)}mm, which none of your current {target.BodyDisplay} lenses cover — probably shot on another camera.", $"{Num(peak.Focal35Mm)}mm", InsightTone.Attention));
        }

        var minOwned = owned.Min(l => l.FocalMin35!.Value);
        var maxOwned = owned.Max(l => l.FocalMax35!.Value);
        if (p.Signals.WideEndShare >= 0.3)
        {
            list.Add(I(ReportSections.Gaps, "Wider than you own", $"Your widest lens reaches {Num(minOwned)}mm equivalent and you hit that limit often.", $"{Num(minOwned)}mm", InsightTone.Attention));
        }

        if (p.Signals.TeleEndShare >= 0.3)
        {
            list.Add(I(ReportSections.Gaps, "Longer than you own", $"Your longest lens reaches {Num(maxOwned)}mm equivalent and you hit that limit often.", $"{Num(maxOwned)}mm", InsightTone.Attention));
        }

        if (p.Signals.LightStarvedShare >= 0.15)
        {
            list.Add(I(ReportSections.Gaps, "Speed", "A meaningful share of frames were limited by maximum aperture in low light; a faster lens in your favourite range would help.", Pct(p.Signals.LightStarvedShare), InsightTone.Attention));
        }
    }

    private static void Recommendation(RecommendationSet? set, List<Insight> list)
    {
        if (set is null || set.Ranked.Count == 0)
        {
            return;
        }

        var best = set.Ranked[0];
        list.Add(I(ReportSections.Recommendation, "Best fit", set.Headline ?? $"{best.Lens.Name} ranks first with a score of {Num(best.Score * 100)} / 100.", best.Lens.Name, InsightTone.Positive));
        if (best.ProbabilityBest is > 0 and < 0.6 && set.Ranked.Count > 1)
        {
            list.Add(I(ReportSections.Recommendation, "Close call", $"{best.Lens.Name} ranks first in {Pct(best.ProbabilityBest)} of resamples; {set.Ranked[1].Lens.Name} is a close alternative.", Pct(best.ProbabilityBest), InsightTone.Attention));
        }
    }

    private static void Method(ShootingProfile p, List<Insight> list)
    {
        var q = p.Quality;
        var inScope = Math.Max(1, p.Library.CapturesInScope);
        if (q.MissingLens > 0)
        {
            list.Add(I(ReportSections.Method, "Lens data", $"{Pct((double)q.MissingLens / inScope)} of in-scope captures have no lens name in EXIF (adapted or manual lenses).", Pct((double)q.MissingLens / inScope)));
        }

        list.Add(I(ReportSections.Method, "Subject recognition", q.GenreSource == "none"
            ? "No subject recognition ran; enable pixel analysis and run `photo-clippy models fetch` for CLIP."
            : $"Subjects were classified by {q.GenreSource}{(q.MeanGenreConfidence is { } c ? $" with mean confidence {Pct(c)}" : string.Empty)}.", q.GenreSource));

        if (q.JunkExcluded > 0)
        {
            list.Add(I(ReportSections.Method, "Excluded", $"{Num(q.JunkExcluded, "N0")} screenshots or document photos were excluded from statistics.", Num(q.JunkExcluded, "N0")));
        }
    }
}
