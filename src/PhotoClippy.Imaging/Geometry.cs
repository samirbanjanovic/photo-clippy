namespace PhotoClippy.Imaging;

internal static class SkiaCapabilities
{
    // Verified empirically: the SkiaSharp native builds for macOS/Linux ship without a HEIF/AVIF decoder.
    public static bool CanDecodeHeif => false;
}

internal readonly record struct Size(int Width, int Height)
{
    public int LongEdge => Math.Max(Width, Height);

    public Size Transposed => new(Height, Width);
}

internal static class Geometry
{
    public static bool SwapsAxes(int orientation) => orientation >= 5;

    /// <summary>Target stored size for a stored source size so the long edge is at most maxEdge (never upscaled).</summary>
    public static Size Target(Size source, int maxEdge)
    {
        var longEdge = source.LongEdge;
        if (longEdge <= maxEdge)
        {
            return source;
        }

        var scale = (double)maxEdge / longEdge;
        if (source.Width >= source.Height)
        {
            return new Size(maxEdge, Math.Clamp((int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero), 1, source.Height));
        }

        return new Size(Math.Clamp((int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero), 1, source.Width), maxEdge);
    }

    public static Size Displayed(Size stored, int orientation) => SwapsAxes(orientation) ? stored.Transposed : stored;

    public static bool SameAspect(Size a, Size b, double tolerance = 0.02)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
        {
            return false;
        }

        var ra = (double)a.Width / a.Height;
        var rb = (double)b.Width / b.Height;
        return Math.Abs(ra - rb) <= tolerance * Math.Max(ra, rb);
    }

    public static int NormalizeOrientation(int value) => value is >= 1 and <= 8 ? value : 1;

    public static int RgbLength(Size size)
    {
        var length = (long)size.Width * size.Height * 3;
        if (length > Array.MaxLength)
        {
            throw new ImageDecodeException("output frame is too large");
        }

        return (int)length;
    }
}
