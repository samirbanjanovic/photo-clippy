namespace PhotoClippy.Contracts;

public enum CropEvidenceStatus
{
    Unknown,
    Verified,
    Unsupported,
    Ambiguous,
    Invalid,
}

public enum CropEvidenceSource
{
    Unknown,
    ExternalMetadataPlugin,
}

/// <summary>
/// An explicit original-to-final crop record. Bounds are normalized to the displayed, orientation-corrected
/// original image; dimensions and EXIF orientations describe the encoded files. IDs are opaque and must not be paths.
/// </summary>
public sealed record CropProvenance
{
    public CropEvidenceStatus Status { get; init; }
    public CropEvidenceSource Source { get; init; }
    public string? OriginalCaptureId { get; init; }
    public string? FinalCaptureId { get; init; }
    public string? IndependentSessionId { get; init; }
    public int? OriginalWidth { get; init; }
    public int? OriginalHeight { get; init; }
    public int? OriginalOrientation { get; init; }
    public int? FinalWidth { get; init; }
    public int? FinalHeight { get; init; }
    public int? FinalOrientation { get; init; }
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Right { get; init; }
    public double? Bottom { get; init; }
    /// <summary>Whether the capture's reported 35 mm equivalent already includes this crop.</summary>
    public bool? FocalEquivalentIncludesCrop { get; init; }
    /// <summary>A bounded, non-path reason when evidence is unsupported, ambiguous, or invalid.</summary>
    public string? ExclusionReason { get; init; }
}
