namespace PhotoClippy.Imaging;

/// <summary>Writes stored-orientation RGB24 rows into a displayed-orientation RGB24 buffer.</summary>
internal sealed class OrientedWriter
{
    private readonly byte[] destination;
    private readonly int width;
    private readonly int height;
    private readonly int orientation;
    private readonly int destinationWidth;
    private readonly int origin;
    private readonly int stepX;
    private readonly int stepY;

    public OrientedWriter(byte[] destination, int width, int height, int orientation)
    {
        this.destination = destination;
        this.width = width;
        this.height = height;
        this.orientation = Geometry.NormalizeOrientation(orientation);
        destinationWidth = this.orientation >= 5 ? height : width;
        var dw = destinationWidth;
        (origin, stepX, stepY) = this.orientation switch
        {
            2 => (width - 1, -1, dw),
            3 => (((height - 1) * dw) + width - 1, -1, -dw),
            4 => ((height - 1) * dw, 1, -dw),
            5 => (0, dw, 1),
            6 => (height - 1, dw, -1),
            7 => (((width - 1) * dw) + height - 1, -dw, -1),
            8 => ((width - 1) * dw, -dw, 1),
            _ => (0, 1, dw),
        };
    }

    public int StoredWidth => width;

    public int StoredHeight => height;

    public void WriteRow(int y, ReadOnlySpan<byte> row)
    {
        var src = row[..(width * 3)];
        if (orientation == 1)
        {
            src.CopyTo(destination.AsSpan(y * width * 3, width * 3));
            return;
        }

        var dst = destination.AsSpan(0, width * height * 3);
        var step = stepX * 3;
        var d = (origin + (y * stepY)) * 3;
        if (step == 3)
        {
            src.CopyTo(dst.Slice(d, width * 3));
            return;
        }

        for (var s = 0; s < src.Length; s += 3, d += step)
        {
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2];
        }
    }
}
