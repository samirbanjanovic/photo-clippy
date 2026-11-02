namespace PhotoClippy.Contracts;

public enum AxisScale
{
    Linear,
    Log,
    Ordinal,
    Categorical,
}

/// <summary>One histogram bucket. <see cref="Share"/> is Count / total of the distribution.</summary>
public sealed record Bin
{
    public required string Label { get; init; }
    public required double Count { get; init; }
    public double Share { get; init; }
    public double? Lower { get; init; }
    public double? Upper { get; init; }
    /// <summary>Machine key (e.g. genre id or lens key) when the label is a display string.</summary>
    public string? Key { get; init; }
}

public sealed record SummaryStats
{
    public int Count { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P10 { get; init; }
    public double P90 { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
}

public sealed record Distribution
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Unit { get; init; }
    public AxisScale Scale { get; init; } = AxisScale.Linear;
    public IReadOnlyList<Bin> Bins { get; init; } = [];
    public SummaryStats? Stats { get; init; }
    public double Total => Bins.Sum(b => b.Count);
}

/// <summary>Cross-tabulation. <see cref="Values"/>[y][x]; rows follow <see cref="YLabels"/>.</summary>
public sealed record Matrix
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? XTitle { get; init; }
    public string? YTitle { get; init; }
    public IReadOnlyList<string> XLabels { get; init; } = [];
    public IReadOnlyList<string> YLabels { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<double>> Values { get; init; } = [];
    public double Total { get; init; }
}

public sealed record BodyUsage
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public string? Make { get; init; }
    public string? Format { get; init; }
    public double? CropFactor { get; init; }
    public DeviceClass Device { get; init; }
    public int Captures { get; init; }
    public double Share { get; init; }
    public DateTime? First { get; init; }
    public DateTime? Last { get; init; }
}

public sealed record LensUsage
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public string? Make { get; init; }
    public bool IsZoom { get; init; }
    /// <summary>True for lenses of fixed-lens cameras and phones.</summary>
    public bool IsFixed { get; init; }
    public double? FocalMinMm { get; init; }
    public double? FocalMaxMm { get; init; }
    public double? FocalMin35 { get; init; }
    public double? FocalMax35 { get; init; }
    public double? MaxApertureWide { get; init; }
    public double? MaxApertureTele { get; init; }
    /// <summary>Where focal range / maximum aperture came from: Exif (LensSpecification) or Observed.</summary>
    public LensSpecSource SpecSource { get; init; } = LensSpecSource.Observed;
    public int Captures { get; init; }
    public double Share { get; init; }
    public double? MedianFocal35 { get; init; }
    public double? MedianFNumber { get; init; }
    public double? MedianIso { get; init; }
    /// <summary>Share of frames shot within ⅓ stop of the lens' maximum aperture.</summary>
    public double WideOpenShare { get; init; }
    /// <summary>Zooms only: share of frames at the wide / tele end stop.</summary>
    public double WideEndShare { get; init; }
    public double TeleEndShare { get; init; }
    public double LightStarvedShare { get; init; }
    public string? TopGenre { get; init; }
    public IReadOnlyList<string> Bodies { get; init; } = [];
    public DateTime? First { get; init; }
    public DateTime? Last { get; init; }
}

public sealed record FocalPeak(double Focal35Mm, double Share, double LowerMm, double UpperMm);

public sealed record LibrarySummary
{
    public IReadOnlyList<string> Roots { get; init; } = [];
    public int FilesSeen { get; init; }
    public int FilesWithMetadata { get; init; }
    public int Captures { get; init; }
    /// <summary>Captures included in shooting statistics (after scope and junk filtering).</summary>
    public int CapturesInScope { get; init; }
    public int FramesAnalyzed { get; init; }
    public int RawPlusJpeg { get; init; }
    public DateTime? FirstCapture { get; init; }
    public DateTime? LastCapture { get; init; }
    /// <summary>Scope of shooting statistics: "interchangeable" or "all".</summary>
    public string Scope { get; init; } = "interchangeable";
    public IReadOnlyDictionary<string, int> FileKinds { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> DeviceClasses { get; init; } = new Dictionary<string, int>();
    public long TotalBytes { get; init; }
}

/// <summary>Headline "pressure" signals, shares 0..1 of in-scope captures with the needed data.</summary>
public sealed record ShootingSignals
{
    public int SampleCount { get; init; }
    public double ApertureCeilingShare { get; init; }
    public double LightStarvedShare { get; init; }
    public double HighIsoShare { get; init; }
    public double SlowShutterShare { get; init; }
    public double WideEndShare { get; init; }
    public double TeleEndShare { get; init; }
    public double FlashShare { get; init; }
    public double PrimeShare { get; init; }
    public double ZoomShare { get; init; }
    public double? MedianFocal35 { get; init; }
    public double? MedianFNumber { get; init; }
    public double? MedianIso { get; init; }
    public double? MedianEv100 { get; init; }
    /// <summary>Inter-quartile range of focal lengths, in stops (log2).</summary>
    public double? FocalSpreadStops { get; init; }
    public double? MedianSeparation { get; init; }
}

public sealed record DataQuality
{
    public int MissingFocal { get; init; }
    public int MissingAperture { get; init; }
    public int MissingIso { get; init; }
    public int MissingShutter { get; init; }
    public int MissingLens { get; init; }
    public int MissingDate { get; init; }
    public int MissingBody { get; init; }
    public int MetadataErrors { get; init; }
    public int DecodeFailed { get; init; }
    public int NotDecodable { get; init; }
    public int JunkExcluded { get; init; }
    /// <summary>e.g. "clip+heuristic", "heuristic", "none".</summary>
    public string GenreSource { get; init; } = "none";
    public double? MeanGenreConfidence { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Aggregated, privacy-preserving description of how the user shoots. This is the stable hand-off
/// to recommenders, renderers and (later) LLM advisors. Contains no file paths or coordinates.
/// </summary>
public sealed record ShootingProfile
{
    public required LibrarySummary Library { get; init; }
    public TargetSystem? Target { get; init; }
    public IReadOnlyList<BodyUsage> Bodies { get; init; } = [];
    public IReadOnlyList<LensUsage> Lenses { get; init; } = [];
    /// <summary>Keyed by <see cref="DistributionIds"/>.</summary>
    public IReadOnlyDictionary<string, Distribution> Distributions { get; init; } = new Dictionary<string, Distribution>();
    /// <summary>Keyed by <see cref="MatrixIds"/>.</summary>
    public IReadOnlyDictionary<string, Matrix> Matrices { get; init; } = new Dictionary<string, Matrix>();
    public IReadOnlyList<PaletteColor> Palette { get; init; } = [];
    public IReadOnlyList<FocalPeak> FocalPeaks { get; init; } = [];
    public ShootingSignals Signals { get; init; } = new();
    /// <summary>Generic distributions built from plugin <c>Extras</c>, keyed "&lt;component&gt;.&lt;name&gt;".</summary>
    public IReadOnlyDictionary<string, Distribution> Extensions { get; init; } = new Dictionary<string, Distribution>();
    public DataQuality Quality { get; init; } = new();
}

/// <summary>Well-known distribution ids. Renderers must tolerate any of them being absent.</summary>
public static class DistributionIds
{
    public const string Focal35 = "focal.eq";
    public const string FocalNative = "focal.native";
    public const string Aperture = "aperture";
    /// <summary>Stops stopped down from the lens' maximum aperture (0 = wide open).</summary>
    public const string StopsFromMax = "aperture.stopsFromMax";
    public const string Iso = "iso";
    public const string Shutter = "shutter";
    public const string Ev100 = "ev100";
    public const string ExposureBias = "exposureBias";
    public const string ExposureProgram = "exposureProgram";
    public const string Flash = "flash";
    public const string Lens = "lens";
    public const string Body = "body";
    public const string DeviceClass = "deviceClass";
    public const string Genre = "genre";
    public const string GenreGroup = "genre.group";
    public const string Orientation = "orientation";
    public const string AspectRatio = "aspectRatio";
    public const string Hour = "time.hour";
    public const string Weekday = "time.weekday";
    public const string Month = "time.month";
    public const string Year = "time.year";
    /// <summary>Captures per calendar month ("yyyy-MM"), chronological.</summary>
    public const string Timeline = "time.timeline";
    public const string Rating = "rating";

    public const string ColorKey = "color.key";
    public const string Monochrome = "color.monochrome";
    public const string Colorfulness = "color.colorfulness";
    public const string Contrast = "color.contrast";
    public const string Warmth = "color.warmth";
    public const string Saturation = "color.saturation";
    public const string Sharpness = "color.sharpness";
    public const string Brightness = "color.brightness";
    /// <summary>Average 16-bin lightness histogram across frames.</summary>
    public const string Tone = "color.tone";
    /// <summary>Chroma-weighted 12-bin hue wheel (labels are degrees: "0", "30", …).</summary>
    public const string Hue = "color.hue";
    public const string HighlightClip = "color.highlightClip";
    public const string ShadowClip = "color.shadowClip";

    public const string Thirds = "composition.thirds";
    public const string Center = "composition.center";
    public const string Symmetry = "composition.symmetry";
    public const string NegativeSpace = "composition.negativeSpace";
    public const string Separation = "composition.separation";
    public const string SubjectArea = "composition.subjectArea";
    public const string HorizonTilt = "composition.horizonTilt";
    public const string EdgeOrientation = "composition.edges";
}

public static class MatrixIds
{
    public const string FocalByAperture = "focal.aperture";
    public const string IsoByShutter = "iso.shutter";
    public const string GenreByFocal = "genre.focal";
    public const string GenreByAperture = "genre.aperture";
    public const string LensByGenre = "lens.genre";
    public const string LensByYear = "lens.year";
    public const string HourByWeekday = "time.hour.weekday";
    /// <summary>3×3 subject placement grid (rows top→bottom, columns left→right).</summary>
    public const string Placement = "composition.placement";
}
