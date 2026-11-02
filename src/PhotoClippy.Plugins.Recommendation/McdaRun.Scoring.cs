using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

internal sealed partial class McdaRun
{
    private static readonly uint[] PoissonThresholds = BuildPoissonThresholds();

    private int _b;
    private int _g;
    private double[] _binF35 = [];
    private double[] _binLn = [];
    private double[] _binFnum = [];
    private double[] _binPupil = [];
    private int[] _binFlags = [];
    private int[] _sBin = [];
    private double[] _sW = [];
    private double[] _sWIso = [];
    private int[] _sG = [];
    private double[] _sWConf = [];
    private double[] _kFit = [];
    private double[] _kLow = [];
    private double[] _kIso = [];
    private double[] _kReach = [];
    private double[] _gm = [];
    private double[] _staticNorm = [];
    private bool[] _eligible = [];
    private double[] _pointRaw = [];
    private double[] _pointNorm = [];
    private double[] _pointScore = [];
    private double[] _pointG = [];
    private double[] _probBest = [];
    private double[] _scoreLow = [];
    private double[] _scoreHigh = [];
    private int _validReplicates;

    // Frames whose focal, f-number and pupil agree within 0.1% (finer than EXIF precision) share a bin.
    private const double BinResolution = 1000;

    private readonly record struct BinKey(long QF, long QN, long QP, int Flags, double F35, double Fnum, double Pupil) : IComparable<BinKey>
    {
        public static BinKey Of(double f35, double lnF, double fnum, double pupil, int flags) => new(
            Quantize(lnF),
            fnum > 0 ? Quantize(Math.Log(fnum)) : long.MinValue,
            pupil > 0 ? Quantize(Math.Log(pupil)) : long.MinValue,
            flags,
            f35,
            fnum > 0 ? fnum : -1,
            pupil > 0 ? pupil : -1);

        public bool SameBin(in BinKey other) => QF == other.QF && QN == other.QN && QP == other.QP && Flags == other.Flags;

        public int CompareTo(BinKey other)
        {
            var c = QF.CompareTo(other.QF);
            if (c == 0)
            {
                c = QN.CompareTo(other.QN);
            }

            if (c == 0)
            {
                c = QP.CompareTo(other.QP);
            }

            if (c == 0)
            {
                c = Flags.CompareTo(other.Flags);
            }

            if (c == 0)
            {
                c = F35.CompareTo(other.F35);
            }

            if (c == 0)
            {
                c = Fnum.CompareTo(other.Fnum);
            }

            return c != 0 ? c : Pupil.CompareTo(other.Pupil);
        }

        private static long Quantize(double ln) => (long)Math.Round(ln * BinResolution);
    }

    private static uint[] BuildPoissonThresholds()
    {
        var thresholds = new uint[12];
        var p = Math.Exp(-1);
        var cdf = 0.0;
        for (var k = 0; k < thresholds.Length; k++)
        {
            cdf += p;
            p /= k + 1;
            thresholds[k] = (uint)Math.Min(cdf * 4294967296.0, uint.MaxValue);
        }

        return thresholds;
    }

    private static int Poisson(uint u)
    {
        var k = 0;
        var t = PoissonThresholds;
        while (k < t.Length && u >= t[k])
        {
            k++;
        }

        return k;
    }

    private void BuildBins()
    {
        var n = _n;
        var keys = new BinKey[n];
        var order = new int[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = BinKey.Of(_f35[i], _lnF[i], _fnum[i], _pupil[i], _flags[i] & ~FCeilKnown);
            order[i] = i;
        }

        Array.Sort(keys, order);
        var starts = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (i == 0 || !keys[i].SameBin(keys[i - 1]))
            {
                starts.Add(i);
            }
        }

        var binCount = starts.Count;
        _b = binCount;
        _binF35 = new double[binCount];
        _binLn = new double[binCount];
        _binFnum = new double[binCount];
        _binPupil = new double[binCount];
        _binFlags = new int[binCount];
        _sBin = new int[n];
        _sW = new double[n];
        _sWIso = new double[n];
        _sG = new int[n];
        _sWConf = new double[n];
        for (var b = 0; b < binCount; b++)
        {
            var end = b + 1 < binCount ? starts[b + 1] : n;
            var s = order[(starts[b] + end - 1) / 2];
            _binF35[b] = _f35[s];
            _binLn[b] = _lnF[s];
            _binFnum[b] = _fnum[s];
            _binPupil[b] = _pupil[s];
            _binFlags[b] = _flags[s];
            for (var i = starts[b]; i < end; i++)
            {
                _sBin[i] = b;
            }
        }

        var sink = _genres.Count; // write-only slot for unclassified frames keeps the resample loop branch-free
        for (var i = 0; i < n; i++)
        {
            var s = order[i];
            _sW[i] = _w[s];
            _sWIso[i] = _w[s] * _isoMult[s];
            _sG[i] = _genre[s] >= 0 ? _genre[s] : sink;
            _sWConf[i] = _w[s] * _gconf[s];
        }
    }

    private void BuildKernels()
    {
        var k = K;
        var b = _b;
        _g = _genres.Count;
        _kFit = new double[k * b];
        _kLow = new double[k * b];
        _kIso = new double[k * b];
        _kReach = new double[k * b];
        _gm = new double[k * Math.Max(1, _g)];
        Parallel.For(0, k, ci =>
        {
            var o = _cands[ci].Optic;
            var off = ci * b;
            for (var bi = 0; bi < b; bi++)
            {
                var ln = _binLn[bi];
                var flags = _binFlags[bi];
                var c = o.Coverage(ln, _sigma);
                _kFit[off + bi] = c;
                if ((flags & FCeil) != 0 && c > 1e-12)
                {
                    var gain = Math.Max(0, Optics.Stops(_binFnum[bi], o.ApertureAtEq(_binF35[bi])));
                    _kLow[off + bi] = gain * c * ((flags & FStarved) != 0 ? 1.5 : 1.0);
                }

                var pupil = _binPupil[bi];
                if (!double.IsNaN(pupil) && pupil > 0 && c > 1e-12)
                {
                    var fl = o.NativeAtEq(_binF35[bi]);
                    var pl = fl / o.Spec.MaxApertureAt(fl);
                    _kIso[off + bi] = Math.Max(0, Math.Log2(pl / pupil)) * c;
                }

                _kReach[off + bi] = ReachKernel(o, ln, flags, c);
            }

            for (var gi = 0; gi < _g; gi++)
            {
                _gm[(ci * _g) + gi] = GenreMatch(o, _genres[gi]);
            }
        });

        _staticNorm = new double[k * 2];
        if (_active[CPort])
        {
            var min = _cands.Min(c => c.Spec.WeightG!.Value);
            for (var ci = 0; ci < k; ci++)
            {
                _staticNorm[ci * 2] = min / _cands[ci].Spec.WeightG!.Value;
            }
        }

        if (_active[CValue])
        {
            var min = _cands.Min(c => c.Spec.PriceUsd!.Value);
            for (var ci = 0; ci < k; ci++)
            {
                _staticNorm[(ci * 2) + 1] = min / _cands[ci].Spec.PriceUsd!.Value;
            }
        }

        _eligible = new bool[k];
        var allOwned = _cands.TrueForAll(c => c.AlreadyOwned);
        for (var ci = 0; ci < k; ci++)
        {
            _eligible[ci] = allOwned || !_cands[ci].AlreadyOwned;
        }
    }

    private double ReachKernel(Optic o, double ln, int flags, double c)
    {
        var r = 0.0;
        if ((flags & FWide) != 0)
        {
            var ramp = Optics.ReachRamp(ln - o.LnMin);
            if (ramp > 0)
            {
                r = Math.Max(r, ramp * Demand(o, ln, -1));
            }
        }

        if ((flags & FTele) != 0)
        {
            var ramp = Optics.ReachRamp(o.LnMax - ln);
            if (ramp > 0)
            {
                r = Math.Max(r, ramp * Demand(o, ln, 1));
            }
        }

        if ((flags & FOutside) != 0)
        {
            r = Math.Max(r, c);
        }

        return r;
    }

    private static readonly (double LnT, double Weight)[] DemandSteps =
        [(Math.Log(1.15), 1.0), (Math.Log(1.4), 0.85), (Math.Log(2.0), 0.6)];

    private double Demand(Optic o, double ln, int direction)
    {
        var best = 0.0;
        foreach (var (lnT, weight) in DemandSteps)
        {
            best = Math.Max(best, weight * o.Coverage(ln + (direction * lnT), _sigma));
        }

        return best;
    }

    private double GenreMatch(Optic o, GenreInfo g)
    {
        var (lo, hi) = o.Band(_sigma);
        var glo = g.LnMin - Optics.ZoomBand;
        var ghi = g.LnMax + Optics.ZoomBand;
        var ov = Optics.Overlap(lo, hi, glo, ghi) / Math.Min(ghi - glo, hi - lo);
        var match = Math.Min(1, ov);
        if (match > 0 && g.Affinity.PrefersFast)
        {
            var centre = Math.Clamp(Math.Exp((g.LnMin + g.LnMax) / 2), o.Min35, o.Max35);
            if (o.ApertureAtEq(centre) <= g.Affinity.FastAperture * Math.Pow(2, 1.0 / 12))
            {
                match *= 1.5;
            }
        }

        return match;
    }

    private sealed class Scratch
    {
        public Scratch(int bins, int genres, int candidates)
        {
            Aw = new double[bins];
            AIso = new double[bins];
            G = new double[genres + 1];
            Raw = new double[candidates * NData];
            Norm = new double[candidates * NCrit];
            Scores = new double[candidates];
        }

        public double[] Aw { get; }
        public double[] AIso { get; }
        public double[] G { get; }
        public double[] Raw { get; }
        public double[] Norm { get; }
        public double[] Scores { get; }
    }

    private void PointEstimate()
    {
        var sc = new Scratch(_b, _g, K);
        var total = 0.0;
        for (var i = 0; i < _n; i++)
        {
            var b = _sBin[i];
            sc.Aw[b] += _sW[i];
            sc.AIso[b] += _sWIso[i];
            sc.G[_sG[i]] += _sWConf[i];

            total += _sW[i];
        }

        Evaluate(sc, total > 0 ? total : 1);
        _pointRaw = (double[])sc.Raw.Clone();
        _pointNorm = (double[])sc.Norm.Clone();
        _pointScore = (double[])sc.Scores.Clone();
        _pointG = (double[])sc.G.Clone();
    }

    private void Evaluate(Scratch sc, double total)
    {
        var b = _b;
        var aw = sc.Aw;
        var aiso = sc.AIso;
        var raw = sc.Raw;
        var kFit = _kFit;
        var kLow = _kLow;
        var kIso = _kIso;
        var kReach = _kReach;
        var inv = 1.0 / total;
        for (var k = 0; k < K; k++)
        {
            var off = k * b;
            double f0 = 0, f1 = 0, l0 = 0, l1 = 0, i0 = 0, i1 = 0, r0 = 0, r1 = 0;
            var x = 0;
            for (; x + 1 < b; x += 2)
            {
                var w0 = aw[x];
                var w1 = aw[x + 1];
                var j = off + x;
                f0 += w0 * kFit[j];
                f1 += w1 * kFit[j + 1];
                l0 += w0 * kLow[j];
                l1 += w1 * kLow[j + 1];
                r0 += w0 * kReach[j];
                r1 += w1 * kReach[j + 1];
                i0 += aiso[x] * kIso[j];
                i1 += aiso[x + 1] * kIso[j + 1];
            }

            if (x < b)
            {
                var w0 = aw[x];
                var j = off + x;
                f0 += w0 * kFit[j];
                l0 += w0 * kLow[j];
                r0 += w0 * kReach[j];
                i0 += aiso[x] * kIso[j];
            }

            var genre = 0.0;
            for (var g = 0; g < _g; g++)
            {
                genre += sc.G[g] * _gm[(k * _g) + g];
            }

            var o = k * NData;
            raw[o + CFit] = (f0 + f1) * inv;
            raw[o + CLow] = (l0 + l1) * inv;
            raw[o + CIso] = (i0 + i1) * inv;
            raw[o + CReach] = (r0 + r1) * inv;
            raw[o + CGenre] = genre * inv;
        }

        Normalize(raw, sc.Norm);
        Score(sc.Norm, _weights, sc.Scores);
    }

    private void Normalize(double[] raw, double[] norm)
    {
        for (var j = 0; j < NData; j++)
        {
            var max = 1e-9;
            for (var k = 0; k < K; k++)
            {
                max = Math.Max(max, raw[(k * NData) + j]);
            }

            for (var k = 0; k < K; k++)
            {
                norm[(k * NCrit) + j] = raw[(k * NData) + j] / max;
            }
        }

        for (var k = 0; k < K; k++)
        {
            norm[(k * NCrit) + CPort] = _staticNorm[k * 2];
            norm[(k * NCrit) + CValue] = _staticNorm[(k * 2) + 1];
        }
    }

    private void Score(double[] norm, double[] weights, double[] scores)
    {
        for (var k = 0; k < K; k++)
        {
            var s = 0.0;
            for (var j = 0; j < NCrit; j++)
            {
                s += weights[j] * norm[(k * NCrit) + j];
            }

            scores[k] = _cands[k].Multiplier * s;
        }
    }

    private int Winner(double[] scores)
    {
        var best = -1;
        for (var k = 0; k < K; k++)
        {
            if (_eligible[k] && (best < 0 || scores[k] > scores[best]))
            {
                best = k;
            }
        }

        return best;
    }

    private void RunBootstrap()
    {
        var k = K;
        _probBest = new double[k];
        _scoreLow = (double[])_pointScore.Clone();
        _scoreHigh = (double[])_pointScore.Clone();
        var replicates = Math.Clamp(_prefs.BootstrapReplicates, 0, 100_000);
        if (replicates == 0 || _n == 0)
        {
            _probBest[Winner(_pointScore)] = 1;
            return;
        }

        var all = new double[replicates * k];
        var valid = new bool[replicates];
        var seed = unchecked((ulong)(long)_prefs.Seed);
        var options = new ParallelOptions { CancellationToken = _ct };
        Parallel.For(
            0,
            replicates,
            options,
            () => new Scratch(_b, _g, k),
            (r, _, sc) =>
            {
                Array.Clear(sc.Aw);
                Array.Clear(sc.AIso);
                Array.Clear(sc.G);
                var rng = new Xoshiro256(seed, (ulong)r);
                var total = Resample(sc, ref rng);

                if (total > 0)
                {
                    Evaluate(sc, total);
                    Array.Copy(sc.Scores, 0, all, r * k, k);
                    valid[r] = true;
                }

                return sc;
            },
            _ => { });

        var wins = new int[k];
        var row = new double[k];
        for (var r = 0; r < replicates; r++)
        {
            if (!valid[r])
            {
                continue;
            }

            _validReplicates++;
            Array.Copy(all, r * k, row, 0, k);
            wins[Winner(row)]++;
        }

        if (_validReplicates == 0)
        {
            _probBest[Winner(_pointScore)] = 1;
            return;
        }

        var column = new double[_validReplicates];
        for (var ci = 0; ci < k; ci++)
        {
            _probBest[ci] = (double)wins[ci] / _validReplicates;
            var m = 0;
            for (var r = 0; r < replicates; r++)
            {
                if (valid[r])
                {
                    column[m++] = all[(r * k) + ci];
                }
            }

            Array.Sort(column);
            _scoreLow[ci] = Math.Min(_pointScore[ci], Optics.Percentile(column, 0.05));
            _scoreHigh[ci] = Math.Max(_pointScore[ci], Optics.Percentile(column, 0.95));
        }
    }

    private double Resample(Scratch sc, ref Xoshiro256 rng)
    {
        var n = _n;
        var aw = sc.Aw;
        var aiso = sc.AIso;
        var gw = sc.G;
        var sBin = _sBin;
        var sW = _sW;
        var sWIso = _sWIso;
        var sG = _sG;
        var sWConf = _sWConf;
        var t = PoissonThresholds;
        uint t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3];
        var total = 0.0;
        ulong bits = 0;
        for (var s = 0; s < n; s++)
        {
            uint u;
            if ((s & 1) == 0)
            {
                bits = rng.Next();
                u = (uint)bits;
            }
            else
            {
                u = (uint)(bits >> 32);
            }

            var lambda = u < t3
                ? (u >= t0 ? 1 : 0) + (u >= t1 ? 1 : 0) + (u >= t2 ? 1 : 0)
                : Poisson(u);
            var b = sBin[s];
            var w = lambda * sW[s];
            aw[b] += w;
            aiso[b] += lambda * sWIso[s];
            total += w;
            gw[sG[s]] += lambda * sWConf[s];
        }

        return total;
    }
}
