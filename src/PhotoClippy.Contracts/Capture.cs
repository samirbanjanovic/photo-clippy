namespace PhotoClippy.Contracts;

public enum DeviceClass
{
    Unknown,
    InterchangeableLens,
    FixedLens,
    Phone,
    Drone,
    Scanner,
    Software,
}

/// <summary>
/// Normalized facts for one capture (a shutter press). RAW+JPEG pairs and exports of the same frame are
/// merged into one capture. Focal lengths are given natively and as 35 mm equivalents.
/// </summary>
public sealed record CaptureFacts
{
    public required string CaptureId { get; init; }
    public required string PrimaryPath { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public IReadOnlyList<MediaKind> Kinds { get; init; } = [];

    public DateTime? CaptureLocal { get; init; }

    public DeviceClass Device { get; init; }
    public string? BodyKey { get; init; }
    public string? BodyDisplay { get; init; }
    public string? Make { get; init; }
    public string? SensorFormat { get; init; }
    public double? CropFactor { get; init; }

    public string? LensKey { get; init; }
    public string? LensDisplay { get; init; }
    public bool? LensIsZoom { get; init; }
    public double? LensMinFocalMm { get; init; }
    public double? LensMaxFocalMm { get; init; }
    public double? LensMaxApertureWide { get; init; }
    public double? LensMaxApertureTele { get; init; }

    public double? FocalMm { get; init; }
    public double? Focal35Mm { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureS { get; init; }
    public int? Iso { get; init; }
    /// <summary>Scene brightness: log2(N²/t) − log2(ISO/100).</summary>
    public double? Ev100 { get; init; }
    public double? ExposureBiasEv { get; init; }
    public bool? Flash { get; init; }
    public string? ExposureProgram { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>Crop provenance records retained from the files associated with this capture.</summary>
    public IReadOnlyList<CropProvenance> CropProvenanceRecords { get; init; } = [];
    public int? Rating { get; init; }
    public bool HasGps { get; init; }
    public string? Software { get; init; }
}
