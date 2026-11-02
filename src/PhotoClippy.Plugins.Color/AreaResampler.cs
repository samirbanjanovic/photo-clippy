using System.Buffers;

namespace PhotoClippy.Plugins.Color;

/// <summary>Separable box (area-average) downsampler for RGB24, in gamma-encoded sRGB like common resizers.</summary>
internal static class AreaResampler
{
    public static (int Width, int Height) TargetSize(int width, int height, int maxEdge)
    {
        var longEdge = Math.Max(width, height);
        if (longEdge <= maxEdge)
        {
            return (width, height);
        }

        var scale = (double)maxEdge / longEdge;
        return width >= height
            ? (maxEdge, Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)))
            : (Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)), maxEdge);
    }

    public static void Resize(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        var xTaps = TapCount(srcWidth, dstWidth);
        var yTaps = TapCount(srcHeight, dstHeight);
        var xStart = ArrayPool<int>.Shared.Rent(dstWidth);
        var xWeights = ArrayPool<float>.Shared.Rent(dstWidth * xTaps);
        var yStart = ArrayPool<int>.Shared.Rent(dstHeight);
        var yWeights = ArrayPool<float>.Shared.Rent(dstHeight * yTaps);
        var rowStride = dstWidth * 3;
        var tmp = ArrayPool<float>.Shared.Rent(srcHeight * rowStride);
        var acc = ArrayPool<float>.Shared.Rent(rowStride);
        try
        {
            BuildTaps(srcWidth, dstWidth, xTaps, xStart, xWeights);
            BuildTaps(srcHeight, dstHeight, yTaps, yStart, yWeights);

            for (var y = 0; y < srcHeight; y++)
            {
                var srcRow = src.Slice(y * srcWidth * 3, srcWidth * 3);
                var tmpRow = tmp.AsSpan(y * rowStride, rowStride);
                for (var x = 0; x < dstWidth; x++)
                {
                    var start = xStart[x];
                    var taps = Math.Min(xTaps, srcWidth - start);
                    var w = xWeights.AsSpan(x * xTaps, taps);
                    float r = 0f, g = 0f, b = 0f;
                    var p = start * 3;
                    for (var t = 0; t < w.Length; t++, p += 3)
                    {
                        var wt = w[t];
                        r += wt * srcRow[p];
                        g += wt * srcRow[p + 1];
                        b += wt * srcRow[p + 2];
                    }

                    tmpRow[x * 3] = r;
                    tmpRow[x * 3 + 1] = g;
                    tmpRow[x * 3 + 2] = b;
                }
            }

            var accRow = acc.AsSpan(0, rowStride);
            for (var y = 0; y < dstHeight; y++)
            {
                accRow.Clear();
                var start = yStart[y];
                var taps = Math.Min(yTaps, srcHeight - start);
                var w = yWeights.AsSpan(y * yTaps, taps);
                for (var t = 0; t < w.Length; t++)
                {
                    var wt = w[t];
                    if (wt == 0f)
                    {
                        continue;
                    }

                    var tmpRow = tmp.AsSpan((start + t) * rowStride, rowStride);
                    for (var i = 0; i < accRow.Length; i++)
                    {
                        accRow[i] += wt * tmpRow[i];
                    }
                }

                var dstRow = dst.Slice(y * rowStride, rowStride);
                for (var i = 0; i < dstRow.Length; i++)
                {
                    var v = (int)(accRow[i] + 0.5f);
                    dstRow[i] = (byte)(v > 255 ? 255 : v < 0 ? 0 : v);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xStart);
            ArrayPool<float>.Shared.Return(xWeights);
            ArrayPool<int>.Shared.Return(yStart);
            ArrayPool<float>.Shared.Return(yWeights);
            ArrayPool<float>.Shared.Return(tmp);
            ArrayPool<float>.Shared.Return(acc);
        }
    }

    private static int TapCount(int srcLength, int dstLength) => (int)Math.Ceiling((double)srcLength / dstLength) + 1;

    private static void BuildTaps(int srcLength, int dstLength, int taps, int[] starts, float[] weights)
    {
        var scale = (double)srcLength / dstLength;
        weights.AsSpan(0, dstLength * taps).Clear();
        for (var o = 0; o < dstLength; o++)
        {
            var x0 = o * scale;
            var x1 = Math.Min(srcLength, (o + 1) * scale);
            var first = Math.Min(srcLength - 1, (int)Math.Floor(x0));
            starts[o] = first;
            for (var t = 0; t < taps; t++)
            {
                var i = first + t;
                if (i >= srcLength)
                {
                    break;
                }

                var overlap = Math.Min(i + 1, x1) - Math.Max(i, x0);
                if (overlap > 0)
                {
                    weights[o * taps + t] = (float)(overlap / scale);
                }
            }
        }
    }
}
