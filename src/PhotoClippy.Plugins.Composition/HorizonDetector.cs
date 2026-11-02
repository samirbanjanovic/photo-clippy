using System.Buffers;

namespace PhotoClippy.Plugins.Composition;

internal readonly record struct HorizonResult(double TiltDeg, double Y, double Span, double Contrast);

/// <summary>
/// Horizon finder: magnitude-weighted Hough transform over near-horizontal lines.
/// Lines are parameterized as <c>y = yc + tan(θ)·(x − cx)</c> with <c>cx</c> the frame's centre column, so the
/// accumulator row is directly the line height at the centre and positive θ means "right side lower" (clockwise).
/// </summary>
internal static class HorizonDetector
{
    /// <summary>Only lines within ±20° of horizontal are considered.</summary>
    public const double MaxTiltDeg = 20.0;

    /// <summary>Accumulator angle resolution (refined to sub-bin precision afterwards).</summary>
    public const double StepDeg = 0.5;

    /// <summary>Minimum share of columns that must carry an edge aligned with the line.</summary>
    public const double MinSpan = 0.55;

    private const int AngleBins = 81;
    private const int VoteSpreadBins = 4;
    private const int MaxPeaks = 4;
    private const float VoteGradient = 0.01f;
    private const float SupportGradient = 0.015f;

    /// <summary>|local slope − line slope| tolerance for a supporting pixel (≈ 7°).</summary>
    private const float SupportSlopeTolerance = 0.13f;

    private const float MaxVoteSlope = 0.42f;

    private static readonly float[] Tan = BuildTan();

    private static float[] BuildTan()
    {
        var t = new float[AngleBins];
        for (int i = 0; i < AngleBins; i++)
        {
            t[i] = (float)Math.Tan(BinToDeg(i) * Math.PI / 180.0);
        }

        return t;
    }

    private static double BinToDeg(double bin) => -MaxTiltDeg + (bin * StepDeg);

    public static HorizonResult? Detect(
        ReadOnlySpan<float> l,
        ReadOnlySpan<float> a,
        ReadOnlySpan<float> b,
        ReadOnlySpan<float> gx,
        ReadOnlySpan<float> gy,
        ReadOnlySpan<float> mag,
        int w,
        int h,
        float scale)
    {
        if (w < 16 || h < 16)
        {
            return null;
        }

        int cells = AngleBins * h;
        int[] buffer = ArrayPool<int>.Shared.Rent(2 * cells);
        try
        {
            Span<int> acc = buffer.AsSpan(0, cells);
            Span<int> smooth = buffer.AsSpan(cells, cells);
            acc.Clear();
            Vote(gx, gy, mag, w, h, acc);

            for (int t = 0; t < AngleBins; t++)
            {
                Span<int> src = acc.Slice(t * h, h);
                Span<int> dst = smooth.Slice(t * h, h);
                for (int y = 0; y < h; y++)
                {
                    dst[y] = src[y] + (y > 0 ? src[y - 1] : 0) + (y < h - 1 ? src[y + 1] : 0);
                }
            }

            int rowSuppress = Math.Max(3, h / 30);
            const int angleSuppress = 6;
            for (int peak = 0; peak < MaxPeaks; peak++)
            {
                int best = 0;
                int bestIndex = -1;
                for (int i = 0; i < cells; i++)
                {
                    if (smooth[i] > best)
                    {
                        best = smooth[i];
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                {
                    return null;
                }

                int tb = bestIndex / h;
                int yb = bestIndex % h;
                double thetaBin = tb;
                if (tb > 0 && tb < AngleBins - 1)
                {
                    double s0 = smooth[((tb - 1) * h) + yb], s1 = smooth[(tb * h) + yb], s2 = smooth[((tb + 1) * h) + yb];
                    double den = s0 - (2 * s1) + s2;
                    if (den < 0)
                    {
                        thetaBin += Math.Clamp(0.5 * (s0 - s2) / den, -0.5, 0.5);
                    }
                }

                double num = 0, sum = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int yy = yb + dy;
                    if (yy >= 0 && yy < h)
                    {
                        int v = acc[(tb * h) + yy];
                        num += v * (double)yy;
                        sum += v;
                    }
                }

                double yc = sum > 0 ? num / sum : yb;
                double tiltDeg = BinToDeg(thetaBin);
                HorizonResult? result = Validate(l, a, b, gx, gy, mag, w, h, scale, tiltDeg, yc);
                if (result is not null)
                {
                    return result;
                }

                for (int t = Math.Max(0, tb - angleSuppress); t <= Math.Min(AngleBins - 1, tb + angleSuppress); t++)
                {
                    smooth.Slice((t * h) + Math.Max(0, yb - rowSuppress), Math.Min(h - 1, yb + rowSuppress) - Math.Max(0, yb - rowSuppress) + 1).Clear();
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    private static void Vote(ReadOnlySpan<float> gx, ReadOnlySpan<float> gy, ReadOnlySpan<float> mag, int w, int h, Span<int> acc)
    {
        float cx = (w - 1) * 0.5f;
        float[] tan = Tan;
        float binsPerRad = (float)(180.0 / Math.PI / StepDeg);
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                float m = mag[i];
                if (m < VoteGradient)
                {
                    continue;
                }

                float dy = gy[i];
                float slope = -gx[i] / dy;
                if (!(MathF.Abs(slope) <= MaxVoteSlope))
                {
                    continue;
                }

                // atan(s) ≈ s − s³/3 is accurate to < 0.15° for |s| ≤ 0.42, plenty for a ±2° vote window.
                float angle = slope - (slope * slope * slope / 3f);
                int centre = (int)MathF.Round((angle * binsPerRad) + (AngleBins - 1) / 2f);
                int weight = (int)(m * 1024f) + 1;
                int t0 = Math.Max(0, centre - VoteSpreadBins);
                int t1 = Math.Min(AngleBins - 1, centre + VoteSpreadBins);
                float dx = x - cx;
                for (int t = t0; t <= t1; t++)
                {
                    int yc = (int)MathF.Round(y - (tan[t] * dx));
                    if ((uint)yc < (uint)h)
                    {
                        acc[(t * h) + yc] += weight;
                    }
                }
            }
        }
    }

    private static HorizonResult? Validate(
        ReadOnlySpan<float> l,
        ReadOnlySpan<float> a,
        ReadOnlySpan<float> b,
        ReadOnlySpan<float> gx,
        ReadOnlySpan<float> gy,
        ReadOnlySpan<float> mag,
        int w,
        int h,
        float scale,
        double tiltDeg,
        double yc)
    {
        double yNorm = (yc + 0.5) / h;
        if (yNorm < 0.03 || yNorm > 0.97)
        {
            return null;
        }

        float slope = (float)Math.Tan(tiltDeg * Math.PI / 180.0);
        float cx = (w - 1) * 0.5f;
        int tol = Math.Max(2, (int)MathF.Round(2f * scale));
        int gap = Math.Max(3, (int)MathF.Round(3f * scale));
        int band = Math.Max(2, h / 10);
        int supported = 0;
        double sumLa = 0, sumAa = 0, sumBa = 0, sumGa = 0, sumLb = 0, sumAb = 0, sumBb = 0, sumGb = 0;
        int nAbove = 0, nBelow = 0;
        for (int x = 1; x < w - 1; x++)
        {
            float yLine = (float)yc + (slope * (x - cx));
            int yr = (int)MathF.Round(yLine);
            for (int y = Math.Max(1, yr - tol); y <= Math.Min(h - 2, yr + tol); y++)
            {
                int i = (y * w) + x;
                float dy = gy[i];
                if (mag[i] >= SupportGradient && MathF.Abs(-gx[i] - (slope * dy)) <= SupportSlopeTolerance * MathF.Abs(dy))
                {
                    supported++;
                    break;
                }
            }

            for (int y = Math.Clamp(yr - gap - band, 0, h); y < Math.Clamp(yr - gap, 0, h); y++)
            {
                int i = (y * w) + x;
                sumLa += l[i];
                sumAa += a[i];
                sumBa += b[i];
                sumGa += mag[i];
                nAbove++;
            }

            for (int y = Math.Clamp(yr + gap + 1, 0, h); y < Math.Clamp(yr + gap + 1 + band, 0, h); y++)
            {
                int i = (y * w) + x;
                sumLb += l[i];
                sumAb += a[i];
                sumBb += b[i];
                sumGb += mag[i];
                nBelow++;
            }
        }

        double span = (double)supported / (w - 2);
        if (span < MinSpan || nAbove < 2 * w || nBelow < 2 * w)
        {
            return null;
        }

        double dL = Math.Abs((sumLa / nAbove) - (sumLb / nBelow));
        double da = (sumAa / nAbove) - (sumAb / nBelow);
        double db = (sumBa / nAbove) - (sumBb / nBelow);
        double dC = Math.Sqrt((da * da) + (db * db));
        double gA = sumGa / nAbove, gB = sumGb / nBelow;
        double texture = (Math.Max(gA, gB) + 0.002) / (Math.Min(gA, gB) + 0.002);
        bool textureSplit = texture >= 2.0 && Math.Max(gA, gB) >= 0.01;
        if (dL < 0.05 && dC < 0.04 && !textureSplit)
        {
            return null;
        }

        double contrast = Math.Max(dL / 0.05, Math.Max(dC / 0.04, textureSplit ? texture / 2.0 : 0.0));
        return new HorizonResult(tiltDeg, yNorm, span, contrast);
    }
}
