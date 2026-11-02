using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Profiling;

/// <summary>Finds the focal lengths a photographer gravitates to (modes of a smoothed log-focal histogram).</summary>
internal sealed class FocalPeakFinder
{
    private const double MinFocal = 6;
    private const double MaxFocal = 2000;
    private const int BinsPerStop = 12;

    private static readonly int BinCount = (int)Math.Ceiling(Math.Log2(MaxFocal / MinFocal) * BinsPerStop);

    private readonly double[] _bins = new double[BinCount];
    private readonly Dictionary<int, double>[] _exact = Enumerable.Range(0, BinCount).Select(_ => new Dictionary<int, double>()).ToArray();
    private double _total;

    public void Add(double? focal35, double weight = 1)
    {
        if (focal35 is not { } f || !double.IsFinite(f) || f <= 0)
        {
            return;
        }

        var i = Index(f);
        _bins[i] += weight;
        var rounded = (int)Math.Round(f);
        _exact[i][rounded] = _exact[i].GetValueOrDefault(rounded) + weight;
        _total += weight;
    }

    private static int Index(double f) => Math.Clamp((int)Math.Floor(Math.Log2(Math.Clamp(f, MinFocal, MaxFocal) / MinFocal) * BinsPerStop), 0, BinCount - 1);

    private static double Edge(int i) => MinFocal * Math.Pow(2, (double)i / BinsPerStop);

    public IReadOnlyList<FocalPeak> Find(double minShare = 0.05, int max = 6)
    {
        if (_total <= 0)
        {
            return [];
        }

        // Gaussian smoothing (σ = 1.5 bins ≈ ⅛ stop) keeps distinct primes apart while merging zoom jitter.
        const int radius = 4;
        const double sigma = 1.5;
        var kernel = Enumerable.Range(-radius, (2 * radius) + 1).Select(k => Math.Exp(-(k * k) / (2 * sigma * sigma))).ToArray();
        var smooth = new double[BinCount];
        for (var i = 0; i < BinCount; i++)
        {
            double sum = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var j = i + k;
                if (j >= 0 && j < BinCount)
                {
                    sum += _bins[j] * kernel[k + radius];
                }
            }

            smooth[i] = sum;
        }

        var peaks = new List<FocalPeak>();
        var claimedUpTo = -1;
        for (var i = 0; i < BinCount; i++)
        {
            var left = i == 0 ? -1 : smooth[i - 1];
            var right = i == BinCount - 1 ? -1 : smooth[i + 1];
            if (smooth[i] <= 0 || smooth[i] < left || smooth[i] <= right)
            {
                continue;
            }

            // Basin: walk outwards while the smoothed curve keeps descending.
            // Basins of neighbouring peaks meet in a shared valley bin; it belongs to the left peak only, so shares never overlap.
            var lo = i;
            while (lo > claimedUpTo + 1 && smooth[lo - 1] > 0 && smooth[lo - 1] <= smooth[lo])
            {
                lo--;
            }

            var hi = i;
            while (hi < BinCount - 1 && smooth[hi + 1] > 0 && smooth[hi + 1] <= smooth[hi])
            {
                hi++;
            }

            claimedUpTo = hi;
            double mass = 0;
            var exact = new Dictionary<int, double>();
            for (var j = lo; j <= hi; j++)
            {
                mass += _bins[j];
                foreach (var (focal, w) in _exact[j])
                {
                    exact[focal] = exact.GetValueOrDefault(focal) + w;
                }
            }

            var share = mass / _total;
            if (share < minShare || exact.Count == 0)
            {
                continue;
            }

            var mode = exact.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            var lower = exact.Keys.Min();
            var upper = exact.Keys.Max();
            peaks.Add(new FocalPeak(mode, share, Math.Max(lower, Edge(lo)), Math.Min(upper, Edge(hi + 1))));
        }

        return [.. peaks.OrderByDescending(p => p.Share).Take(max)];
    }

    public static string Describe(FocalPeak peak) => peak.Focal35Mm.ToString("0", CultureInfo.InvariantCulture) + "mm";
}

/// <summary>Library-wide palette: frame palettes binned into a 15-bit RGB grid, then greedily de-duplicated.</summary>
internal sealed class PaletteAccumulator
{
    private readonly double[] _weight = new double[32 * 32 * 32];
    private readonly double[] _r = new double[32 * 32 * 32];
    private readonly double[] _g = new double[32 * 32 * 32];
    private readonly double[] _b = new double[32 * 32 * 32];

    public void Add(IReadOnlyList<PaletteColor>? palette, double weight = 1)
    {
        if (palette is null)
        {
            return;
        }

        foreach (var color in palette)
        {
            if (!TryParseHex(color.Hex, out var r, out var g, out var b) || !(color.Weight > 0))
            {
                continue;
            }

            var cell = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            var w = color.Weight * weight;
            _weight[cell] += w;
            _r[cell] += r * w;
            _g[cell] += g * w;
            _b[cell] += b * w;
        }
    }

    public IReadOnlyList<PaletteColor> Build(int max = 8, double minDistance = 48)
    {
        var total = _weight.Sum();
        if (total <= 0)
        {
            return [];
        }

        var cells = Enumerable.Range(0, _weight.Length).Where(i => _weight[i] > 0).OrderByDescending(i => _weight[i]).ToList();
        var picked = new List<(double R, double G, double B, double W)>();
        foreach (var cell in cells)
        {
            var w = _weight[cell];
            var color = (R: _r[cell] / w, G: _g[cell] / w, B: _b[cell] / w);
            var merged = false;
            for (var i = 0; i < picked.Count; i++)
            {
                var p = picked[i];
                var d = Math.Sqrt(Math.Pow(p.R - color.R, 2) + Math.Pow(p.G - color.G, 2) + Math.Pow(p.B - color.B, 2));
                if (d < minDistance)
                {
                    picked[i] = p with { W = p.W + w };
                    merged = true;
                    break;
                }
            }

            if (!merged && picked.Count < max * 4)
            {
                picked.Add((color.R, color.G, color.B, w));
            }
        }

        return [.. picked.OrderByDescending(p => p.W).Take(max).Select(p => new PaletteColor(ToHex(p.R, p.G, p.B), p.W / total))];
    }

    private static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex))
        {
            return false;
        }

        var span = hex.AsSpan().TrimStart('#');
        if (span.Length != 6 || !int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        r = (value >> 16) & 0xFF;
        g = (value >> 8) & 0xFF;
        b = value & 0xFF;
        return true;
    }

    private static string ToHex(double r, double g, double b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(int)Math.Round(r):X2}{(int)Math.Round(g):X2}{(int)Math.Round(b):X2}");
}
