namespace PhotoClippy.Contracts;

/// <summary>
/// A decoded, orientation-corrected sRGB image, shared by all frame analyzers for one capture.
/// Pixels are 8-bit RGB interleaved (RGB24), row stride = <see cref="Width"/> * 3, no padding.
/// A frame is only valid for the duration of the <c>AnalyzeAsync</c> call; do not retain it.
/// </summary>
public sealed class Frame
{
    public Frame(int width, int height, ReadOnlyMemory<byte> rgb, int sourceWidth, int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgb.Length < width * height * 3)
        {
            throw new ArgumentException("Pixel buffer is smaller than width*height*3.", nameof(rgb));
        }

        Width = width;
        Height = height;
        Rgb = rgb[..(width * height * 3)];
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Rgb { get; }

    /// <summary>Dimensions of the full image (after orientation), before downscaling.</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public double AspectRatio => (double)Width / Height;
}

/// <summary>What a frame analyzer needs. The host decodes once at the largest requested edge.</summary>
public sealed record FrameRequirements(int MaxEdge);

public sealed record AnalysisContext
{
    public required string CaptureId { get; init; }
    public required string SourcePath { get; init; }
    /// <summary>Normalized metadata; lets analyzers use priors such as focal length or EV.</summary>
    public CaptureFacts? Facts { get; init; }
}

/// <summary>
/// Composition component output. All fields are optional: several analyzers are merged
/// (scalars: first non-null by priority; genres: weighted fusion; extras: union).
/// Coordinates are normalized 0..1 with origin top-left.
/// </summary>
public sealed record CompositionFeatures
{
    /// <summary>Genre id → probability (sums to ≤ 1). Ids come from the <see cref="GenreTaxonomy"/>.</summary>
    public IReadOnlyDictionary<string, double>? Genres { get; init; }

    public double? SubjectX { get; init; }
    public double? SubjectY { get; init; }
    /// <summary>Fraction of the frame covered by the salient subject.</summary>
    public double? SubjectArea { get; init; }
    /// <summary>0..1, closeness of the subject to a rule-of-thirds power point.</summary>
    public double? ThirdsScore { get; init; }
    /// <summary>0..1, closeness of the subject to the frame centre.</summary>
    public double? CenterScore { get; init; }
    /// <summary>0..1 left/right mirror symmetry.</summary>
    public double? SymmetryScore { get; init; }
    /// <summary>0..1 share of low-detail, low-saliency area.</summary>
    public double? NegativeSpace { get; init; }
    /// <summary>0..1 subject/background sharpness separation (a proxy for shallow depth of field).</summary>
    public double? SeparationScore { get; init; }
    /// <summary>Detected horizon tilt in degrees (positive = clockwise), null when no horizon.</summary>
    public double? HorizonTiltDeg { get; init; }
    /// <summary>Vertical position of the detected horizon, 0 = top.</summary>
    public double? HorizonY { get; init; }
    /// <summary>Edge-orientation energy shares; sum to 1.</summary>
    public double? HorizontalEdges { get; init; }
    public double? VerticalEdges { get; init; }
    public double? DiagonalEdges { get; init; }

    public IReadOnlyDictionary<string, double> Extras { get; init; } = new Dictionary<string, double>();
}

public sealed record PaletteColor(string Hex, double Weight);

/// <summary>Color / tonal component output. Luma values are 0..1 (perceptual lightness, OKLab L).</summary>
public sealed record ColorFeatures
{
    public double? MeanLuma { get; init; }
    public double? LumaP05 { get; init; }
    public double? LumaP50 { get; init; }
    public double? LumaP95 { get; init; }
    /// <summary>Share of pixels crushed to black / clipped to white.</summary>
    public double? ShadowClip { get; init; }
    public double? HighlightClip { get; init; }
    /// <summary>0..1, p95 − p05 of lightness.</summary>
    public double? Contrast { get; init; }
    /// <summary>"low", "mid" or "high" key.</summary>
    public string? Key { get; init; }
    /// <summary>Hasler–Süsstrunk colorfulness scaled to roughly 0..1.</summary>
    public double? Colorfulness { get; init; }
    public double? MeanChroma { get; init; }
    public double? Saturation { get; init; }
    public bool? Monochrome { get; init; }
    /// <summary>Warm (+) / cool (−) cast, roughly −1..1 (OKLab b).</summary>
    public double? Warmth { get; init; }
    /// <summary>Magenta (+) / green (−) tint, roughly −1..1 (OKLab a).</summary>
    public double? Tint { get; init; }
    /// <summary>0..1 normalized Laplacian-variance detail measure.</summary>
    public double? Sharpness { get; init; }

    /// <summary>12 chroma-weighted hue bins (0°, 30°, …), normalized to sum 1.</summary>
    public IReadOnlyList<double>? HueHistogram { get; init; }
    /// <summary>16 lightness bins (0..1), normalized to sum 1.</summary>
    public IReadOnlyList<double>? LumaHistogram { get; init; }
    /// <summary>Dominant colors, heaviest first.</summary>
    public IReadOnlyList<PaletteColor>? Palette { get; init; }

    public IReadOnlyDictionary<string, double> Extras { get; init; } = new Dictionary<string, double>();
}

/// <summary>Composition component (geometry, subject, genre recognition).</summary>
public interface ICompositionAnalyzer : IPlugin
{
    FrameRequirements Requirements { get; }

    ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default);
}

/// <summary>Color and tonal analytics component.</summary>
public interface IColorAnalyzer : IPlugin
{
    FrameRequirements Requirements { get; }

    ValueTask<ColorFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default);
}
