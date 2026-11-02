namespace PhotoClippy.Plugins.Composition;

/// <summary>Raw (unrounded) pixel-derived composition measurements; also the pixel half of the genre-prior signals.</summary>
internal sealed record PixelFeatures
{
    public static readonly PixelFeatures Empty = new();

    public int Width { get; init; }
    public int Height { get; init; }

    public bool HasSubject => SubjectX is not null;
    public double? SubjectX { get; init; }
    public double? SubjectY { get; init; }
    public double? SubjectArea { get; init; }
    public double? ThirdsScore { get; init; }
    public double? CenterScore { get; init; }
    public double? SymmetryScore { get; init; }
    public double? NegativeSpace { get; init; }
    public double? SeparationScore { get; init; }

    public bool HasHorizon => HorizonTiltDeg is not null;
    public double? HorizonTiltDeg { get; init; }
    public double? HorizonY { get; init; }
    public double? HorizonSpan { get; init; }
    public double? HorizonContrast { get; init; }

    public double? HorizontalEdges { get; init; }
    public double? VerticalEdges { get; init; }
    public double? DiagonalEdges { get; init; }

    public int SubjectCount { get; init; }
    public double? SubjectContrast { get; init; }
    public double SaliencySpread { get; init; }
    public double SaliencyPeak { get; init; }
    public double BrightSky { get; init; }
    public double EdgeDensity { get; init; }
    public double FlatShare { get; init; }
    public double WhiteShare { get; init; }
    public double DarkShare { get; init; }
    public double MeanChroma { get; init; }
    public double MeanLightness { get; init; }
}
