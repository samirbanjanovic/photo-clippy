using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

internal sealed partial class McdaRun
{
    private static readonly (double Min35, double Max35, double ApWide, double ApTele)[] ZoomArchetypes =
    [
        (14, 24, 2.8, 2.8),
        (16, 35, 4, 4),
        (24, 70, 2.8, 2.8),
        (24, 120, 4, 4),
        (70, 200, 2.8, 2.8),
        (100, 400, 4.5, 5.6),
    ];

    private bool CropIsFull => Math.Abs(_crop - 1) < 0.03;

    private double NiceNative(double eq)
    {
        if (CropIsFull)
        {
            return eq;
        }

        var native = eq / _crop;
        return native < 60 ? Math.Max(1, Math.Round(native)) : Math.Round(native / 5) * 5;
    }

    private static double SnapEq(double f35)
    {
        var best = Optics.CommonEq[0];
        var bestD = double.MaxValue;
        foreach (var c in Optics.CommonEq)
        {
            var d = Math.Abs(Math.Log(f35 / c));
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }

        return best;
    }

    private List<double> PeakFocals()
    {
        var peaks = new List<double>();
        var profilePeaks = _request.Profile.FocalPeaks
            .Where(p => double.IsFinite(p.Focal35Mm) && p.Focal35Mm > 1 && p.Share > 0)
            .OrderByDescending(p => p.Share)
            .ThenBy(p => p.Focal35Mm)
            .ToList();
        if (profilePeaks.Count > 0)
        {
            var top = profilePeaks[0].Share;
            foreach (var p in profilePeaks)
            {
                var eq = SnapEq(p.Focal35Mm);
                if (peaks.Contains(eq))
                {
                    continue;
                }

                if (peaks.Count >= 2 && p.Share < Math.Max(0.08, top / 3))
                {
                    break;
                }

                peaks.Add(eq);
                if (peaks.Count == 3)
                {
                    break;
                }
            }

            return peaks;
        }

        var hist = new double[Optics.CommonEq.Length];
        for (var i = 0; i < _n; i++)
        {
            hist[Array.IndexOf(Optics.CommonEq, SnapEq(_f35[i]))] += _w[i];
        }

        var total = hist.Sum();
        var order = Enumerable.Range(0, hist.Length).Where(i => hist[i] > 0).OrderByDescending(i => hist[i]).ThenBy(i => i).ToList();
        foreach (var i in order)
        {
            if (peaks.Count >= 2 && hist[i] < Math.Max(0.08 * total, hist[order[0]] / 3))
            {
                break;
            }

            peaks.Add(Optics.CommonEq[i]);
            if (peaks.Count == 3)
            {
                break;
            }
        }

        return peaks;
    }

    private List<LensSpec> ProposeArchetypes()
    {
        var result = new List<LensSpec>();
        if (_n == 0 && _request.Profile.FocalPeaks.Count == 0)
        {
            return result;
        }

        var primes = new List<LensSpec>();
        foreach (var eq in PeakFocals())
        {
            var ap = eq >= 135 ? 2.8 : eq >= 85 ? 1.8 : 1.4;
            var native = NiceNative(eq);
            var spec = Archetype(native, native, ap, ap, eq, eq, "prime");
            if (!DuplicatesOwned(spec) && !primes.Exists(p => p.Id == spec.Id))
            {
                primes.Add(spec);
            }
        }

        var zooms = new List<(LensSpec Spec, double Relevance)>();
        foreach (var (min35, max35, apW, apT) in ZoomArchetypes)
        {
            var spec = Archetype(NiceNative(min35), NiceNative(max35), apW, apT, min35, max35, "zoom");
            if (DuplicatesOwned(spec))
            {
                continue;
            }

            var optic = new Optic(spec, _crop, spec.ShortName);
            var coverage = 0.0;
            var gap = 0.0;
            for (var i = 0; i < _n; i++)
            {
                var c = optic.Coverage(_lnF[i], _sigma);
                coverage += _w[i] * c;
                gap += _w[i] * ReachKernel(optic, _lnF[i], _flags[i], c);
            }

            var total = _totalW > 0 ? _totalW : 1;
            coverage /= total;
            gap /= total;
            if (coverage < 0.2 && gap < 0.08)
            {
                continue;
            }

            var redundancy = 0.0;
            var (a1, a2) = optic.Band(_sigma);
            foreach (var o in _owned)
            {
                var (b1, b2) = o.Band(_sigma);
                var ov = Optics.Overlap(a1, a2, b1, b2) / (a2 - a1);
                if (ov <= 0)
                {
                    continue;
                }

                var centre = Math.Exp((Math.Max(a1, b1) + Math.Min(a2, b2)) / 2);
                var stops = Optics.Stops(o.ApertureAtEq(centre), optic.ApertureAtEq(centre));
                redundancy = Math.Max(redundancy, Math.Min(1, ov) * Math.Exp(-1.6 * Math.Max(0, stops)));
            }

            zooms.Add((spec, ((0.6 * coverage) + gap) * (1 - (0.8 * redundancy))));
        }

        zooms.Sort((x, y) => y.Relevance.CompareTo(x.Relevance));
        var cap = _settings.MaxSuggestions;
        var zoomSlots = zooms.Count == 0 ? 0 : Math.Max(1, cap - primes.Count);
        var takeZooms = Math.Min(zoomSlots, zooms.Count);
        var takePrimes = Math.Min(primes.Count, cap - takeZooms);
        result.AddRange(primes.Take(takePrimes));
        result.AddRange(zooms.Take(Math.Min(takeZooms, cap - takePrimes)).Select(z => z.Spec));
        return result;
    }

    private LensSpec Archetype(double nMin, double nMax, double apW, double apT, double eqMin, double eqMax, string kind)
    {
        var spec = new LensSpec
        {
            Id = LensSpec.Shorthand(nMin, nMax, apW, apT),
            Name = string.Empty,
            FocalMinMm = nMin,
            FocalMaxMm = nMax,
            MaxApertureWide = apW,
            MaxApertureTele = apT,
            Source = LensSpecSource.Suggested,
        };
        var eqText = eqMin >= eqMax ? Optics.Num(eqMin, "0") : Optics.Num(eqMin, "0") + "-" + Optics.Num(eqMax, "0");
        var name = CropIsFull
            ? $"{spec.ShortName} {kind} (archetype)"
            : $"{spec.ShortName} {kind} (archetype, ≈{eqText}mm-eq)";
        return spec with { Name = name };
    }

    private bool DuplicatesOwned(LensSpec spec)
    {
        var optic = new Optic(spec, _crop, spec.ShortName);
        return _owned.Exists(o => SameLens(optic, o));
    }
}
