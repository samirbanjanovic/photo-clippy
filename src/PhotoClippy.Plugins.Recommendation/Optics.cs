using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

internal static class Optics
{
    public const double ZoomSigma = 0.08;
    public const double ZoomBand = 0.05;
    public static readonly double CeilingFactor = Math.Pow(2, 1.0 / 6);
    public static readonly double Ln105 = Math.Log(1.05);
    public static readonly double Ln115 = Math.Log(1.15);
    public static readonly double[] CommonEq = [14, 20, 24, 28, 35, 40, 50, 85, 105, 135, 200];

    public static CultureInfo Inv => CultureInfo.InvariantCulture;

    public static double Stops(double from, double to) => 2 * Math.Log2(from / to);

    public static double Gauss(double d, double sigma) => Math.Exp(-(d * d) / (2 * sigma * sigma));

    public static double Coverage(double lnF, double lnMin, double lnMax, bool zoom, double primeSigma)
    {
        if (!zoom)
        {
            return Gauss(lnF - lnMin, primeSigma);
        }

        if (lnF < lnMin)
        {
            return Gauss(lnMin - lnF, ZoomSigma);
        }

        return lnF > lnMax ? Gauss(lnF - lnMax, ZoomSigma) : 1.0;
    }

    public static double Overlap(double a1, double a2, double b1, double b2) => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));

    /// <summary>0 when going &lt;5 % further than the owned end, 1 from 15 % further, linear in log focal in between.</summary>
    public static double ReachRamp(double lnFurther) => Math.Clamp((lnFurther - Ln105) / (Ln115 - Ln105), 0, 1);

    public static bool Valid(double? v, double min, double max) => v is { } x && double.IsFinite(x) && x >= min && x <= max;

    public static string Num(double v, string format = "0.#") => v.ToString(format, Inv);

    public static string Mm(double v) => Num(v) + "mm";

    public static string FNum(double n) => "f/" + Num(n);

    public static string Pct(double share) => Num(Math.Round(share * 100), "0") + "%";

    public static string Eq(double lo, double hi)
    {
        var a = Math.Round(lo);
        var b = Math.Round(hi);
        return a >= b ? Num(a, "0") + "mm-eq" : Num(a, "0") + "–" + Num(b, "0") + "mm-eq";
    }

    public static string StopsText(double stops)
    {
        if (stops < 0.5)
        {
            return "⅓ stop";
        }

        if (stops < 0.85)
        {
            return "⅔ stop";
        }

        if (stops < 1.25)
        {
            return "a stop";
        }

        return "~" + Num(Math.Round(stops * 2) / 2) + " stops";
    }

    public static string Iso(double iso) => Num(Math.Round(iso), "0");

    private static readonly int[] NominalIso =
    [
        100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400,
        8000, 10000, 12800, 16000, 20000, 25600, 32000, 40000, 51200, 64000, 80000, 102400,
    ];

    /// <summary>Snaps an ISO value to the nearest nominal third-stop setting; "base ISO" below 100.</summary>
    public static string IsoNominal(double iso)
    {
        if (!(iso >= 90))
        {
            return "base ISO";
        }

        var best = NominalIso[0];
        var bestD = double.MaxValue;
        foreach (var v in NominalIso)
        {
            var d = Math.Abs(Math.Log(iso / v));
            if (d < bestD)
            {
                bestD = d;
                best = v;
            }
        }

        return best.ToString(Inv);
    }

    public static double WeightedMedian(List<(double Value, double Weight)> items)
    {
        if (items.Count == 0)
        {
            return double.NaN;
        }

        items.Sort((a, b) => a.Value.CompareTo(b.Value));
        var total = 0.0;
        foreach (var (_, w) in items)
        {
            total += w;
        }

        var half = total / 2;
        var acc = 0.0;
        foreach (var (v, w) in items)
        {
            acc += w;
            if (acc >= half)
            {
                return v;
            }
        }

        return items[^1].Value;
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((pos - lo) * (sorted[hi] - sorted[lo]));
    }
}

/// <summary>A lens (native spec) as seen on the target body: 35 mm-eq range and log-focal bounds.</summary>
internal sealed class Optic
{
    public Optic(LensSpec spec, double crop, string display)
    {
        Spec = spec;
        Crop = crop;
        Display = display;
        Min35 = spec.FocalMinMm * crop;
        Max35 = Math.Max(spec.FocalMaxMm, spec.FocalMinMm) * crop;
        LnMin = Math.Log(Min35);
        LnMax = Math.Log(Max35);
        IsZoom = spec.IsZoom;
        Fastest = Math.Min(spec.MaxApertureWide, spec.MaxApertureTele);
    }

    public LensSpec Spec { get; }
    public double Crop { get; }
    public string Display { get; }
    public double Min35 { get; }
    public double Max35 { get; }
    public double LnMin { get; }
    public double LnMax { get; }
    public bool IsZoom { get; }
    public double Fastest { get; }

    public double NativeAtEq(double f35) => Math.Clamp(f35 / Crop, Spec.FocalMinMm, Math.Max(Spec.FocalMaxMm, Spec.FocalMinMm));

    public double ApertureAtEq(double f35) => Spec.MaxApertureAt(f35 / Crop);

    public double Coverage(double lnF, double primeSigma) => Optics.Coverage(lnF, LnMin, LnMax, IsZoom, primeSigma);

    /// <summary>Log-focal band used for overlap tests: the zoom range ± 0.05 or the prime ± σ.</summary>
    public (double Lo, double Hi) Band(double primeSigma) => IsZoom
        ? (LnMin - Optics.ZoomBand, LnMax + Optics.ZoomBand)
        : (LnMin - primeSigma, LnMax + primeSigma);

    /// <summary>Equivalent focal range where coverage ≥ 0.5.</summary>
    public (double Lo, double Hi) Zone(double primeSigma)
    {
        if (IsZoom)
        {
            return (Min35, Max35);
        }

        var half = primeSigma * Math.Sqrt(2 * Math.Log(2));
        return (Min35 * Math.Exp(-half), Min35 * Math.Exp(half));
    }
}

internal sealed class Candidate
{
    public required Optic Optic { get; init; }
    public required int Index { get; init; }
    public Optic? OwnedMatch { get; set; }
    public bool AlreadyOwned => OwnedMatch is not null;
    public double Redundancy { get; set; }
    public Optic? RedundantWith { get; set; }
    public double Overlap { get; set; }
    public Optic? OverlapWith { get; set; }
    public double Multiplier { get; set; } = 1;
    public LensSpec Spec => Optic.Spec;
}

internal sealed record GenreInfo(string Id, string Name, GenreAffinity Affinity, double LnMin, double LnMax);

/// <summary>xoshiro256** seeded by SplitMix64; fast, deterministic per (seed, stream).</summary>
internal struct Xoshiro256
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public Xoshiro256(ulong seed, ulong stream)
    {
        var x = (seed * 0x9E3779B97F4A7C15UL) ^ ((stream + 1) * 0xD1B54A32D192ED03UL);
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public ulong Next()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        var z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
