using System.Buffers;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Composition;

/// <summary>
/// Model-free composition measurements on a ≤ <c>WorkingEdge</c> OKLab raster.
/// All coordinates are normalized to 0..1 with the origin at the top-left corner.
/// </summary>
internal static class CompositionEngine
{
    /// <summary>Thirds/centre distances are measured in units of the frame's short side, so the score falloff is aspect-independent.</summary>
    public const double ThirdsSigma = 0.10;

    /// <summary>CenterScore = exp(−(dc / 0.12)²): ~0.5 at 0.1 short-sides from the centre, ~0 beyond 0.25.</summary>
    public const double CenterSigma = 0.12;

    /// <summary>SeparationScore = 1 − exp(−max(0, r − 1) / 1.5) for the in/out |Laplacian| ratio r: r = 2 → 0.49, r = 3 → 0.74, r = 5 → 0.93.</summary>
    public const double SeparationSoftness = 1.5;

    /// <summary>Added to both |Laplacian| means so flat regions do not produce huge ratios from noise.</summary>
    public const float LaplacianEpsilon = 0.003f;

    /// <summary>Negative space grid (cells per side).</summary>
    public const int Grid = 16;

    /// <summary>Mean Sobel magnitude (OKLab L per px) below which a cell or sky pixel counts as untextured.</summary>
    public const float QuietGradient = 0.012f;

    /// <summary>Weight of the frequency-tuned colour term vs the local edge-density term in the saliency map.</summary>
    public const float FrequencyTunedWeight = 0.75f;

    private const int Planes = 13;
    private const int MaxComponents = 64;
    private const int MaxSubjects = 8;
    private const double MinSubjectArea = 0.003;
    private const double MaxSubjectArea = 0.6;
    private const double MinSubjectSaliency = 0.15;
    private const double MinSubjectContrast = 0.3;
    private const double MinConcentration = 0.5;
    private const double SecondarySubjectMass = 0.35;
    private const double BorderShareLimit = 0.2;
    private const double UniformSpread = 0.40824829;

    private struct Component
    {
        public int Label;
        public int Count;
        public double Mass;
        public double MassX;
        public double MassY;
    }

    public static PixelFeatures Analyze(Frame frame, int workingEdge)
    {
        (int w, int h) = ImageOps.WorkingSize(frame.Width, frame.Height, workingEdge);
        if (w < 16 || h < 16)
        {
            return PixelFeatures.Empty with { Width = w, Height = h };
        }

        int n = w * h;
        float[] floats = ArrayPool<float>.Shared.Rent(n * Planes);
        int[] ints = ArrayPool<int>.Shared.Rent(n * 2);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(n * 4);
        try
        {
            return AnalyzeCore(frame, w, h, floats, ints, bytes);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floats);
            ArrayPool<int>.Shared.Return(ints);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static PixelFeatures AnalyzeCore(Frame frame, int w, int h, float[] floats, int[] ints, byte[] bytes)
    {
        int n = w * h;
        Span<float> l = floats.AsSpan(0, n);
        Span<float> a = floats.AsSpan(n, n);
        Span<float> b = floats.AsSpan(2 * n, n);
        Span<float> bl = floats.AsSpan(3 * n, n);
        Span<float> ba = floats.AsSpan(4 * n, n);
        Span<float> bb = floats.AsSpan(5 * n, n);
        Span<float> gx = floats.AsSpan(6 * n, n);
        Span<float> gy = floats.AsSpan(7 * n, n);
        Span<float> g = floats.AsSpan(8 * n, n);
        Span<float> lap = floats.AsSpan(9 * n, n);
        Span<float> ed = floats.AsSpan(10 * n, n);
        Span<float> s = floats.AsSpan(11 * n, n);
        Span<float> tmp = floats.AsSpan(12 * n, n);

        ImageOps.ToOklab(frame, w, h, l, a, b);

        double sumL = 0, sumA = 0, sumB = 0, sumC = 0;
        int white = 0, dark = 0;
        for (int i = 0; i < n; i++)
        {
            float li = l[i], ai = a[i], bi = b[i];
            float c = MathF.Sqrt((ai * ai) + (bi * bi));
            sumL += li;
            sumA += ai;
            sumB += bi;
            sumC += c;
            if (li >= 0.9f && c < 0.03f)
            {
                white++;
            }

            if (li < 0.2f)
            {
                dark++;
            }
        }

        float meanL = (float)(sumL / n), meanA = (float)(sumA / n), meanB = (float)(sumB / n);

        // Detail scales are defined for a 256 px long edge and scale with the working raster.
        float scale = Math.Max(w, h) / 256f;
        Span<float> kernel = stackalloc float[(2 * ImageOps.MaxKernelRadius) + 1];
        int kr = ImageOps.GaussianKernel(MathF.Max(1f, 1.5f * scale), kernel);
        ImageOps.GaussianBlur(l, bl, tmp, w, h, kernel, kr);
        ImageOps.GaussianBlur(a, ba, tmp, w, h, kernel, kr);
        ImageOps.GaussianBlur(b, bb, tmp, w, h, kernel, kr);

        GradientStats gs = ImageOps.Gradients(l, gx, gy, g, lap, w, h);
        float edSigma = 3.5f * scale;
        int edRadius = Math.Max(1, (int)MathF.Round((MathF.Sqrt((4f * edSigma * edSigma) + 1f) - 1f) * 0.5f));
        ImageOps.BoxBlur3(g, ed, tmp, w, h, edRadius);

        // Saliency: frequency-tuned (Achanta 2009) distance of the blurred OKLab colour from the frame mean,
        // plus above-average local edge density. Normalization floors stop near-flat frames from being amplified.
        double sumEd = 0;
        for (int i = 0; i < n; i++)
        {
            sumEd += ed[i];
        }

        float meanEd = (float)(sumEd / n);
        float maxFt = 0f, maxEdT = 0f;
        for (int i = 0; i < n; i++)
        {
            float dl = bl[i] - meanL, da = ba[i] - meanA, db = bb[i] - meanB;
            float ft = MathF.Sqrt((dl * dl) + (da * da) + (db * db));
            float et = MathF.Max(0f, ed[i] - meanEd);
            s[i] = ft;
            tmp[i] = et;
            maxFt = MathF.Max(maxFt, ft);
            maxEdT = MathF.Max(maxEdT, et);
        }

        float kFt = FrequencyTunedWeight / MathF.Max(maxFt, 0.05f);
        float kEd = (1f - FrequencyTunedWeight) / MathF.Max(maxEdT, 0.01f);
        Span<int> hist = stackalloc int[64];
        hist.Clear();
        double sumS = 0, sumSx = 0, sumSy = 0, sumSxx = 0, sumSyy = 0;
        float invW = 1f / w, invH = 1f / h;
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) * invH;
            double rowS = 0, rowSx = 0, rowSxx = 0;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float si = (s[i] * kFt) + (tmp[i] * kEd);
                s[i] = si;
                float u = (x + 0.5f) * invW;
                rowS += si;
                rowSx += si * u;
                rowSxx += si * u * u;
                hist[Math.Min(63, (int)(si * 64f))]++;
            }

            sumS += rowS;
            sumSx += rowSx;
            sumSxx += rowSxx;
            sumSy += rowS * v;
            sumSyy += rowS * v * v;
        }

        double meanS = sumS / n;
        double spread = 0;
        if (sumS > 1e-9)
        {
            double mx = sumSx / sumS, my = sumSy / sumS;
            double varX = Math.Max(0, (sumSxx / sumS) - (mx * mx));
            double varY = Math.Max(0, (sumSyy / sumS) - (my * my));
            spread = Math.Sqrt(varX + varY) / UniformSpread;
        }

        // Adaptive threshold: 2× mean saliency (Achanta), capped by Otsu so large subjects are not lost.
        float threshold = (float)Math.Max(0.1, Math.Min(2.0 * meanS, Otsu(hist, n)));

        Span<byte> above = bytes.AsSpan(0, n);
        Span<byte> subject = bytes.AsSpan(n, n);
        Span<byte> eroded = bytes.AsSpan(2 * n, n);
        Span<byte> dilated = bytes.AsSpan(3 * n, n);
        for (int i = 0; i < n; i++)
        {
            above[i] = s[i] >= threshold ? (byte)1 : (byte)0;
        }

        Span<Component> comps = stackalloc Component[MaxComponents];
        int compCount = Label(above, s, ints.AsSpan(0, n), ints.AsSpan(n, n), w, h, comps, out double totalMass);
        Span<int> labels = ints.AsSpan(0, n);

        double? subjectX = null, subjectY = null, subjectArea = null, thirds = null, center = null, separation = null, subjectContrast = null;
        int subjectCount = 0;
        if (compCount > 0)
        {
            SortByMass(comps[..compCount]);
            double topMass = comps[0].Mass;
            int kept = 1;
            while (kept < Math.Min(compCount, MaxSubjects) && comps[kept].Mass >= SecondarySubjectMass * topMass)
            {
                kept++;
            }

            double keptMass = 0, keptX = 0, keptY = 0;
            Span<int> keptLabels = stackalloc int[MaxSubjects];
            for (int k = 0; k < kept; k++)
            {
                keptMass += comps[k].Mass;
                keptX += comps[k].MassX;
                keptY += comps[k].MassY;
                keptLabels[k] = comps[k].Label;
            }

            keptLabels = keptLabels[..kept];
            int area = 0;
            double massIn = 0;
            for (int i = 0; i < n; i++)
            {
                byte inside = 0;
                int lb = labels[i];
                if (lb != 0)
                {
                    for (int k = 0; k < keptLabels.Length; k++)
                    {
                        if (keptLabels[k] == lb)
                        {
                            inside = 1;
                            area++;
                            massIn += s[i];
                            break;
                        }
                    }
                }

                subject[i] = inside;
            }

            double areaShare = (double)area / n;
            double meanIn = massIn / Math.Max(1, area);
            double meanOut = (sumS - massIn) / Math.Max(1, n - area);
            double contrast = meanIn > 0 ? (meanIn - meanOut) / meanIn : 0;
            double concentration = totalMass > 0 ? keptMass / totalMass : 0;
            if (areaShare >= MinSubjectArea && areaShare <= MaxSubjectArea && meanIn >= MinSubjectSaliency
                && contrast >= MinSubjectContrast && concentration >= MinConcentration)
            {
                subjectCount = kept;
                subjectContrast = contrast;
                double sx = keptX / keptMass, sy = keptY / keptMass;
                subjectX = sx;
                subjectY = sy;
                subjectArea = areaShare;

                double shortSide = Math.Min(w, h);
                double fx = w / shortSide, fy = h / shortSide;
                double dThirds = double.MaxValue;
                for (int py = 1; py <= 2; py++)
                {
                    for (int px = 1; px <= 2; px++)
                    {
                        double dx = (sx - (px / 3.0)) * fx, dy = (sy - (py / 3.0)) * fy;
                        dThirds = Math.Min(dThirds, Math.Sqrt((dx * dx) + (dy * dy)));
                    }
                }

                double cdx = (sx - 0.5) * fx, cdy = (sy - 0.5) * fy;
                double dCenter = Math.Sqrt((cdx * cdx) + (cdy * cdy));
                thirds = Math.Exp(-Square(dThirds / ThirdsSigma));
                center = Math.Exp(-Square(dCenter / CenterSigma));
                separation = Separation(subject, eroded, dilated, above, lap, w, h, scale);
            }
        }

        double symmetry = 0.5 * (Symmetry(bl, w, h, 0.02f) + Symmetry(ed, w, h, 0.005f));
        double negativeSpace = NegativeSpace(g, s, w, h, threshold);
        double brightSky = BrightSky(l, g, w, h);
        HorizonResult? horizon = HorizonDetector.Detect(l, a, b, gx, gy, g, w, h, scale);

        double edgeTotal = gs.HorizontalEnergy + gs.VerticalEnergy + gs.DiagonalEnergy;
        bool hasEdges = edgeTotal > 1e-6;

        return new PixelFeatures
        {
            Width = w,
            Height = h,
            SubjectX = subjectX,
            SubjectY = subjectY,
            SubjectArea = subjectArea,
            ThirdsScore = thirds,
            CenterScore = center,
            SymmetryScore = Math.Clamp(symmetry, 0, 1),
            NegativeSpace = negativeSpace,
            SeparationScore = separation,
            HorizonTiltDeg = horizon?.TiltDeg,
            HorizonY = horizon?.Y,
            HorizonSpan = horizon?.Span,
            HorizonContrast = horizon?.Contrast,
            HorizontalEdges = hasEdges ? gs.HorizontalEnergy / edgeTotal : null,
            VerticalEdges = hasEdges ? gs.VerticalEnergy / edgeTotal : null,
            DiagonalEdges = hasEdges ? gs.DiagonalEnergy / edgeTotal : null,
            SubjectCount = subjectCount,
            SubjectContrast = subjectContrast,
            SaliencySpread = Math.Clamp(spread, 0, 1.5),
            SaliencyPeak = maxFt,
            BrightSky = brightSky,
            EdgeDensity = gs.MeanMagnitude,
            FlatShare = gs.FlatShare,
            WhiteShare = (double)white / n,
            DarkShare = (double)dark / n,
            MeanChroma = sumC / n,
            MeanLightness = sumL / n,
        };
    }

    private static double Square(double v) => v * v;

    private static double Otsu(ReadOnlySpan<int> hist, int total)
    {
        double sumAll = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            sumAll += (i + 0.5) * hist[i];
        }

        double sumBack = 0, bestVar = -1;
        int weightBack = 0, best = hist.Length - 1;
        for (int t = 0; t < hist.Length - 1; t++)
        {
            weightBack += hist[t];
            if (weightBack == 0)
            {
                continue;
            }

            int weightFore = total - weightBack;
            if (weightFore == 0)
            {
                break;
            }

            sumBack += (t + 0.5) * hist[t];
            double mb = sumBack / weightBack;
            double mf = (sumAll - sumBack) / weightFore;
            double between = (double)weightBack * weightFore * (mb - mf) * (mb - mf);
            if (between > bestVar)
            {
                bestVar = between;
                best = t;
            }
        }

        return (best + 1) / (double)hist.Length;
    }

    /// <summary>
    /// 4-connected labelling of the above-threshold mask. Components touching the frame border along more than
    /// 20 % of its perimeter (sky bands, backgrounds) are discarded. Returns the large components and the total
    /// saliency mass of all non-discarded components.
    /// </summary>
    private static int Label(
        ReadOnlySpan<byte> mask,
        ReadOnlySpan<float> s,
        Span<int> labels,
        Span<int> stack,
        int w,
        int h,
        Span<Component> comps,
        out double totalMass)
    {
        int n = w * h;
        labels.Clear();
        int minArea = Math.Max(4, (int)(MinSubjectArea * n));
        int borderLimit = (int)(BorderShareLimit * 2 * (w + h));
        int count = 0;
        int next = 0;
        totalMass = 0;
        for (int seed = 0; seed < n; seed++)
        {
            if (mask[seed] == 0 || labels[seed] != 0)
            {
                continue;
            }

            int label = ++next;
            int top = 0;
            stack[top++] = seed;
            labels[seed] = label;
            int pixels = 0, border = 0;
            double mass = 0, massX = 0, massY = 0;
            while (top > 0)
            {
                int i = stack[--top];
                int y = i / w;
                int x = i - (y * w);
                float si = s[i];
                pixels++;
                mass += si;
                massX += si * ((x + 0.5) / w);
                massY += si * ((y + 0.5) / h);
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                {
                    border++;
                }

                if (x > 0 && mask[i - 1] != 0 && labels[i - 1] == 0)
                {
                    labels[i - 1] = label;
                    stack[top++] = i - 1;
                }

                if (x < w - 1 && mask[i + 1] != 0 && labels[i + 1] == 0)
                {
                    labels[i + 1] = label;
                    stack[top++] = i + 1;
                }

                if (y > 0 && mask[i - w] != 0 && labels[i - w] == 0)
                {
                    labels[i - w] = label;
                    stack[top++] = i - w;
                }

                if (y < h - 1 && mask[i + w] != 0 && labels[i + w] == 0)
                {
                    labels[i + w] = label;
                    stack[top++] = i + w;
                }
            }

            if (border > borderLimit)
            {
                continue;
            }

            totalMass += mass;
            if (pixels < minArea)
            {
                continue;
            }

            var c = new Component { Label = label, Count = pixels, Mass = mass, MassX = massX, MassY = massY };
            if (count < comps.Length)
            {
                comps[count++] = c;
            }
            else
            {
                int smallest = 0;
                for (int k = 1; k < count; k++)
                {
                    if (comps[k].Mass < comps[smallest].Mass)
                    {
                        smallest = k;
                    }
                }

                if (comps[smallest].Mass < mass)
                {
                    comps[smallest] = c;
                }
            }
        }

        return count;
    }

    private static void SortByMass(Span<Component> comps)
    {
        for (int i = 1; i < comps.Length; i++)
        {
            Component c = comps[i];
            int j = i - 1;
            while (j >= 0 && (comps[j].Mass < c.Mass || (comps[j].Mass == c.Mass && comps[j].Label > c.Label)))
            {
                comps[j + 1] = comps[j];
                j--;
            }

            comps[j + 1] = c;
        }
    }

    private static double? Separation(
        ReadOnlySpan<byte> subject,
        Span<byte> eroded,
        Span<byte> dilated,
        Span<byte> scratch,
        ReadOnlySpan<float> lap,
        int w,
        int h,
        float scale)
    {
        int radius = Math.Max(1, (int)MathF.Round(2f * scale));
        ImageOps.Morph(subject, eroded, scratch, w, h, radius, dilate: false);
        ImageOps.Morph(subject, dilated, scratch, w, h, radius, dilate: true);
        int n = w * h;
        double sumIn = 0, sumOut = 0, sumMask = 0;
        int nIn = 0, nOut = 0, nMask = 0;
        for (int i = 0; i < n; i++)
        {
            float v = lap[i];
            if (eroded[i] != 0)
            {
                sumIn += v;
                nIn++;
            }

            if (subject[i] != 0)
            {
                sumMask += v;
                nMask++;
            }

            if (dilated[i] == 0)
            {
                sumOut += v;
                nOut++;
            }
        }

        if (nIn < 20)
        {
            sumIn = sumMask;
            nIn = nMask;
        }

        if (nIn == 0 || nOut < 20)
        {
            return null;
        }

        double ratio = ((sumIn / nIn) + LaplacianEpsilon) / ((sumOut / nOut) + LaplacianEpsilon);
        return 1.0 - Math.Exp(-Math.Max(0.0, ratio - 1.0) / SeparationSoftness);
    }

    /// <summary>1 − mean |P(x) − P(w−1−x)| / (√2 · mean absolute deviation from the row mean + floor), clamped to 0..1.</summary>
    private static double Symmetry(ReadOnlySpan<float> plane, int w, int h, float floor)
    {
        int half = w / 2;
        double diff = 0, mad = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<float> row = plane.Slice(y * w, w);
            double mean = 0;
            for (int x = 0; x < w; x++)
            {
                mean += row[x];
            }

            mean /= w;
            for (int x = 0; x < w; x++)
            {
                mad += Math.Abs(row[x] - mean);
            }

            for (int x = 0; x < half; x++)
            {
                diff += MathF.Abs(row[x] - row[w - 1 - x]);
            }
        }

        diff /= (double)half * h;
        mad /= (double)w * h;
        return Math.Clamp(1.0 - (diff / ((Math.Sqrt(2.0) * mad) + floor)), 0.0, 1.0);
    }

    private static double NegativeSpace(ReadOnlySpan<float> g, ReadOnlySpan<float> s, int w, int h, float threshold)
    {
        Span<double> cellG = stackalloc double[Grid * Grid];
        Span<double> cellS = stackalloc double[Grid * Grid];
        Span<int> cellN = stackalloc int[Grid * Grid];
        cellG.Clear();
        cellS.Clear();
        cellN.Clear();
        for (int y = 0; y < h; y++)
        {
            int cy = y * Grid / h;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int c = (cy * Grid) + (x * Grid / w);
                cellG[c] += g[row + x];
                cellS[c] += s[row + x];
                cellN[c]++;
            }
        }

        int empty = 0;
        for (int c = 0; c < cellN.Length; c++)
        {
            int count = Math.Max(1, cellN[c]);
            if (cellG[c] / count < QuietGradient && cellS[c] / count < threshold)
            {
                empty++;
            }
        }

        return (double)empty / cellN.Length;
    }

    /// <summary>Share of the top third that is bright (OKLab L ≥ 0.65) and untextured.</summary>
    private static double BrightSky(ReadOnlySpan<float> l, ReadOnlySpan<float> g, int w, int h)
    {
        int rows = Math.Max(1, h / 3);
        int bright = 0;
        for (int i = 0; i < rows * w; i++)
        {
            if (l[i] >= 0.65f && g[i] < QuietGradient)
            {
                bright++;
            }
        }

        return (double)bright / (rows * w);
    }
}
