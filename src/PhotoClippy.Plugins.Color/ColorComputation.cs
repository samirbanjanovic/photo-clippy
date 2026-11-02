using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Color;

/// <summary>
/// Tonal and colour statistics over a working-size RGB24 image. One pass converts every pixel to OKLab
/// (sRGB LUT → linear → LMS → cube root) and feeds all accumulators; lightness is kept in a pooled plane for
/// the Laplacian; everything else is reduced from fixed-size histograms and sums.
/// </summary>
/// <remarks>
/// Definitions where the contract leaves room:
/// <list type="bullet">
/// <item><b>Lightness</b> is OKLab L (0..1; sRGB mid-grey 128 ≈ 0.60). Percentiles come from a 256-bin histogram
/// with per-bin min/max interpolation, so flat images yield exact values and zero contrast.</item>
/// <item><b>Key</b>: "low" when P50 &lt; 0.35 and P95 &lt; 0.75; "high" when P50 &gt; 0.70 and P05 &gt; 0.35;
/// otherwise "mid".</item>
/// <item><b>Monochrome</b>: (share of pixels with chroma &gt; 0.04 is below 1.5 % and mean chroma &lt; 0.02) or a
/// toned image (circular, chroma-weighted hue standard deviation of pixels with chroma &gt; 0.02 below 12° and
/// mean chroma &lt; 0.05) — catches sepia, selenium and cyanotype prints.</item>
/// <item><b>Warmth/Tint</b>: 5 × mean OKLab b / a over mid-tone pixels (0.15 &lt; L &lt; 0.92), clamped to −1..1;
/// when an image has no mid-tones at all every pixel is used.</item>
/// <item><b>Sharpness</b>: variance of the 4-neighbour 3×3 Laplacian of L×255 over interior pixels, mapped by
/// 1 − exp(−var / <see cref="SharpnessScale"/>). Crisp edges saturate towards 1, a 9×9 box blur of 32 px squares
/// lands near 0.2. Depends on the working size, hence on <see cref="ColorSettings.WorkingEdge"/>.</item>
/// <item><b>HueHistogram</b>: 12 bins of 30° on the OKLab hue angle atan2(b, a); bin i is centred on i × 30°
/// (bin 0 covers [−15°, 15°) — pinkish red; sRGB red ≈ 29° falls in bin 1, green ≈ 142° in bin 5, blue ≈ 264° in
/// bin 9). Pixels with chroma &gt; 0.02 contribute their chroma as weight. Null when the chroma mass is negligible.</item>
/// <item><b>Saturation</b>: mean HSV saturation over pixels whose max channel exceeds 13 (near-black excluded).</item>
/// </list>
/// Extras (all shares 0..1 of the working pixels): <c>color.skyBlueShare</c> (hue 215–268°, chroma 0.025–0.22,
/// L 0.50–0.97), <c>color.skyBlueTopShare</c> (same band, share of the top third of the frame),
/// <c>color.greenShare</c> (foliage: hue 115–165°, chroma ≥ 0.035, L 0.20–0.90), <c>color.skinShare</c>
/// (conservative skin band: hue 40–78°, chroma 0.045–0.125, L 0.50–0.92), <c>color.neutralShare</c>
/// (chroma &lt; 0.02), <c>color.lumaStdDev</c> (std-dev of L) and <c>color.hueConcentration</c>
/// (0..1 mean resultant length of chroma-weighted hues; absent for greyscale).
/// </remarks>
internal static class ColorComputation
{
    public const double SharpnessScale = 400.0;
    public const double LowKeyP50 = 0.35;
    public const double LowKeyP95 = 0.75;
    public const double HighKeyP50 = 0.70;
    public const double HighKeyP05 = 0.35;

    private const float ChromaticThreshold = 0.02f;
    private const float MonoChromaThreshold = 0.04f;

    public static ColorFeatures Compute(ReadOnlySpan<byte> rgb, int width, int height, ColorSettings settings)
    {
        var n = width * height;
        var lPlane = ArrayPool<float>.Shared.Rent(n);
        try
        {
            var acc = new Accumulators();
            Span<int> hist = stackalloc int[256];
            Span<float> binMin = stackalloc float[256];
            Span<float> binMax = stackalloc float[256];
            Span<double> hueHist = stackalloc double[12];
            hist.Clear();
            binMin.Fill(float.MaxValue);
            binMax.Fill(float.MinValue);
            hueHist.Clear();

            Accumulate(rgb, width, height, lPlane, ref acc, hist, binMin, binMax, hueHist);
            var laplacianVariance = LaplacianVariance(lPlane.AsSpan(0, n), width, height);
            var palette = PaletteExtractor.Extract(rgb, width, height, settings.PaletteSize, settings.MaxPaletteSamples);
            return Build(ref acc, n, width, height, hist, binMin, binMax, hueHist, laplacianVariance, palette);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(lPlane);
        }
    }

    private struct Accumulators
    {
        public long ShadowClip, HighlightClip;
        public long SumRg, SumRg2, SumYb, SumYb2;
        public double SatSum;
        public long SatCount;
        public double SumL, SumL2, SumC;
        public long Chroma04Count, NeutralCount;
        public double MidA, MidB, AllA, AllB;
        public long MidCount;
        public double HueA, HueB, HueC;
        public long Sky, SkyTop, Green, Skin;
    }

    /// <summary>
    /// Single pass over the image, row by row: (1) integer statistics and sRGB → linear via LUT into planar row
    /// buffers, (2) SIMD OKLab/chroma/hue conversion of the row, (3) histogram and band reductions.
    /// </summary>
    private static void Accumulate(
        ReadOnlySpan<byte> rgb, int width, int height, float[] lPlane, ref Accumulators acc,
        Span<int> hist, Span<float> binMin, Span<float> binMax, Span<double> hueHist)
    {
        var lut = OkLab.SrgbToLinear;
        var recip = OkLab.Reciprocal;
        var lanes = Vector128<float>.Count;
        var padded = (width + lanes - 1) / lanes * lanes;
        var rowBuffer = ArrayPool<float>.Shared.Rent(padded * 8);
        try
        {
            var rows = rowBuffer.AsSpan(0, padded * 8);
            rows.Clear();
            var rLin = rows.Slice(0, padded);
            var gLin = rows.Slice(padded, padded);
            var bLin = rows.Slice(padded * 2, padded);
            var lRow = rows.Slice(padded * 3, padded);
            var aRow = rows.Slice(padded * 4, padded);
            var bRow = rows.Slice(padded * 5, padded);
            var cRow = rows.Slice(padded * 6, padded);
            var hRow = rows.Slice(padded * 7, padded);
            ref var rl = ref MemoryMarshal.GetReference(rLin);
            ref var gl = ref MemoryMarshal.GetReference(gLin);
            ref var bl = ref MemoryMarshal.GetReference(bLin);
            ref var lr = ref MemoryMarshal.GetReference(lRow);
            ref var ar = ref MemoryMarshal.GetReference(aRow);
            ref var brr = ref MemoryMarshal.GetReference(bRow);
            ref var cr = ref MemoryMarshal.GetReference(cRow);
            ref var hr = ref MemoryMarshal.GetReference(hRow);
            ref var lutRef = ref MemoryMarshal.GetArrayDataReference(lut);
            ref var recipRef = ref MemoryMarshal.GetArrayDataReference(recip);
            ref var src = ref MemoryMarshal.GetReference(rgb);
            ref var histRef = ref MemoryMarshal.GetReference(hist);
            ref var minRef = ref MemoryMarshal.GetReference(binMin);
            ref var maxRef = ref MemoryMarshal.GetReference(binMax);

            var topRows = (height + 2) / 3;
            long shadowClip = 0, highlightClip = 0, satCount = 0;
            long sumRg = 0, sumRg2 = 0, sumYb = 0, sumYb2 = 0;
            double satSum = 0, sumL = 0, sumL2 = 0, sumC = 0;
            long chroma04 = 0, neutral = 0, midCount = 0;
            double midA = 0, midB = 0, allA = 0, allB = 0, hueA = 0, hueB = 0, hueC = 0;
            long sky = 0, skyTop = 0, green = 0, skin = 0;

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * width;
                ref var px = ref Unsafe.Add(ref src, rowStart * 3);
                long rowRg = 0, rowRg2 = 0, rowYb = 0, rowYb2 = 0;
                float rowSat = 0;
                for (var x = 0; x < width; x++)
                {
                    int r = Unsafe.Add(ref px, x * 3);
                    int g = Unsafe.Add(ref px, x * 3 + 1);
                    int b = Unsafe.Add(ref px, x * 3 + 2);
                    Unsafe.Add(ref rl, x) = Unsafe.Add(ref lutRef, r);
                    Unsafe.Add(ref gl, x) = Unsafe.Add(ref lutRef, g);
                    Unsafe.Add(ref bl, x) = Unsafe.Add(ref lutRef, b);
                    var max = Math.Max(r, Math.Max(g, b));
                    if (max <= 3)
                    {
                        shadowClip++;
                    }
                    else if (max >= 252)
                    {
                        highlightClip++;
                    }

                    var rg = r - g;
                    var yb = r + g - 2 * b;
                    rowRg += rg;
                    rowRg2 += rg * rg;
                    rowYb += yb;
                    rowYb2 += yb * yb;

                    if (max > 13)
                    {
                        var min = Math.Min(r, Math.Min(g, b));
                        rowSat += (max - min) * Unsafe.Add(ref recipRef, max);
                        satCount++;
                    }
                }

                sumRg += rowRg;
                sumRg2 += rowRg2;
                sumYb += rowYb;
                sumYb2 += rowYb2;
                satSum += rowSat;

                OkLab.ConvertRow(rLin, gLin, bLin, lRow, aRow, bRow, cRow, hRow);
                lRow.Slice(0, width).CopyTo(lPlane.AsSpan(rowStart, width));

                var top = y < topRows;
                float rowL = 0, rowL2 = 0, rowC = 0, rowAllA = 0, rowAllB = 0, rowMidA = 0, rowMidB = 0;
                for (var x = 0; x < width; x++)
                {
                    var l = Unsafe.Add(ref lr, x);
                    var oa = Unsafe.Add(ref ar, x);
                    var ob = Unsafe.Add(ref brr, x);
                    var c = Unsafe.Add(ref cr, x);
                    var bin = Math.Min(255, (int)(l * 256f));
                    Unsafe.Add(ref histRef, bin)++;
                    ref var bMin = ref Unsafe.Add(ref minRef, bin);
                    if (l < bMin)
                    {
                        bMin = l;
                    }

                    ref var bMax = ref Unsafe.Add(ref maxRef, bin);
                    if (l > bMax)
                    {
                        bMax = l;
                    }

                    rowL += l;
                    rowL2 += l * l;
                    rowC += c;
                    rowAllA += oa;
                    rowAllB += ob;
                    if (l > 0.15f && l < 0.92f)
                    {
                        rowMidA += oa;
                        rowMidB += ob;
                        midCount++;
                    }

                    if (c > MonoChromaThreshold)
                    {
                        chroma04++;
                    }

                    if (c < ChromaticThreshold)
                    {
                        neutral++;
                        continue;
                    }

                    var hue = Unsafe.Add(ref hr, x);
                    var hb = (int)((hue + 15f) * (1f / 30f));
                    hueHist[hb >= 12 ? hb - 12 : hb] += c;
                    hueA += oa;
                    hueB += ob;
                    hueC += c;

                    if (hue >= 215f && hue <= 268f && c >= 0.025f && c <= 0.22f && l >= 0.50f && l <= 0.97f)
                    {
                        sky++;
                        if (top)
                        {
                            skyTop++;
                        }
                    }
                    else if (hue >= 115f && hue <= 165f && c >= 0.035f && l >= 0.20f && l <= 0.90f)
                    {
                        green++;
                    }
                    else if (hue >= 40f && hue <= 78f && c >= 0.045f && c <= 0.125f && l >= 0.50f && l <= 0.92f)
                    {
                        skin++;
                    }
                }

                sumL += rowL;
                sumL2 += rowL2;
                sumC += rowC;
                allA += rowAllA;
                allB += rowAllB;
                midA += rowMidA;
                midB += rowMidB;
            }

            acc.ShadowClip = shadowClip;
            acc.HighlightClip = highlightClip;
            acc.SumRg = sumRg;
            acc.SumRg2 = sumRg2;
            acc.SumYb = sumYb;
            acc.SumYb2 = sumYb2;
            acc.SatSum = satSum;
            acc.SatCount = satCount;
            acc.SumL = sumL;
            acc.SumL2 = sumL2;
            acc.SumC = sumC;
            acc.Chroma04Count = chroma04;
            acc.NeutralCount = neutral;
            acc.MidA = midA;
            acc.MidB = midB;
            acc.AllA = allA;
            acc.AllB = allB;
            acc.MidCount = midCount;
            acc.HueA = hueA;
            acc.HueB = hueB;
            acc.HueC = hueC;
            acc.Sky = sky;
            acc.SkyTop = skyTop;
            acc.Green = green;
            acc.Skin = skin;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowBuffer);
        }
    }

    /// <summary>Population variance of the 4-neighbour Laplacian of L×255 over interior pixels; null below 3×3.</summary>
    private static double? LaplacianVariance(ReadOnlySpan<float> l, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return null;
        }

        var lanes = Vector128<float>.Count;
        var four = Vector128.Create(4f);
        ref var plane = ref MemoryMarshal.GetReference(l);
        double sum = 0, sum2 = 0;
        for (var y = 1; y < height - 1; y++)
        {
            ref var up = ref Unsafe.Add(ref plane, (y - 1) * width);
            ref var mid = ref Unsafe.Add(ref plane, y * width);
            ref var down = ref Unsafe.Add(ref plane, (y + 1) * width);
            var vSum = Vector128<float>.Zero;
            var vSum2 = Vector128<float>.Zero;
            var x = 1;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x + lanes <= width - 1; x += lanes)
                {
                    var o = (nuint)x;
                    var v = four * Vector128.LoadUnsafe(ref mid, o)
                        - Vector128.LoadUnsafe(ref mid, o - 1)
                        - Vector128.LoadUnsafe(ref mid, o + 1)
                        - Vector128.LoadUnsafe(ref up, o)
                        - Vector128.LoadUnsafe(ref down, o);
                    vSum += v;
                    vSum2 += v * v;
                }
            }

            var rowSum = Vector128.Sum(vSum);
            var rowSum2 = Vector128.Sum(vSum2);
            for (; x < width - 1; x++)
            {
                var v = 4f * Unsafe.Add(ref mid, x) - Unsafe.Add(ref mid, x - 1) - Unsafe.Add(ref mid, x + 1)
                    - Unsafe.Add(ref up, x) - Unsafe.Add(ref down, x);
                rowSum += v;
                rowSum2 += v * v;
            }

            sum += rowSum;
            sum2 += rowSum2;
        }

        var count = (double)(width - 2) * (height - 2);
        var mean = sum / count;
        return Math.Max(0.0, sum2 / count - mean * mean) * 255.0 * 255.0;
    }

    private static ColorFeatures Build(
        ref Accumulators acc, int n, int width, int height,
        ReadOnlySpan<int> hist, ReadOnlySpan<float> binMin, ReadOnlySpan<float> binMax, ReadOnlySpan<double> hueHist,
        double? laplacianVariance, IReadOnlyList<PaletteColor> palette)
    {
        var inv = 1.0 / n;
        var p05 = Percentile(hist, binMin, binMax, n, 0.05);
        var p50 = Percentile(hist, binMin, binMax, n, 0.50);
        var p95 = Percentile(hist, binMin, binMax, n, 0.95);
        var meanL = acc.SumL * inv;
        var lumaStd = Math.Sqrt(Math.Max(0.0, acc.SumL2 * inv - meanL * meanL));

        var lumaHistogram = new double[16];
        for (var i = 0; i < 256; i++)
        {
            lumaHistogram[i >> 4] += hist[i];
        }

        for (var i = 0; i < 16; i++)
        {
            lumaHistogram[i] *= inv;
        }

        var key = p50 < LowKeyP50 && p95 < LowKeyP95 ? "low"
            : p50 > HighKeyP50 && p05 > HighKeyP05 ? "high"
            : "mid";

        var muRg = acc.SumRg * inv;
        var varRg = Math.Max(0.0, acc.SumRg2 * inv - muRg * muRg);
        var muYb = acc.SumYb * inv * 0.5;
        var varYb = Math.Max(0.0, acc.SumYb2 * inv * 0.25 - muYb * muYb);
        var hasler = Math.Sqrt(varRg + varYb) + 0.3 * Math.Sqrt(muRg * muRg + muYb * muYb);
        var colorfulness = Math.Min(1.0, hasler / 110.0);

        var meanChroma = acc.SumC * inv;
        var saturation = acc.SatCount > 0 ? acc.SatSum / acc.SatCount : 0.0;

        double? hueConcentration = null;
        var hueStdDeg = double.PositiveInfinity;
        if (acc.HueC > 0)
        {
            var resultant = Math.Min(1.0, Math.Sqrt(acc.HueA * acc.HueA + acc.HueB * acc.HueB) / acc.HueC);
            hueConcentration = resultant;
            hueStdDeg = resultant > 0 ? Math.Sqrt(-2.0 * Math.Log(resultant)) * (180.0 / Math.PI) : double.PositiveInfinity;
        }

        var chromaticShare = acc.Chroma04Count * inv;
        var monochrome = (chromaticShare < 0.015 && meanChroma < 0.02) || (hueStdDeg < 12.0 && meanChroma < 0.05);

        double meanA, meanB;
        if (acc.MidCount > 0)
        {
            meanA = acc.MidA / acc.MidCount;
            meanB = acc.MidB / acc.MidCount;
        }
        else
        {
            meanA = acc.AllA * inv;
            meanB = acc.AllB * inv;
        }

        double[]? hueHistogram = null;
        if (acc.HueC > 1e-4 * n)
        {
            hueHistogram = new double[12];
            var total = 0.0;
            for (var i = 0; i < 12; i++)
            {
                total += hueHist[i];
            }

            for (var i = 0; i < 12; i++)
            {
                hueHistogram[i] = hueHist[i] / total;
            }
        }

        var topPixels = (double)((height + 2) / 3) * width;
        var extras = new Dictionary<string, double>(8, StringComparer.Ordinal)
        {
            ["color.skyBlueShare"] = acc.Sky * inv,
            ["color.skyBlueTopShare"] = acc.SkyTop / topPixels,
            ["color.greenShare"] = acc.Green * inv,
            ["color.skinShare"] = acc.Skin * inv,
            ["color.neutralShare"] = acc.NeutralCount * inv,
            ["color.lumaStdDev"] = lumaStd,
        };
        if (hueConcentration is { } hc)
        {
            extras["color.hueConcentration"] = hc;
        }

        return new ColorFeatures
        {
            MeanLuma = meanL,
            LumaP05 = p05,
            LumaP50 = p50,
            LumaP95 = p95,
            ShadowClip = acc.ShadowClip * inv,
            HighlightClip = acc.HighlightClip * inv,
            Contrast = Math.Max(0.0, p95 - p05),
            Key = key,
            Colorfulness = colorfulness,
            MeanChroma = meanChroma,
            Saturation = saturation,
            Monochrome = monochrome,
            Warmth = Math.Clamp(5.0 * meanB, -1.0, 1.0),
            Tint = Math.Clamp(5.0 * meanA, -1.0, 1.0),
            Sharpness = laplacianVariance is { } v ? 1.0 - Math.Exp(-v / SharpnessScale) : null,
            HueHistogram = hueHistogram,
            LumaHistogram = lumaHistogram,
            Palette = palette,
            Extras = extras,
        };
    }

    /// <summary>Quantile of L, linearly interpolated between the smallest and largest value inside the target bin.</summary>
    private static double Percentile(ReadOnlySpan<int> hist, ReadOnlySpan<float> binMin, ReadOnlySpan<float> binMax, int n, double q)
    {
        var target = Math.Clamp(q * n, 0.5, n);
        long cumulative = 0;
        for (var i = 0; i < 256; i++)
        {
            var count = hist[i];
            if (count == 0)
            {
                continue;
            }

            if (cumulative + count >= target)
            {
                var frac = count == 1 ? 0.5 : (target - cumulative - 0.5) / (count - 1);
                frac = Math.Clamp(frac, 0.0, 1.0);
                return binMin[i] + frac * (binMax[i] - binMin[i]);
            }

            cumulative += count;
        }

        return binMax[255] > float.MinValue ? binMax[255] : 0.0;
    }
}
