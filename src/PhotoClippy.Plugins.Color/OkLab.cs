using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PhotoClippy.Plugins.Color;

/// <summary>sRGB ↔ OKLab (Björn Ottosson, 2020) helpers.</summary>
internal static class OkLab
{
    /// <summary>8-bit sRGB code → linear light (IEC 61966-2-1 transfer function).</summary>
    public static readonly float[] SrgbToLinear = BuildLinearLut();

    /// <summary>1 / v for v in 0..255 (0 → 0); used for HSV saturation.</summary>
    public static readonly float[] Reciprocal = BuildReciprocalLut();

    private static float[] BuildLinearLut()
    {
        var lut = new float[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var c = i / 255.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        return lut;
    }

    private static float[] BuildReciprocalLut()
    {
        var lut = new float[256];
        for (var i = 1; i < lut.Length; i++)
        {
            lut[i] = 1f / i;
        }

        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FromLinear(float r, float g, float b, out float lightness, out float a, out float bb)
    {
        var l = Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        lightness = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        a = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        bb = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FromSrgb(byte r, byte g, byte b, out float lightness, out float a, out float bb)
    {
        var lut = SrgbToLinear;
        FromLinear(lut[r], lut[g], lut[b], out lightness, out a, out bb);
    }

    /// <summary>
    /// Cube root for x in [0, ~1]: exponent bit-hack seed refined by two Halley steps
    /// (relative error &lt; 1e-7, i.e. float precision) — several times faster than <see cref="MathF.Cbrt"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cbrt(float x)
    {
        if (x <= 1e-30f)
        {
            return 0f;
        }

        var y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) / 3 + 709_921_077);
        var y3 = y * y * y;
        y *= (y3 + 2f * x) / (2f * y3 + x);
        y3 = y * y * y;
        y *= (y3 + 2f * x) / (2f * y3 + x);
        return y;
    }

    /// <summary>OKLab hue angle atan2(b, a) in degrees, [0, 360). Polynomial atan, max error ≈ 0.001°.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HueDegrees(float a, float b)
    {
        var ax = MathF.Abs(a);
        var ay = MathF.Abs(b);
        var mx = MathF.Max(ax, ay);
        if (mx <= 0f)
        {
            return 0f;
        }

        var t = MathF.Min(ax, ay) / mx;
        var t2 = t * t;
        var r = t * (0.99997726f + t2 * (-0.33262347f + t2 * (0.19354346f + t2 * (-0.11643287f + t2 * (0.05265332f + t2 * -0.01172120f)))));
        if (ay > ax)
        {
            r = MathF.PI / 2f - r;
        }

        if (a < 0f)
        {
            r = MathF.PI - r;
        }

        var deg = r * (180f / MathF.PI);
        if (b < 0f)
        {
            deg = 360f - deg;
        }

        return deg >= 360f ? deg - 360f : deg;
    }

    /// <summary>
    /// Planar row conversion: linear RGB → OKLab lightness (clamped to 0..1), a, b, chroma and hue (degrees).
    /// Uses 128-bit SIMD over the whole span length (callers pad rows to a multiple of 4 so every pixel takes
    /// the same code path); numerically equivalent to <see cref="FromLinear"/> + <see cref="HueDegrees"/>.
    /// </summary>
    public static void ConvertRow(
        ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        Span<float> lightness, Span<float> a, Span<float> bb, Span<float> chroma, Span<float> hue)
    {
        var n = r.Length;
        if (g.Length < n || b.Length < n || lightness.Length < n || a.Length < n || bb.Length < n || chroma.Length < n || hue.Length < n)
        {
            throw new ArgumentException("All row spans must be at least as long as the red span.");
        }

        var i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref var rr = ref MemoryMarshal.GetReference(r);
            ref var gr = ref MemoryMarshal.GetReference(g);
            ref var br = ref MemoryMarshal.GetReference(b);
            ref var lo = ref MemoryMarshal.GetReference(lightness);
            ref var ao = ref MemoryMarshal.GetReference(a);
            ref var bo = ref MemoryMarshal.GetReference(bb);
            ref var co = ref MemoryMarshal.GetReference(chroma);
            ref var ho = ref MemoryMarshal.GetReference(hue);
            var zero = Vector128<float>.Zero;
            var one = Vector128.Create(1f);
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                var off = (nuint)i;
                var vr = Vector128.LoadUnsafe(ref rr, off);
                var vg = Vector128.LoadUnsafe(ref gr, off);
                var vb = Vector128.LoadUnsafe(ref br, off);
                var l = Cbrt(vr * 0.4122214708f + vg * 0.5363325363f + vb * 0.0514459929f);
                var m = Cbrt(vr * 0.2119034982f + vg * 0.6806995451f + vb * 0.1073969566f);
                var s = Cbrt(vr * 0.0883024619f + vg * 0.2817188376f + vb * 0.6299787005f);
                var vl = l * 0.2104542553f + m * 0.7936177850f - s * 0.0040720468f;
                var va = l * 1.9779984951f - m * 2.4285922050f + s * 0.4505937099f;
                var vbb = l * 0.0259040371f + m * 0.7827717662f - s * 0.8086757660f;
                Vector128.Min(Vector128.Max(vl, zero), one).StoreUnsafe(ref lo, off);
                va.StoreUnsafe(ref ao, off);
                vbb.StoreUnsafe(ref bo, off);
                Vector128.Sqrt(va * va + vbb * vbb).StoreUnsafe(ref co, off);
                HueDegrees(va, vbb).StoreUnsafe(ref ho, off);
            }
        }

        for (; i < n; i++)
        {
            FromLinear(r[i], g[i], b[i], out var l, out var oa, out var ob);
            lightness[i] = Math.Clamp(l, 0f, 1f);
            a[i] = oa;
            bb[i] = ob;
            chroma[i] = MathF.Sqrt(oa * oa + ob * ob);
            hue[i] = HueDegrees(oa, ob);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Cbrt(Vector128<float> x)
    {
        // Same seed + two Halley steps as the scalar Cbrt; the bits/3 division is done in float (seed only).
        var seed = Vector128.ConvertToInt32(Vector128.ConvertToSingle(x.AsInt32()) * (1f / 3f)) + Vector128.Create(709_921_077);
        var y = seed.AsSingle();
        var x2 = x + x;
        var y3 = y * y * y;
        y *= (y3 + x2) / (y3 + y3 + x);
        y3 = y * y * y;
        y *= (y3 + x2) / (y3 + y3 + x);
        return Vector128.ConditionalSelect(Vector128.GreaterThan(x, Vector128.Create(1e-30f)), y, Vector128<float>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> HueDegrees(Vector128<float> a, Vector128<float> b)
    {
        var zero = Vector128<float>.Zero;
        var ax = Vector128.Abs(a);
        var ay = Vector128.Abs(b);
        var mx = Vector128.Max(ax, ay);
        var t = Vector128.Min(ax, ay) / Vector128.Max(mx, Vector128.Create(1e-30f));
        var t2 = t * t;
        var poly = Vector128.Create(-0.01172120f);
        poly = poly * t2 + Vector128.Create(0.05265332f);
        poly = poly * t2 + Vector128.Create(-0.11643287f);
        poly = poly * t2 + Vector128.Create(0.19354346f);
        poly = poly * t2 + Vector128.Create(-0.33262347f);
        poly = poly * t2 + Vector128.Create(0.99997726f);
        var r = t * poly;
        r = Vector128.ConditionalSelect(Vector128.GreaterThan(ay, ax), Vector128.Create(MathF.PI / 2f) - r, r);
        r = Vector128.ConditionalSelect(Vector128.LessThan(a, zero), Vector128.Create(MathF.PI) - r, r);
        var deg = r * (180f / MathF.PI);
        deg = Vector128.ConditionalSelect(Vector128.LessThan(b, zero), Vector128.Create(360f) - deg, deg);
        var full = Vector128.Create(360f);
        return Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(deg, full), deg - full, deg);
    }

    /// <summary>OKLab → gamut-clipped 8-bit sRGB "#RRGGBB" (upper-case).</summary>
    public static string ToHex(double lightness, double a, double b)
    {
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        var r = Encode(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s);
        var g = Encode(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s);
        var bl = Encode(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        return string.Create(7, (r, g, bl), static (span, c) =>
        {
            const string digits = "0123456789ABCDEF";
            span[0] = '#';
            span[1] = digits[c.r >> 4];
            span[2] = digits[c.r & 0xF];
            span[3] = digits[c.g >> 4];
            span[4] = digits[c.g & 0xF];
            span[5] = digits[c.bl >> 4];
            span[6] = digits[c.bl & 0xF];
        });
    }

    private static int Encode(double linear)
    {
        linear = Math.Clamp(linear, 0.0, 1.0);
        var v = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        return Math.Clamp((int)Math.Round(v * 255.0, MidpointRounding.AwayFromZero), 0, 255);
    }
}
