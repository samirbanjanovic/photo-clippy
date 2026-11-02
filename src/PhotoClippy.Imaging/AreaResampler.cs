using System.Buffers;

namespace PhotoClippy.Imaging;

/// <summary>
/// Streaming separable area-average (box) downscaler. Source rows are pushed top to bottom as packed
/// 8-bit RGB (3 or 4 bytes per pixel, the 4th byte ignored); finished output rows go to an <see cref="OrientedWriter"/>.
/// </summary>
internal sealed class AreaResampler : IDisposable
{
    private readonly int sourceWidth;
    private readonly int sourceHeight;
    private readonly int targetWidth;
    private readonly int targetHeight;
    private readonly OrientedWriter writer;
    private readonly bool identity;
    private readonly int[] columnOffset;
    private readonly float[] columnWeight0;
    private readonly float[] columnWeight1;
    private readonly float[] horizontal;
    private readonly float[] current;
    private readonly float[] next;
    private readonly byte[] outputRow;
    private readonly float normalize;
    private int sourceRow;
    private int targetRow;
    private bool disposed;

    public AreaResampler(int sourceWidth, int sourceHeight, OrientedWriter writer)
    {
        this.sourceWidth = sourceWidth;
        this.sourceHeight = sourceHeight;
        this.writer = writer;
        targetWidth = writer.StoredWidth;
        targetHeight = writer.StoredHeight;
        if (targetWidth > sourceWidth || targetHeight > sourceHeight)
        {
            throw new ImageDecodeException("internal error: resampler cannot upscale");
        }

        identity = targetWidth == sourceWidth && targetHeight == sourceHeight;
        var pool = ArrayPool<float>.Shared;
        outputRow = ArrayPool<byte>.Shared.Rent(targetWidth * 3);
        if (identity)
        {
            columnOffset = [];
            columnWeight0 = [];
            columnWeight1 = [];
            horizontal = [];
            current = [];
            next = [];
            return;
        }

        columnOffset = ArrayPool<int>.Shared.Rent(sourceWidth);
        columnWeight0 = pool.Rent(sourceWidth);
        columnWeight1 = pool.Rent(sourceWidth);
        var accumulator = (targetWidth + 1) * 3;
        horizontal = pool.Rent(accumulator);
        current = pool.Rent(accumulator);
        next = pool.Rent(accumulator);
        Array.Clear(horizontal, 0, accumulator);
        Array.Clear(current, 0, accumulator);
        Array.Clear(next, 0, accumulator);
        for (long i = 0; i < sourceWidth; i++)
        {
            var start = i * targetWidth;
            var end = start + targetWidth;
            var j0 = start / sourceWidth;
            var boundary = (j0 + 1) * sourceWidth;
            columnOffset[i] = (int)(j0 * 3);
            if (end <= boundary)
            {
                columnWeight0[i] = targetWidth;
                columnWeight1[i] = 0f;
            }
            else
            {
                columnWeight0[i] = boundary - start;
                columnWeight1[i] = end - boundary;
            }
        }

        normalize = (float)(1.0 / ((double)sourceWidth * sourceHeight));
    }

    public bool IsComplete => targetRow >= targetHeight;

    public void PushRows(ReadOnlySpan<byte> rows, int count, int rowBytes, int bytesPerPixel)
    {
        for (var r = 0; r < count; r++)
        {
            PushRow(rows.Slice(r * rowBytes, sourceWidth * bytesPerPixel), bytesPerPixel);
        }
    }

    public void PushRow(ReadOnlySpan<byte> row, int bytesPerPixel)
    {
        if (sourceRow >= sourceHeight)
        {
            return;
        }

        if (identity)
        {
            PushIdentity(row, bytesPerPixel);
            return;
        }

        AccumulateHorizontal(row, bytesPerPixel);
        AccumulateVertical();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<byte>.Shared.Return(outputRow);
        if (identity)
        {
            return;
        }

        ArrayPool<int>.Shared.Return(columnOffset);
        ArrayPool<float>.Shared.Return(columnWeight0);
        ArrayPool<float>.Shared.Return(columnWeight1);
        ArrayPool<float>.Shared.Return(horizontal);
        ArrayPool<float>.Shared.Return(current);
        ArrayPool<float>.Shared.Return(next);
    }

    private void PushIdentity(ReadOnlySpan<byte> row, int bytesPerPixel)
    {
        if (bytesPerPixel == 3)
        {
            writer.WriteRow(sourceRow, row);
        }
        else
        {
            var output = outputRow.AsSpan(0, targetWidth * 3);
            for (int s = 0, d = 0; d < output.Length; s += bytesPerPixel, d += 3)
            {
                output[d] = row[s];
                output[d + 1] = row[s + 1];
                output[d + 2] = row[s + 2];
            }

            writer.WriteRow(sourceRow, output);
        }

        sourceRow++;
        targetRow++;
    }

    private void AccumulateHorizontal(ReadOnlySpan<byte> row, int bytesPerPixel)
    {
        var h = horizontal.AsSpan(0, (targetWidth + 1) * 3);
        var offsets = columnOffset.AsSpan(0, sourceWidth);
        var w0 = columnWeight0.AsSpan(0, sourceWidth);
        var w1 = columnWeight1.AsSpan(0, sourceWidth);
        var pixels = row[..(sourceWidth * bytesPerPixel)];
        for (int i = 0, s = 0; i < offsets.Length; i++, s += bytesPerPixel)
        {
            var o = offsets[i];
            float r = pixels[s], g = pixels[s + 1], b = pixels[s + 2];
            var a0 = w0[i];
            var a1 = w1[i];
            h[o] += r * a0;
            h[o + 1] += g * a0;
            h[o + 2] += b * a0;
            h[o + 3] += r * a1;
            h[o + 4] += g * a1;
            h[o + 5] += b * a1;
        }
    }

    private void AccumulateVertical()
    {
        long start = (long)sourceRow * targetHeight;
        var end = start + targetHeight;
        var boundary = ((start / sourceHeight) + 1) * sourceHeight;
        var length = targetWidth * 3;
        var h = horizontal.AsSpan(0, length);
        var cur = current.AsSpan(0, length);
        if (end <= boundary)
        {
            float weight = targetHeight;
            for (var k = 0; k < h.Length; k++)
            {
                cur[k] += h[k] * weight;
            }
        }
        else
        {
            float weight0 = boundary - start;
            float weight1 = end - boundary;
            var nxt = next.AsSpan(0, length);
            for (var k = 0; k < h.Length; k++)
            {
                var v = h[k];
                cur[k] += v * weight0;
                nxt[k] += v * weight1;
            }
        }

        horizontal.AsSpan(0, (targetWidth + 1) * 3).Clear();
        sourceRow++;
        if (end >= boundary && targetRow < targetHeight)
        {
            EmitRow(cur);
            if (end > boundary)
            {
                next.AsSpan(0, length).CopyTo(cur);
                next.AsSpan(0, length).Clear();
            }
        }
    }

    private void EmitRow(Span<float> accumulated)
    {
        var output = outputRow.AsSpan(0, targetWidth * 3);
        var scale = normalize;
        for (var k = 0; k < output.Length; k++)
        {
            var v = (accumulated[k] * scale) + 0.5f;
            output[k] = v >= 255f ? (byte)255 : v <= 0f ? (byte)0 : (byte)v;
        }

        accumulated.Clear();
        writer.WriteRow(targetRow, output);
        targetRow++;
    }
}
