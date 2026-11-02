using System.Buffers;

namespace PhotoClippy.Plugins.Clip;

/// <summary>
/// CLIP image preprocessing: resize the shortest side to <c>size</c> with antialiased bicubic filtering
/// (Pillow semantics, as used by the Hugging Face CLIP image processor), centre-crop, normalize to CHW floats.
/// Only the pixels inside the crop window are resampled.
/// </summary>
internal static class ClipImagePreprocessor
{
    public const int DefaultSize = 224;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static (int Width, int Height) ResizedSize(int width, int height, int size) =>
        width <= height
            ? (size, Math.Max(size, (int)(size * (double)height / width)))
            : (Math.Max(size, (int)(size * (double)width / height)), size);

    public static void Preprocess(ReadOnlySpan<byte> rgb, int width, int height, int size, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (rgb.Length < width * height * 3)
        {
            throw new ArgumentException("Pixel buffer is smaller than width*height*3.", nameof(rgb));
        }

        var plane = size * size;
        if (destination.Length < 3 * plane)
        {
            throw new ArgumentException("Destination is smaller than 3*size*size.", nameof(destination));
        }

        var (resizedWidth, resizedHeight) = ResizedSize(width, height, size);
        var left = (resizedWidth - size) / 2;
        var top = (resizedHeight - size) / 2;

        var xs = Coefficients.Create(width, resizedWidth, left, size);
        var ys = Coefficients.Create(height, resizedHeight, top, size);
        var rowStart = ys.Min[0];
        var rowEnd = ys.Min[size - 1] + ys.Count[size - 1];
        var rows = rowEnd - rowStart;
        var lineLength = size * 3;
        var temp = ArrayPool<float>.Shared.Rent(rows * lineLength);
        try
        {
            HorizontalPass(rgb, width, rowStart, rows, xs, temp);
            VerticalPass(temp, ys, rowStart, size, destination);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
            xs.Dispose();
            ys.Dispose();
        }
    }

    private static void HorizontalPass(ReadOnlySpan<byte> rgb, int width, int rowStart, int rows, Coefficients xs, float[] temp)
    {
        var size = xs.Length;
        var lineLength = size * 3;
        for (var r = 0; r < rows; r++)
        {
            var src = rgb.Slice((rowStart + r) * width * 3, width * 3);
            var dst = temp.AsSpan(r * lineLength, lineLength);
            for (var x = 0; x < size; x++)
            {
                var weights = xs.WeightsFor(x);
                var offset = xs.Min[x] * 3;
                float red = 0, green = 0, blue = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var w = weights[k];
                    var p = offset + (k * 3);
                    red += w * src[p];
                    green += w * src[p + 1];
                    blue += w * src[p + 2];
                }

                dst[x * 3] = Clip8(red);
                dst[(x * 3) + 1] = Clip8(green);
                dst[(x * 3) + 2] = Clip8(blue);
            }
        }
    }

    private static void VerticalPass(float[] temp, Coefficients ys, int rowStart, int size, Span<float> destination)
    {
        var lineLength = size * 3;
        var plane = size * size;
        Span<float> scale = [1f / (255f * Std[0]), 1f / (255f * Std[1]), 1f / (255f * Std[2])];
        Span<float> bias = [-Mean[0] / Std[0], -Mean[1] / Std[1], -Mean[2] / Std[2]];
        var accumulator = ArrayPool<float>.Shared.Rent(lineLength);
        try
        {
            var acc = accumulator.AsSpan(0, lineLength);
            for (var y = 0; y < size; y++)
            {
                acc.Clear();
                var weights = ys.WeightsFor(y);
                var first = ys.Min[y] - rowStart;
                for (var k = 0; k < weights.Length; k++)
                {
                    var w = weights[k];
                    var line = temp.AsSpan((first + k) * lineLength, lineLength);
                    for (var i = 0; i < lineLength; i++)
                    {
                        acc[i] += w * line[i];
                    }
                }

                var rowOffset = y * size;
                for (var x = 0; x < size; x++)
                {
                    var o = rowOffset + x;
                    destination[o] = (Clip8(acc[x * 3]) * scale[0]) + bias[0];
                    destination[plane + o] = (Clip8(acc[(x * 3) + 1]) * scale[1]) + bias[1];
                    destination[(2 * plane) + o] = (Clip8(acc[(x * 3) + 2]) * scale[2]) + bias[2];
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(accumulator);
        }
    }

    private static float Clip8(float value) => value <= 0f ? 0f : value >= 255f ? 255f : MathF.Floor(value + 0.5f);

    private static double Bicubic(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1.0)
        {
            return ((((a + 2.0) * x) - (a + 3.0)) * x * x) + 1.0;
        }

        return x < 2.0 ? ((((x - 5.0) * x) + 8.0) * x - 4.0) * a : 0.0;
    }

    /// <summary>Pillow-style separable filter coefficients for output samples [first, first + length).</summary>
    private readonly struct Coefficients : IDisposable
    {
        private readonly float[] _weights;
        private readonly int _stride;

        private Coefficients(int[] min, int[] count, float[] weights, int stride, int length)
        {
            Min = min;
            Count = count;
            _weights = weights;
            _stride = stride;
            Length = length;
        }

        public int[] Min { get; }
        public int[] Count { get; }
        public int Length { get; }

        public ReadOnlySpan<float> WeightsFor(int i) => _weights.AsSpan(i * _stride, Count[i]);

        public static Coefficients Create(int inSize, int outSize, int first, int length)
        {
            const double support = 2.0;
            var scale = (double)inSize / outSize;
            var filterScale = Math.Max(scale, 1.0);
            var scaledSupport = support * filterScale;
            var stride = ((int)Math.Ceiling(scaledSupport) * 2) + 1;
            var min = ArrayPool<int>.Shared.Rent(length);
            var count = ArrayPool<int>.Shared.Rent(length);
            var weights = ArrayPool<float>.Shared.Rent(length * stride);
            Span<double> k = stackalloc double[stride];
            for (var i = 0; i < length; i++)
            {
                var center = (first + i + 0.5) * scale;
                var xmin = Math.Max((int)(center - scaledSupport + 0.5), 0);
                var xmax = Math.Min((int)(center + scaledSupport + 0.5), inSize) - xmin;
                xmax = Math.Clamp(xmax, 0, stride);
                var sum = 0.0;
                for (var x = 0; x < xmax; x++)
                {
                    var w = Bicubic((x + xmin - center + 0.5) / filterScale);
                    k[x] = w;
                    sum += w;
                }

                var row = weights.AsSpan(i * stride, stride);
                for (var x = 0; x < xmax; x++)
                {
                    row[x] = (float)(sum != 0.0 ? k[x] / sum : 0.0);
                }

                min[i] = xmin;
                count[i] = xmax;
            }

            return new Coefficients(min, count, weights, stride, length);
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(Min);
            ArrayPool<int>.Shared.Return(Count);
            ArrayPool<float>.Shared.Return(_weights);
        }
    }
}
