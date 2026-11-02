using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>Mutable accumulator used while mapping one file; converted to an immutable <see cref="PhotoMetadata"/>.</summary>
internal sealed class MetadataDraft
{
    private List<string>? _warnings;
    private Dictionary<string, string>? _extras;

    public string? Make;
    public string? Model;
    public string? BodySerial;
    public string? Software;
    public string? LensMake;
    public string? LensModel;
    public string? LensSerial;
    public LensSpecification? LensSpec;
    public double? FocalLengthMm;
    public double? FocalLength35mm;
    public double? FNumber;
    public double? ExposureTimeS;
    public int? Iso;
    public double? ExposureBiasEv;
    public string? ExposureProgram;
    public string? MeteringMode;
    public string? WhiteBalance;
    public bool? FlashFired;
    public DateTime? CaptureLocal;
    public string? OffsetTime;
    public string? SubSec;
    public string? ImageUniqueId;
    public int? Width;
    public int? Height;
    public int? Orientation;
    public double? FocalPlaneXResolutionPerMm;
    public double? SubjectDistanceM;
    public int? Rating;
    public bool HasGps;
    public double? Latitude;
    public double? Longitude;

    public void Warn(string message)
    {
        _warnings ??= [];
        if (_warnings.Count < 16)
        {
            _warnings.Add(message.Length > 200 ? message[..200] : message);
        }
    }

    public void SetExtra(string key, string value)
    {
        _extras ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _extras[key] = value;
    }

    /// <summary>Fills gaps (null members only) from values found in an XMP packet.</summary>
    public void FillFrom(XmpValues xmp, string source)
    {
        var used = false;
        if (Rating is null && xmp.Rating is { } rating)
        {
            Rating = rating;
            used = true;
        }

        if (LensModel is null && xmp.LensModel is { } lens)
        {
            LensModel = lens;
            SetExtra("lensSource", source);
            used = true;
        }

        if (LensMake is null && xmp.LensMake is { } lensMake)
        {
            LensMake = lensMake;
            used = true;
        }

        if (LensSpec is null && xmp.LensSpec is { } spec)
        {
            LensSpec = spec;
            used = true;
        }

        if (BodySerial is null && xmp.BodySerial is { } serial)
        {
            BodySerial = serial;
            used = true;
        }

        if (LensSerial is null && xmp.LensSerial is { } lensSerial)
        {
            LensSerial = lensSerial;
            used = true;
        }

        if (used && source == "sidecar" && xmp.SourcePath is { } path)
        {
            SetExtra("sidecar", Path.GetFileName(path));
        }
    }

    public PhotoMetadata Build(string path, string extractor) => new()
    {
        Path = path,
        Extractor = extractor,
        Make = Make,
        Model = Model,
        BodySerial = BodySerial,
        Software = Software,
        LensMake = LensMake,
        LensModel = LensModel,
        LensSerial = LensSerial,
        LensSpec = LensSpec,
        FocalLengthMm = FocalLengthMm,
        FocalLength35mm = FocalLength35mm,
        FNumber = FNumber,
        ExposureTimeS = ExposureTimeS,
        Iso = Iso,
        ExposureBiasEv = ExposureBiasEv,
        ExposureProgram = ExposureProgram,
        MeteringMode = MeteringMode,
        WhiteBalance = WhiteBalance,
        FlashFired = FlashFired,
        CaptureLocal = CaptureLocal,
        OffsetTime = OffsetTime,
        SubSec = SubSec,
        ImageUniqueId = ImageUniqueId,
        Width = Width,
        Height = Height,
        Orientation = Orientation,
        FocalPlaneXResolutionPerMm = FocalPlaneXResolutionPerMm,
        SubjectDistanceM = SubjectDistanceM,
        Rating = Rating,
        HasGps = HasGps,
        Latitude = HasGps ? Latitude : null,
        Longitude = HasGps ? Longitude : null,
        Warnings = _warnings is null ? [] : _warnings.ToArray(),
        Extras = _extras is null ? EmptyExtras : _extras,
    };

    public static PhotoMetadata Failed(string path, string extractor, string warning) => new()
    {
        Path = path,
        Extractor = extractor,
        Warnings = [warning.Length > 200 ? warning[..200] : warning],
        Extras = EmptyExtras,
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyExtras = new Dictionary<string, string>(0);
}
