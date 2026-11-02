using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

internal sealed partial class McdaRun
{
    private sealed class Stats
    {
        public double CoverageShare;
        public double ZoneLo;
        public double ZoneHi;
        public double ZoneW;
        public double ZoneCeilW;
        public double CeilingCoveredShare;
        public double StarvedShare;
        public double StopsGained;
        public double MedianCeilN = double.NaN;
        public double MedianIsoBefore = double.NaN;
        public double MedianIsoAfter = double.NaN;
        public double IsolationGain;
        public double BlurRatio = double.NaN;
        public double PupilCandidate = double.NaN;
        public double PupilYours = double.NaN;
        public double ReachShare;
        public int TopGenre = -1;
        public double TopGenreShare;
        public bool TopGenreFast;
    }

    private Stats ComputeStats(int k)
    {
        var o = _cands[k].Optic;
        var st = new Stats();
        (st.ZoneLo, st.ZoneHi) = o.Zone(_sigma);
        var ceilW = 0.0;
        var ceilCov = 0.0;
        var starvedW = 0.0;
        var gainSum = 0.0;
        var isoW = 0.0;
        var isoGainSum = 0.0;
        var covW = 0.0;
        var reachW = 0.0;
        var ceilN = new List<(double, double)>();
        var isoBefore = new List<(double, double)>();
        var isoAfter = new List<(double, double)>();
        var pupils = new List<(double, double)>();
        var candPupils = new List<(double, double)>();
        for (var i = 0; i < _n; i++)
        {
            var ln = _lnF[i];
            var w = _w[i];
            var flags = _flags[i];
            var c = o.Coverage(ln, _sigma);
            var inZone = o.IsZoom ? ln >= o.LnMin - 0.01 && ln <= o.LnMax + 0.01 : c >= 0.5;
            var atCeil = (flags & FCeil) != 0;
            var gain = atCeil ? Math.Max(0, Optics.Stops(_fnum[i], o.ApertureAtEq(_f35[i]))) : 0;
            if (atCeil)
            {
                ceilW += w;
                if (inZone && gain >= 1.0 / 3)
                {
                    ceilCov += w;
                }
            }

            if (ReachKernel(o, ln, flags, c) >= 0.5)
            {
                reachW += w;
            }

            if (!inZone)
            {
                continue;
            }

            covW += w;
            if (atCeil)
            {
                st.ZoneCeilW += w;
                gainSum += w * gain;
                if ((flags & FStarved) != 0)
                {
                    starvedW += w;
                }

                ceilN.Add((_fnum[i], w));
                if (_iso[i] > 0)
                {
                    isoBefore.Add((_iso[i], w));
                    isoAfter.Add((_iso[i] / Math.Pow(2, gain), w));
                }
            }

            if (!double.IsNaN(_pupil[i]) && _pupil[i] > 0)
            {
                var fl = o.NativeAtEq(_f35[i]);
                var pl = fl / o.Spec.MaxApertureAt(fl);
                isoGainSum += w * Math.Max(0, Math.Log2(pl / _pupil[i]));
                isoW += w;
                pupils.Add((_pupil[i], w));
                candPupils.Add((pl, w));
            }
        }

        var total = _totalW > 0 ? _totalW : 1;
        st.ZoneW = covW;
        st.CoverageShare = covW / total;
        st.ReachShare = reachW / total;
        st.CeilingCoveredShare = ceilW > 0 ? ceilCov / ceilW : 0;
        st.StopsGained = st.ZoneCeilW > 0 ? gainSum / st.ZoneCeilW : 0;
        st.StarvedShare = st.ZoneCeilW > 0 ? starvedW / st.ZoneCeilW : 0;
        st.MedianCeilN = Optics.WeightedMedian(ceilN);
        st.MedianIsoBefore = Optics.WeightedMedian(isoBefore);
        st.MedianIsoAfter = Optics.WeightedMedian(isoAfter);
        st.IsolationGain = isoW > 0 ? isoGainSum / isoW : 0;
        st.PupilYours = Optics.WeightedMedian(pupils);
        st.PupilCandidate = Optics.WeightedMedian(candPupils);
        if (st.PupilYours > 0 && st.PupilCandidate > 0)
        {
            st.BlurRatio = st.PupilCandidate / st.PupilYours;
        }

        if (_active[CGenre])
        {
            var best = 0.0;
            for (var g = 0; g < _g; g++)
            {
                var v = _pointG[g] * _gm[(k * _g) + g];
                if (v > best)
                {
                    best = v;
                    st.TopGenre = g;
                }
            }

            if (st.TopGenre >= 0)
            {
                st.TopGenreShare = _pointG[st.TopGenre] / total;
                st.TopGenreFast = _gm[(k * _g) + st.TopGenre] > 1.0 + 1e-9;
            }
        }

        return st;
    }

    private Dictionary<string, double> BuildMetrics(int k, Stats st)
    {
        var c = _cands[k];
        var raw = k * NData;
        var m = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["focalMin35"] = Math.Round(c.Optic.Min35, 2),
            ["focalMax35"] = Math.Round(c.Optic.Max35, 2),
            ["coverageShare"] = st.CoverageShare,
            ["fit"] = _pointRaw[raw + CFit],
            ["reachShare"] = st.ReachShare,
            ["reach"] = _pointRaw[raw + CReach],
            ["redundancy"] = c.Redundancy,
        };
        if (_active[CLow])
        {
            m["ceilingCoveredShare"] = st.CeilingCoveredShare;
            m["stopsGained"] = st.StopsGained;
            m["lowLight"] = _pointRaw[raw + CLow];
            if (double.IsFinite(st.MedianIsoBefore))
            {
                m["medianIsoBefore"] = st.MedianIsoBefore;
                m["medianIsoAfter"] = st.MedianIsoAfter;
            }
        }

        if (_active[CIso])
        {
            m["isolationGain"] = st.IsolationGain;
            m["isolation"] = _pointRaw[raw + CIso];
            if (double.IsFinite(st.BlurRatio))
            {
                m["blurRatio"] = st.BlurRatio;
            }
        }

        if (_active[CGenre])
        {
            m["genreMatch"] = _pointRaw[raw + CGenre];
        }

        if (c.Spec.WeightG is { } wg)
        {
            m["weightG"] = wg;
        }

        if (c.Spec.PriceUsd is { } p)
        {
            m["priceUsd"] = p;
        }

        return m;
    }

    private string Label(Candidate c) => c.Spec.Source == LensSpecSource.Suggested
        ? c.Spec.ShortName + (c.Optic.IsZoom ? " zoom" : " prime") + " archetype"
        : c.Spec.ShortName;

    private string ZoneText(Stats st) => Optics.Eq(st.ZoneLo, st.ZoneHi);

    private string ApertureText(Optic o) => Math.Abs(o.Spec.MaxApertureTele - o.Spec.MaxApertureWide) > 0.05
        ? Optics.FNum(o.Spec.MaxApertureWide) + "-" + Optics.Num(o.Spec.MaxApertureTele)
        : Optics.FNum(o.Spec.MaxApertureWide);

    private List<string> BuildEvidence(int k, Stats st, double[] contribution)
    {
        var c = _cands[k];
        var o = c.Optic;
        var lines = new List<(double Order, string Text)>();
        string fit;
        if (o.IsZoom)
        {
            fit = $"{Optics.Pct(st.CoverageShare)} of your frames fall inside its {Optics.Eq(o.Min35, o.Max35)} range";
        }
        else
        {
            fit = $"{Optics.Pct(st.CoverageShare)} of your frames were shot at {ZoneText(st)}, the framing a {Optics.Num(o.Min35, "0")}mm-eq prime covers";
        }

        if (!CropIsFull)
        {
            fit += $" ({Optics.Mm(o.Spec.FocalMinMm)}{(o.IsZoom ? "-" + Optics.Mm(o.Spec.FocalMaxMm) : string.Empty)} × {Optics.Num(_crop, "0.0#")} crop)";
        }

        if (_active[CLow] && st.ZoneCeilW > 0 && st.StopsGained >= 1.0 / 3 && st.ZoneW > 0)
        {
            var text = $"{Optics.Pct(st.ZoneCeilW / st.ZoneW)} of your frames at {ZoneText(st)} were shot wide open"
                + (double.IsFinite(st.MedianCeilN) ? $" at {Optics.FNum(st.MedianCeilN)}" : string.Empty)
                + $"; {ApertureText(o)} gains {Optics.StopsText(st.StopsGained)}";
            if (st.MedianIsoBefore >= 800)
            {
                text += $" (median ISO {Optics.IsoNominal(st.MedianIsoBefore)} → {Optics.IsoNominal(st.MedianIsoAfter)})";
            }
            else if (double.IsFinite(st.MedianIsoBefore))
            {
                text += $" — at median ISO {Optics.IsoNominal(st.MedianIsoBefore)} that buys shutter speed or shallower focus rather than less noise";
            }

            lines.Add((contribution[CLow], text));
        }

        if (_active[CIso] && double.IsFinite(st.BlurRatio) && st.BlurRatio >= 1.3)
        {
            lines.Add((contribution[CIso],
                $"~{Optics.Num(st.BlurRatio, "0.#")}× more background blur than your frames at {ZoneText(st)} (entrance pupil {Optics.Num(st.PupilCandidate, "0")}mm vs your median {Optics.Num(st.PupilYours, "0")}mm)"));
        }

        if (st.ReachShare >= 0.02)
        {
            var ext = ReachExtent(o);
            lines.Add((contribution[CReach],
                $"{Optics.Pct(st.ReachShare)} of your frames hit the limit of your current lenses in a direction it extends{ext}"));
        }

        if (st.TopGenre >= 0 && st.TopGenreShare >= 0.03)
        {
            var g = _genres[st.TopGenre];
            var text = $"{Optics.Pct(st.TopGenreShare)} of your frames are {g.Name.ToLowerInvariant()}, which favours {Optics.Eq(g.Affinity.FocalMin35, g.Affinity.FocalMax35)}";
            if (st.TopGenreFast)
            {
                text += $" and f/{Optics.Num(g.Affinity.FastAperture)} or faster";
            }

            lines.Add((contribution[CGenre], text));
        }

        if (_active[CPort] && c.Spec.WeightG is { } wg && _staticNorm[k * 2] >= 0.999)
        {
            lines.Add((contribution[CPort], $"Lightest candidate at {Optics.Num(wg, "0")}g"));
        }

        if (_active[CValue] && c.Spec.PriceUsd is { } price && _staticNorm[(k * 2) + 1] >= 0.999)
        {
            lines.Add((contribution[CValue], $"Least expensive candidate at ${Optics.Num(price, "0")}"));
        }

        lines.Sort((a, b) => b.Order.CompareTo(a.Order));
        var result = new List<string> { fit };
        foreach (var (_, text) in lines)
        {
            if (result.Count == 4)
            {
                break;
            }

            result.Add(text);
        }

        if (result.Count < 2)
        {
            result.Add(_validReplicates > 0
                ? $"Overall score {Optics.Num(_pointScore[k], "0.00")}; ranks first in {Optics.Pct(_probBest[k])} of {_validReplicates} resamples of your frames"
                : $"Overall score {Optics.Num(_pointScore[k], "0.00")}");
        }

        return result;
    }

    private string ReachExtent(Optic o)
    {
        if (_owned.Count == 0)
        {
            return string.Empty;
        }

        var ownedMin = _owned.Min(x => x.Min35);
        var ownedMax = _owned.Max(x => x.Max35);
        if (o.Min35 < ownedMin / 1.05)
        {
            return $" — it goes down to {Optics.Mm(Math.Round(o.Min35))}-eq, yours stop at {Optics.Mm(Math.Round(ownedMin))}-eq";
        }

        return o.Max35 > ownedMax * 1.05 ? $" — it reaches {Optics.Mm(Math.Round(o.Max35))}-eq, yours stop at {Optics.Mm(Math.Round(ownedMax))}-eq" : string.Empty;
    }

    private List<string> BuildTradeOffs(int k, Stats st)
    {
        var c = _cands[k];
        var o = c.Optic;
        var t = new List<string>();
        if (c.OwnedMatch is { } owned)
        {
            t.Add($"You already own this lens ({owned.Display})");
        }

        if (!o.IsZoom && _n > 0)
        {
            t.Add($"Fixed focal length — {Optics.Pct(1 - st.CoverageShare)} of your frames fall outside {ZoneText(st)} and need another lens, cropping or moving");
        }
        else if (o.IsZoom && _n > 0 && st.CoverageShare < 0.15)
        {
            t.Add($"Only {Optics.Pct(st.CoverageShare)} of your frames fall inside its range");
        }

        var (lo, hi) = o.Band(_sigma);
        Candidate? fastest = null;
        foreach (var x in _cands)
        {
            var (xl, xh) = x.Optic.Band(_sigma);
            if (x != c && Optics.Overlap(lo, hi, xl, xh) > 0 && (fastest is null || x.Optic.Fastest < fastest.Optic.Fastest))
            {
                fastest = x;
            }
        }

        var slower = fastest is null ? 0 : Optics.Stops(o.Fastest, fastest.Optic.Fastest);
        if (fastest is not null && slower >= 0.9)
        {
            t.Add($"{Optics.FNum(o.Fastest)} is {Optics.StopsText(slower)} slower than {Label(fastest)} where their ranges overlap");
        }

        if (o.IsZoom && Optics.Stops(o.Spec.MaxApertureTele, o.Spec.MaxApertureWide) >= 0.5)
        {
            t.Add($"Aperture narrows to {Optics.FNum(o.Spec.MaxApertureTele)} at the long end");
        }

        if (c.Spec.WeightG is { } wg)
        {
            var lightest = _cands.Where(x => x.Spec.WeightG is not null).MinBy(x => x.Spec.WeightG!.Value)!;
            var ratio = wg / lightest.Spec.WeightG!.Value;
            if (ratio >= 1.3)
            {
                t.Add($"{Optics.Num(wg, "0")}g — {Optics.Num(ratio, "0.#")}× the lightest candidate ({Label(lightest)}, {Optics.Num(lightest.Spec.WeightG.Value, "0")}g)");
            }
            else if (lightest == c && wg >= 900)
            {
                t.Add($"Weighs {Optics.Num(wg, "0")}g");
            }
        }

        if (c.Spec.PriceUsd is { } price)
        {
            var cheapest = _cands.Where(x => x.Spec.PriceUsd is not null).MinBy(x => x.Spec.PriceUsd!.Value)!;
            var ratio = price / cheapest.Spec.PriceUsd!.Value;
            if (ratio >= 1.3)
            {
                t.Add($"${Optics.Num(price, "0")} — {Optics.Num(ratio, "0.#")}× the least expensive candidate ({Label(cheapest)}, ${Optics.Num(cheapest.Spec.PriceUsd.Value, "0")})");
            }
        }

        if (c.OwnedMatch is null && c.OverlapWith is { } with && (c.Redundancy >= 0.25 || c.Overlap >= 0.5))
        {
            var (a1, a2) = o.Band(_sigma);
            var (b1, b2) = with.Band(_sigma);
            var centre = Math.Exp((Math.Max(a1, b1) + Math.Min(a2, b2)) / 2);
            var faster = Optics.Stops(with.ApertureAtEq(centre), o.ApertureAtEq(centre));
            var span = Optics.Pct(Math.Min(1, c.Overlap));
            var pair = Math.Min(1, c.Overlap) * Math.Exp(-1.6 * Math.Max(0, faster));
            if (pair < 0.25)
            {
                t.Add($"{span} of its range duplicates your {with.Display}; what you gain there is {Optics.StopsText(faster)} of speed, not new framing");
            }
            else
            {
                t.Add(faster >= 1.0 / 3
                    ? $"Overlaps your {with.Display}; only {Optics.StopsText(faster)} faster there (redundancy {Optics.Pct(pair)})"
                    : $"Overlaps your {with.Display} at no speed advantage (redundancy {Optics.Pct(pair)})");
            }
        }

        return t;
    }

    private static readonly string[] Strengths =
        ["fits how you shoot", "big low-light gain", "strong subject isolation", "covers new focal lengths", "suits your genres", "light to carry", "good value"];

    private int StrengthCriterion(int k)
    {
        var best = CFit;
        var bestV = double.NegativeInfinity;
        for (var j = 0; j < NCrit; j++)
        {
            if (!_active[j] || _weights[j] <= 0 || _pointNorm[(k * NCrit) + j] < 0.05)
            {
                continue;
            }

            var mean = 0.0;
            for (var x = 0; x < K; x++)
            {
                mean += _pointNorm[(x * NCrit) + j];
            }

            mean /= K;
            var v = _weights[j] * (_pointNorm[(k * NCrit) + j] - mean);
            if (v > bestV + 1e-12)
            {
                bestV = v;
                best = j;
            }
        }

        return best;
    }

    private string Verdict(int k, int rank, double topScore, Stats st)
    {
        var c = _cands[k];
        if (c.AlreadyOwned && !_eligible[k])
        {
            return "Already owned";
        }

        var strength = Strengths[StrengthCriterion(k)];
        if (rank == 1)
        {
            return (_probBest[k] >= 0.6 ? "Top pick — " : "Narrow top pick — ") + strength;
        }

        if (c.Redundancy >= 0.4 && c.RedundantWith is { } with)
        {
            return $"Largely duplicates your {with.Display}";
        }

        var rel = topScore > 0 ? _pointScore[k] / topScore : 0;
        if (rel >= 0.9)
        {
            return "Close alternative — " + strength;
        }

        if (rel >= 0.7)
        {
            return "Solid alternative — " + strength;
        }

        if (rel >= 0.45)
        {
            return "Trails the top pick — " + strength;
        }

        return st.CoverageShare < 0.1 && _n > 0 ? "Weak match — little overlap with how you shoot" : $"Lower priority — {strength}, but scores {Optics.Pct(rel)} of the top pick";
    }
}
