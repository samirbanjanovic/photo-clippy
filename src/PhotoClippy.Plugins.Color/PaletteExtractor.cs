using System.Buffers;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Color;

/// <summary>
/// Deterministic dominant-colour extraction: k-means in OKLab on a stratified grid subsample, seeded with greedy
/// k-means++ driven by a fixed-seed SplitMix64 generator, then near-duplicate merging and small-cluster pruning.
/// </summary>
internal static class PaletteExtractor
{
    /// <summary>Clusters whose centroids are closer than this OKLab Euclidean distance (ΔE_OK) are merged.</summary>
    public const double MergeDistance = 0.04;

    /// <summary>Clusters lighter than this share of the samples are dropped.</summary>
    public const double MinWeight = 0.02;

    public const int MaxIterations = 12;

    private const ulong Seed = 0xC010_9A1E_77E5_EED5UL;

    public static IReadOnlyList<PaletteColor> Extract(ReadOnlySpan<byte> rgb, int width, int height, int k, int maxSamples)
    {
        var (nx, ny) = SampleGrid(width, height, maxSamples);
        var count = nx * ny;
        var pts = ArrayPool<float>.Shared.Rent(count * 3);
        var dist = ArrayPool<float>.Shared.Rent(count);
        var assign = ArrayPool<int>.Shared.Rent(count);
        try
        {
            var p = 0;
            for (var j = 0; j < ny; j++)
            {
                var y = (int)((j + 0.5) * height / ny);
                var row = y * width;
                for (var i = 0; i < nx; i++)
                {
                    var o = (row + (int)((i + 0.5) * width / nx)) * 3;
                    OkLab.FromSrgb(rgb[o], rgb[o + 1], rgb[o + 2], out var l, out var a, out var b);
                    pts[p++] = l;
                    pts[p++] = a;
                    pts[p++] = b;
                }
            }

            var points = pts.AsSpan(0, count * 3);
            Span<double> centers = stackalloc double[k * 3];
            var kk = SeedCenters(points, count, k, centers, dist.AsSpan(0, count));
            Span<double> weights = stackalloc double[kk];
            Lloyd(points, count, kk, centers[..(kk * 3)], assign.AsSpan(0, count), weights);
            return Finish(centers[..(kk * 3)], weights, count);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pts);
            ArrayPool<float>.Shared.Return(dist);
            ArrayPool<int>.Shared.Return(assign);
        }
    }

    private static (int Nx, int Ny) SampleGrid(int width, int height, int maxSamples)
    {
        if ((long)width * height <= maxSamples)
        {
            return (width, height);
        }

        var stride = Math.Sqrt((double)width * height / maxSamples);
        var nx = Math.Clamp((int)(width / stride), 1, width);
        var ny = Math.Clamp((int)(height / stride), 1, height);
        while (nx * ny > maxSamples)
        {
            if (nx >= ny)
            {
                nx--;
            }
            else
            {
                ny--;
            }
        }

        return (nx, ny);
    }

    private static int SeedCenters(ReadOnlySpan<float> pts, int count, int k, Span<double> centers, Span<float> dist)
    {
        var rng = new SplitMix64(Seed);
        var first = Math.Min(count - 1, (int)(rng.NextDouble() * count));
        SetCenter(centers, 0, pts, first);
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            var d = Distance2(pts, i, pts, first);
            dist[i] = d;
            total += d;
        }

        var trials = 2 + (int)Math.Log(k);
        var chosen = 1;
        while (chosen < k && total > 1e-12)
        {
            var best = -1;
            var bestPotential = double.MaxValue;
            for (var t = 0; t < trials; t++)
            {
                var candidate = Pick(dist, rng.NextDouble() * total);
                var potential = 0.0;
                for (var i = 0; i < count; i++)
                {
                    potential += Math.Min(dist[i], Distance2(pts, i, pts, candidate));
                }

                if (potential < bestPotential)
                {
                    bestPotential = potential;
                    best = candidate;
                }
            }

            SetCenter(centers, chosen++, pts, best);
            total = 0.0;
            for (var i = 0; i < count; i++)
            {
                var d = Math.Min(dist[i], Distance2(pts, i, pts, best));
                dist[i] = d;
                total += d;
            }
        }

        return chosen;
    }

    private static int Pick(ReadOnlySpan<float> dist, double target)
    {
        var acc = 0.0;
        var last = 0;
        for (var i = 0; i < dist.Length; i++)
        {
            if (dist[i] <= 0f)
            {
                continue;
            }

            last = i;
            acc += dist[i];
            if (acc > target)
            {
                return i;
            }
        }

        return last;
    }

    private static void Lloyd(ReadOnlySpan<float> pts, int count, int k, Span<double> centers, Span<int> assign, Span<double> counts)
    {
        Span<double> sums = stackalloc double[k * 3];
        assign.Fill(-1);
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            sums.Clear();
            counts.Clear();
            var changed = 0;
            for (var i = 0; i < count; i++)
            {
                double l = pts[i * 3], a = pts[i * 3 + 1], b = pts[i * 3 + 2];
                var best = 0;
                var bestD = double.MaxValue;
                for (var c = 0; c < k; c++)
                {
                    var dl = l - centers[c * 3];
                    var da = a - centers[c * 3 + 1];
                    var db = b - centers[c * 3 + 2];
                    var d = dl * dl + da * da + db * db;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = c;
                    }
                }

                if (assign[i] != best)
                {
                    assign[i] = best;
                    changed++;
                }

                sums[best * 3] += l;
                sums[best * 3 + 1] += a;
                sums[best * 3 + 2] += b;
                counts[best]++;
            }

            for (var c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    centers[c * 3] = sums[c * 3] / counts[c];
                    centers[c * 3 + 1] = sums[c * 3 + 1] / counts[c];
                    centers[c * 3 + 2] = sums[c * 3 + 2] / counts[c];
                }
            }

            if (changed == 0)
            {
                break;
            }
        }
    }

    private static PaletteColor[] Finish(Span<double> centers, Span<double> weights, int total)
    {
        var m = weights.Length;
        while (m > 1)
        {
            int bi = -1, bj = -1;
            var bestD = MergeDistance * MergeDistance;
            for (var i = 0; i < m; i++)
            {
                if (weights[i] <= 0)
                {
                    continue;
                }

                for (var j = i + 1; j < m; j++)
                {
                    if (weights[j] <= 0)
                    {
                        continue;
                    }

                    var dl = centers[i * 3] - centers[j * 3];
                    var da = centers[i * 3 + 1] - centers[j * 3 + 1];
                    var db = centers[i * 3 + 2] - centers[j * 3 + 2];
                    var d = dl * dl + da * da + db * db;
                    if (d < bestD)
                    {
                        bestD = d;
                        bi = i;
                        bj = j;
                    }
                }
            }

            if (bi < 0)
            {
                break;
            }

            var w = weights[bi] + weights[bj];
            for (var c = 0; c < 3; c++)
            {
                centers[bi * 3 + c] = (centers[bi * 3 + c] * weights[bi] + centers[bj * 3 + c] * weights[bj]) / w;
            }

            weights[bi] = w;
            m--;
            weights[bj] = weights[m];
            centers.Slice(m * 3, 3).CopyTo(centers.Slice(bj * 3, 3));
        }

        Span<int> order = stackalloc int[m];
        var kept = 0;
        var keptWeight = 0.0;
        for (var i = 0; i < m; i++)
        {
            if (weights[i] >= MinWeight * total)
            {
                order[kept++] = i;
                keptWeight += weights[i];
            }
        }

        order = order[..kept];
        for (var i = 1; i < order.Length; i++)
        {
            var cur = order[i];
            var j = i - 1;
            while (j >= 0 && Heavier(cur, order[j], centers, weights))
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = cur;
        }

        var result = new PaletteColor[kept];
        for (var i = 0; i < kept; i++)
        {
            var c = order[i];
            result[i] = new PaletteColor(OkLab.ToHex(centers[c * 3], centers[c * 3 + 1], centers[c * 3 + 2]), weights[c] / keptWeight);
        }

        return result;
    }

    private static bool Heavier(int x, int y, ReadOnlySpan<double> centers, ReadOnlySpan<double> weights)
    {
        if (weights[x] != weights[y])
        {
            return weights[x] > weights[y];
        }

        for (var c = 0; c < 3; c++)
        {
            if (centers[x * 3 + c] != centers[y * 3 + c])
            {
                return centers[x * 3 + c] > centers[y * 3 + c];
            }
        }

        return false;
    }

    private static void SetCenter(Span<double> centers, int c, ReadOnlySpan<float> pts, int i)
    {
        centers[c * 3] = pts[i * 3];
        centers[c * 3 + 1] = pts[i * 3 + 1];
        centers[c * 3 + 2] = pts[i * 3 + 2];
    }

    private static float Distance2(ReadOnlySpan<float> a, int i, ReadOnlySpan<float> b, int j)
    {
        var dl = a[i * 3] - b[j * 3];
        var da = a[i * 3 + 1] - b[j * 3 + 1];
        var db = a[i * 3 + 2] - b[j * 3 + 2];
        return dl * dl + da * da + db * db;
    }

    private struct SplitMix64(ulong state)
    {
        private ulong _state = state;

        public double NextDouble()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
