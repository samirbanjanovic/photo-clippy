using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Composition;

internal readonly record struct GradientStats(
    double MeanMagnitude,
    double FlatShare,
    double HorizontalEnergy,
    double VerticalEnergy,
    double DiagonalEnergy);

/// <summary>Raster primitives on float planes (row-major, stride = width).</summary>
internal static class ImageOps
{
    public const int MaxKernelRadius = 16;

    /// <summary>Gradient magnitudes below this (OKLab L per px, Sobel/8) are treated as perfectly flat.</summary>
    public const float FlatGradient = 0.0005f;

    /// <summary>Noise floor for edge-orientation statistics.</summary>
    public const float OrientationFloor = 0.004f;

    private const float Tan22_5 = 0.41421356f;

    private static readonly float[] SrgbToLinearLut = BuildSrgbLut();

    private static float[] BuildSrgbLut()
    {
        var lut = new float[256];
        for (int i = 0; i < lut.Length; i++)
        {
            double c = i / 255.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        return lut;
    }

    public static (int Width, int Height) WorkingSize(int width, int height, int workingEdge)
    {
        int longEdge = Math.Max(width, height);
        if (longEdge <= workingEdge)
        {
            return (width, height);
        }

        double s = (double)workingEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }

    /// <summary>Area-averages the frame (in linear light) down to <paramref name="w"/>×<paramref name="h"/> and converts it to OKLab planes.</summary>
    public static void ToOklab(Frame frame, int w, int h, Span<float> l, Span<float> a, Span<float> b)
    {
        ReadOnlySpan<byte> rgb = frame.Rgb.Span;
        float[] lut = SrgbToLinearLut;
        int sw = frame.Width;
        int sh = frame.Height;
        if (sw == w && sh == h)
        {
            for (int i = 0, p = 0; i < w * h; i++, p += 3)
            {
                LinearToOklab(lut[rgb[p]], lut[rgb[p + 1]], lut[rgb[p + 2]], out l[i], out a[i], out b[i]);
            }

            return;
        }

        int kx = (int)Math.Ceiling((double)sw / w) + 1;
        int ky = (int)Math.Ceiling((double)sh / h) + 1;
        int[] ints = ArrayPool<int>.Shared.Rent((2 * w) + (2 * h));
        float[] floats = ArrayPool<float>.Shared.Rent((w * kx) + (h * ky) + (6 * w));
        try
        {
            Span<int> xStart = ints.AsSpan(0, w);
            Span<int> xCount = ints.AsSpan(w, w);
            Span<int> yStart = ints.AsSpan(2 * w, h);
            Span<int> yCount = ints.AsSpan((2 * w) + h, h);
            Span<float> xWeights = floats.AsSpan(0, w * kx);
            Span<float> yWeights = floats.AsSpan(w * kx, h * ky);
            Span<float> hRow = floats.AsSpan((w * kx) + (h * ky), 3 * w);
            Span<float> acc = floats.AsSpan((w * kx) + (h * ky) + (3 * w), 3 * w);
            BuildAxis(sw, w, kx, xStart, xCount, xWeights);
            BuildAxis(sh, h, ky, yStart, yCount, yWeights);

            int cachedRow = -1;
            for (int oy = 0; oy < h; oy++)
            {
                acc.Clear();
                int y0 = yStart[oy];
                int taps = yCount[oy];
                for (int k = 0; k < taps; k++)
                {
                    int sy = y0 + k;
                    if (sy != cachedRow)
                    {
                        HorizontalAreaPass(rgb.Slice(sy * sw * 3, sw * 3), hRow, w, kx, xStart, xCount, xWeights, lut);
                        cachedRow = sy;
                    }

                    MulAdd(hRow, yWeights[(oy * ky) + k], acc);
                }

                int o = oy * w;
                for (int ox = 0, p = 0; ox < w; ox++, p += 3)
                {
                    LinearToOklab(acc[p], acc[p + 1], acc[p + 2], out l[o + ox], out a[o + ox], out b[o + ox]);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ints);
            ArrayPool<float>.Shared.Return(floats);
        }
    }

    private static void HorizontalAreaPass(
        ReadOnlySpan<byte> src,
        Span<float> dst,
        int w,
        int kx,
        ReadOnlySpan<int> xStart,
        ReadOnlySpan<int> xCount,
        ReadOnlySpan<float> xWeights,
        float[] lut)
    {
        for (int ox = 0; ox < w; ox++)
        {
            int taps = xCount[ox];
            ReadOnlySpan<float> wt = xWeights.Slice(ox * kx, taps);
            float r = 0f, g = 0f, b = 0f;
            int p = xStart[ox] * 3;
            for (int k = 0; k < taps; k++, p += 3)
            {
                float q = wt[k];
                r += q * lut[src[p]];
                g += q * lut[src[p + 1]];
                b += q * lut[src[p + 2]];
            }

            dst[ox * 3] = r;
            dst[(ox * 3) + 1] = g;
            dst[(ox * 3) + 2] = b;
        }
    }

    private static void BuildAxis(int src, int dst, int maxTaps, Span<int> start, Span<int> count, Span<float> weights)
    {
        double s = (double)src / dst;
        for (int o = 0; o < dst; o++)
        {
            double lo = o * s;
            double hi = Math.Min(src, (o + 1) * s);
            int i0 = Math.Min(src - 1, (int)Math.Floor(lo));
            int i1 = Math.Max(i0 + 1, Math.Min(src, (int)Math.Ceiling(hi)));
            int taps = Math.Min(maxTaps, i1 - i0);
            start[o] = i0;
            count[o] = taps;
            for (int j = 0; j < taps; j++)
            {
                int i = i0 + j;
                double overlap = Math.Min(hi, i + 1) - Math.Max(lo, i);
                weights[(o * maxTaps) + j] = (float)(Math.Max(0.0, overlap) / s);
            }
        }
    }

    /// <summary>Cube root for x ≥ 0: exponent bit trick plus two Newton steps (relative error &lt; 1e-5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastCbrt(float x)
    {
        if (!(x > 1e-20f))
        {
            return 0f;
        }

        float y = BitConverter.Int32BitsToSingle((BitConverter.SingleToInt32Bits(x) / 3) + 709958130);
        y = ((2f * y) + (x / (y * y))) * (1f / 3f);
        y = ((2f * y) + (x / (y * y))) * (1f / 3f);
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LinearToOklab(float r, float g, float b, out float okL, out float okA, out float okB)
    {
        float l = FastCbrt((0.4122214708f * r) + (0.5363325363f * g) + (0.0514459929f * b));
        float m = FastCbrt((0.2119034982f * r) + (0.6806995451f * g) + (0.1073969566f * b));
        float s = FastCbrt((0.0883024619f * r) + (0.2817188376f * g) + (0.6299787005f * b));
        okL = (0.2104542553f * l) + (0.7936177850f * m) - (0.0040720468f * s);
        okA = (1.9779984951f * l) - (2.4285922050f * m) + (0.4505937099f * s);
        okB = (0.0259040371f * l) + (0.7827717662f * m) - (0.8086757660f * s);
    }

    /// <summary>dst += src * weight (vectorized).</summary>
    public static void MulAdd(ReadOnlySpan<float> src, float weight, Span<float> dst)
    {
        int n = src.Length;
        int i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vw = new Vector<float>(weight);
            ref float s = ref MemoryMarshal.GetReference(src);
            ref float d = ref MemoryMarshal.GetReference(dst[..n]);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                (Vector.LoadUnsafe(ref d, (nuint)i) + (Vector.LoadUnsafe(ref s, (nuint)i) * vw)).StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            dst[i] += src[i] * weight;
        }
    }

    /// <summary>Fills a normalized Gaussian kernel and returns its radius (≤ <see cref="MaxKernelRadius"/>).</summary>
    public static int GaussianKernel(float sigma, Span<float> kernel)
    {
        int r = Math.Clamp((int)MathF.Ceiling(3f * sigma), 1, MaxKernelRadius);
        float sum = 0f;
        for (int i = -r; i <= r; i++)
        {
            float v = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + r] = v;
            sum += v;
        }

        for (int i = 0; i <= 2 * r; i++)
        {
            kernel[i] /= sum;
        }

        return r;
    }

    /// <summary>Separable convolution with edge clamping.</summary>
    public static void GaussianBlur(ReadOnlySpan<float> src, Span<float> dst, Span<float> tmp, int w, int h, ReadOnlySpan<float> kernel, int r)
    {
        for (int y = 0; y < h; y++)
        {
            ConvolveRow(src.Slice(y * w, w), tmp.Slice(y * w, w), kernel, r);
        }

        for (int y = 0; y < h; y++)
        {
            Span<float> row = dst.Slice(y * w, w);
            row.Clear();
            for (int i = -r; i <= r; i++)
            {
                int yy = Math.Clamp(y + i, 0, h - 1);
                MulAdd(tmp.Slice(yy * w, w), kernel[i + r], row);
            }
        }
    }

    private static void ConvolveRow(ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<float> kernel, int r)
    {
        int w = src.Length;
        int interiorEnd = w - r;
        int x = 0;
        for (; x < Math.Min(r, w); x++)
        {
            dst[x] = ConvolveClamped(src, kernel, r, x);
        }

        if (Vector.IsHardwareAccelerated)
        {
            int vc = Vector<float>.Count;
            ref float s = ref MemoryMarshal.GetReference(src);
            ref float d = ref MemoryMarshal.GetReference(dst);
            for (; x + vc <= interiorEnd; x += vc)
            {
                var acc = Vector<float>.Zero;
                for (int i = -r; i <= r; i++)
                {
                    acc += Vector.LoadUnsafe(ref s, (nuint)(x + i)) * kernel[i + r];
                }

                acc.StoreUnsafe(ref d, (nuint)x);
            }
        }

        for (; x < interiorEnd; x++)
        {
            float acc = 0f;
            for (int i = -r; i <= r; i++)
            {
                acc += src[x + i] * kernel[i + r];
            }

            dst[x] = acc;
        }

        for (; x < w; x++)
        {
            dst[x] = ConvolveClamped(src, kernel, r, x);
        }
    }

    private static float ConvolveClamped(ReadOnlySpan<float> src, ReadOnlySpan<float> kernel, int r, int x)
    {
        int last = src.Length - 1;
        float acc = 0f;
        for (int i = -r; i <= r; i++)
        {
            acc += src[Math.Clamp(x + i, 0, last)] * kernel[i + r];
        }

        return acc;
    }

    /// <summary>Three box passes (≈ Gaussian with σ² = (2r+1)²−1)/4) with edge clamping.</summary>
    public static void BoxBlur3(ReadOnlySpan<float> src, Span<float> dst, Span<float> tmp, int w, int h, int r)
    {
        Span<float> colSum = w <= 2048 ? stackalloc float[w] : new float[w];
        BoxH(src, tmp, w, h, r);
        BoxV(tmp, dst, colSum, w, h, r);
        BoxH(dst, tmp, w, h, r);
        BoxV(tmp, dst, colSum, w, h, r);
        BoxH(dst, tmp, w, h, r);
        BoxV(tmp, dst, colSum, w, h, r);
    }

    private static void BoxH(ReadOnlySpan<float> src, Span<float> dst, int w, int h, int r)
    {
        float inv = 1f / ((2 * r) + 1);
        int last = w - 1;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<float> s = src.Slice(y * w, w);
            Span<float> d = dst.Slice(y * w, w);
            float sum = 0f;
            for (int i = -r; i <= r; i++)
            {
                sum += s[Math.Clamp(i, 0, last)];
            }

            for (int x = 0; x < w; x++)
            {
                d[x] = sum * inv;
                sum += s[Math.Min(x + r + 1, last)] - s[Math.Max(x - r, 0)];
            }
        }
    }

    private static void BoxV(ReadOnlySpan<float> src, Span<float> dst, Span<float> colSum, int w, int h, int r)
    {
        float inv = 1f / ((2 * r) + 1);
        int last = h - 1;
        colSum.Clear();
        for (int i = -r; i <= r; i++)
        {
            MulAdd(src.Slice(Math.Clamp(i, 0, last) * w, w), 1f, colSum);
        }

        for (int y = 0; y < h; y++)
        {
            Span<float> d = dst.Slice(y * w, w);
            ReadOnlySpan<float> add = src.Slice(Math.Min(y + r + 1, last) * w, w);
            ReadOnlySpan<float> sub = src.Slice(Math.Max(y - r, 0) * w, w);
            for (int x = 0; x < w; x++)
            {
                d[x] = colSum[x] * inv;
                colSum[x] += add[x] - sub[x];
            }
        }
    }

    /// <summary>
    /// Sobel gradients (normalized by 1/8 so a ramp of slope k gives magnitude k), magnitude and |4-neighbour Laplacian|.
    /// Border pixels are set to zero. Also gathers magnitude-weighted orientation energy.
    /// </summary>
    public static GradientStats Gradients(ReadOnlySpan<float> l, Span<float> gx, Span<float> gy, Span<float> mag, Span<float> lap, int w, int h)
    {
        gx.Clear();
        gy.Clear();
        mag.Clear();
        lap.Clear();
        double sumMag = 0, hor = 0, ver = 0, diag = 0;
        int flat = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                float tl = l[i - w - 1], tc = l[i - w], tr = l[i - w + 1];
                float ml = l[i - 1], mc = l[i], mr = l[i + 1];
                float bl = l[i + w - 1], bc = l[i + w], br = l[i + w + 1];
                float dx = ((tr + (2f * mr) + br) - (tl + (2f * ml) + bl)) * 0.125f;
                float dy = ((bl + (2f * bc) + br) - (tl + (2f * tc) + tr)) * 0.125f;
                float m = MathF.Sqrt((dx * dx) + (dy * dy));
                gx[i] = dx;
                gy[i] = dy;
                mag[i] = m;
                lap[i] = MathF.Abs(ml + mr + tc + bc - (4f * mc));
                sumMag += m;
                if (m < FlatGradient)
                {
                    flat++;
                }
                else if (m >= OrientationFloor)
                {
                    float ax = MathF.Abs(dx);
                    float ay = MathF.Abs(dy);
                    if (ax <= Tan22_5 * ay)
                    {
                        hor += m;
                    }
                    else if (ay <= Tan22_5 * ax)
                    {
                        ver += m;
                    }
                    else
                    {
                        diag += m;
                    }
                }
            }
        }

        int interior = Math.Max(1, (w - 2) * (h - 2));
        return new GradientStats(sumMag / interior, (double)flat / interior, hor, ver, diag);
    }

    /// <summary>
    /// Binary (0/1) square dilation/erosion with radius r using separable sliding-window counts. Pixels outside the
    /// frame count as background, so subjects touching the border still erode there.
    /// </summary>
    public static void Morph(ReadOnlySpan<byte> src, Span<byte> dst, Span<byte> tmp, int w, int h, int r, bool dilate)
    {
        int full = (2 * r) + 1;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> s = src.Slice(y * w, w);
            Span<byte> d = tmp.Slice(y * w, w);
            int count = 0;
            for (int x = 0; x < Math.Min(r, w); x++)
            {
                count += s[x];
            }

            for (int x = 0; x < w; x++)
            {
                if (x + r < w)
                {
                    count += s[x + r];
                }

                if (x - r - 1 >= 0)
                {
                    count -= s[x - r - 1];
                }

                d[x] = Hit(count, full, dilate);
            }
        }

        int[]? rented = null;
        Span<int> counts = w <= 1024 ? stackalloc int[w] : (rented = ArrayPool<int>.Shared.Rent(w)).AsSpan(0, w);
        try
        {
            counts.Clear();
            for (int y = 0; y < Math.Min(r, h); y++)
            {
                AddRow(tmp.Slice(y * w, w), counts, 1);
            }

            for (int y = 0; y < h; y++)
            {
                if (y + r < h)
                {
                    AddRow(tmp.Slice((y + r) * w, w), counts, 1);
                }

                if (y - r - 1 >= 0)
                {
                    AddRow(tmp.Slice((y - r - 1) * w, w), counts, -1);
                }

                Span<byte> d = dst.Slice(y * w, w);
                for (int x = 0; x < w; x++)
                {
                    d[x] = Hit(counts[x], full, dilate);
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Hit(int count, int full, bool dilate) =>
        (dilate ? count > 0 : count == full) ? (byte)1 : (byte)0;

    private static void AddRow(ReadOnlySpan<byte> row, Span<int> counts, int sign)
    {
        for (int x = 0; x < row.Length; x++)
        {
            counts[x] += sign * row[x];
        }
    }
}
