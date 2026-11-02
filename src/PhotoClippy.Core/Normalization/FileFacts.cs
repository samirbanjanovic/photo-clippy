using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Normalization;

/// <summary>Normalized, compact per-file facts (the intermediate between PhotoMetadata and CaptureFacts).</summary>
public sealed record FileFacts
{
    public required SourceFile File { get; init; }
    public bool MetadataError { get; init; }

    public string? Make { get; init; }
    public string? BodyKey { get; init; }
    public string? BodyDisplay { get; init; }
    public string? BodySerial { get; init; }
    public string? Software { get; init; }
    public bool IsEdited { get; init; }

    public string? LensDisplay { get; init; }
    public LensSpecification? LensSpec { get; init; }

    public double? FocalMm { get; init; }
    public double? Focal35Exif { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureS { get; init; }
    public int? Iso { get; init; }
    public double? ExposureBiasEv { get; init; }
    public string? ExposureProgram { get; init; }
    public bool? Flash { get; init; }

    public DateTime? CaptureLocal { get; init; }
    public string? SubSec { get; init; }
    public string? ImageUniqueId { get; init; }

    /// <summary>Displayed dimensions (orientation applied).</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Orientation { get; init; }
    public double? FocalPlaneXResolutionPerMm { get; init; }
    public int? Rating { get; init; }
    public bool HasGps { get; init; }

    public bool HasExposure => FNumber is not null || ExposureS is not null || Iso is not null || FocalMm is not null;
}
