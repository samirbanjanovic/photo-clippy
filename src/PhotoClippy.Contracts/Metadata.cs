using System.Text.Json.Serialization;

namespace PhotoClippy.Contracts;

public enum MediaKind
{
    Jpeg,
    Heif,
    Tiff,
    Png,
    Raw,
    Other,
}

public sealed record SourceFile
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public required MediaKind Kind { get; init; }

    [JsonIgnore]
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>EXIF LensSpecification (0xA432): focal range and the maximum aperture at each end.</summary>
public sealed record LensSpecification(
    double MinFocalMm,
    double MaxFocalMm,
    double? MaxApertureAtMinFocal,
    double? MaxApertureAtMaxFocal);

/// <summary>
/// Raw-but-typed metadata for one file as reported by an extractor. Units: mm, seconds, f-number,
/// ISO. Fields are null when unknown. Several extractors may be merged field by field.
/// </summary>
public sealed record PhotoMetadata
{
    public required string Path { get; init; }
    public string? Extractor { get; init; }

    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? BodySerial { get; init; }
    public string? Software { get; init; }

    public string? LensMake { get; init; }
    public string? LensModel { get; init; }
    public string? LensSerial { get; init; }
    public LensSpecification? LensSpec { get; init; }

    public double? FocalLengthMm { get; init; }
    public double? FocalLength35mm { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureTimeS { get; init; }
    public int? Iso { get; init; }
    public double? ExposureBiasEv { get; init; }
    public string? ExposureProgram { get; init; }
    public string? MeteringMode { get; init; }
    public string? WhiteBalance { get; init; }
    public bool? FlashFired { get; init; }

    /// <summary>DateTimeOriginal as written by the camera (local wall-clock time).</summary>
    public DateTime? CaptureLocal { get; init; }
    public string? OffsetTime { get; init; }
    public string? SubSec { get; init; }
    public string? ImageUniqueId { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>EXIF orientation 1..8.</summary>
    public int? Orientation { get; init; }

    /// <summary>Focal plane X resolution in pixels per mm; used to estimate sensor size.</summary>
    public double? FocalPlaneXResolutionPerMm { get; init; }
    public double? SubjectDistanceM { get; init; }
    public int? Rating { get; init; }

    public bool HasGps { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, string> Extras { get; init; } = new Dictionary<string, string>();
}

/// <summary>Metadata component. Called with batches; must return exactly one result per input, in order.</summary>
public interface IMetadataExtractor : IPlugin
{
    bool CanHandle(SourceFile file);

    Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default);
}
