using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Pipeline;

/// <summary>Field-by-field merge of metadata from several extractors: the earlier (higher priority) value wins.</summary>
public static class MetadataMerge
{
    public static PhotoMetadata Merge(PhotoMetadata primary, PhotoMetadata secondary)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in secondary.Extras)
        {
            extras[k] = v;
        }

        foreach (var (k, v) in primary.Extras)
        {
            extras[k] = v;
        }

        var cropProvenance = primary.CropProvenance ?? secondary.CropProvenance;
        if (primary.CropProvenance is { } primaryCrop && secondary.CropProvenance is { } secondaryCrop && primaryCrop != secondaryCrop)
        {
            cropProvenance = primaryCrop with
            {
                Status = CropEvidenceStatus.Ambiguous,
                ExclusionReason = "conflicting-crop-provenance",
            };
        }

        return primary with
        {
            Extractor = primary.Extractor is null ? secondary.Extractor : secondary.Extractor is null ? primary.Extractor : $"{primary.Extractor}+{secondary.Extractor}",
            Make = primary.Make ?? secondary.Make,
            Model = primary.Model ?? secondary.Model,
            BodySerial = primary.BodySerial ?? secondary.BodySerial,
            Software = primary.Software ?? secondary.Software,
            LensMake = primary.LensMake ?? secondary.LensMake,
            LensModel = primary.LensModel ?? secondary.LensModel,
            LensSerial = primary.LensSerial ?? secondary.LensSerial,
            LensSpec = primary.LensSpec ?? secondary.LensSpec,
            FocalLengthMm = primary.FocalLengthMm ?? secondary.FocalLengthMm,
            FocalLength35mm = primary.FocalLength35mm ?? secondary.FocalLength35mm,
            FNumber = primary.FNumber ?? secondary.FNumber,
            ExposureTimeS = primary.ExposureTimeS ?? secondary.ExposureTimeS,
            Iso = primary.Iso ?? secondary.Iso,
            ExposureBiasEv = primary.ExposureBiasEv ?? secondary.ExposureBiasEv,
            ExposureProgram = primary.ExposureProgram ?? secondary.ExposureProgram,
            MeteringMode = primary.MeteringMode ?? secondary.MeteringMode,
            WhiteBalance = primary.WhiteBalance ?? secondary.WhiteBalance,
            FlashFired = primary.FlashFired ?? secondary.FlashFired,
            CaptureLocal = primary.CaptureLocal ?? secondary.CaptureLocal,
            OffsetTime = primary.OffsetTime ?? secondary.OffsetTime,
            SubSec = primary.SubSec ?? secondary.SubSec,
            ImageUniqueId = primary.ImageUniqueId ?? secondary.ImageUniqueId,
            Width = primary.Width ?? secondary.Width,
            Height = primary.Height ?? secondary.Height,
            Orientation = primary.Orientation ?? secondary.Orientation,
            CropProvenance = cropProvenance,
            FocalPlaneXResolutionPerMm = primary.FocalPlaneXResolutionPerMm ?? secondary.FocalPlaneXResolutionPerMm,
            SubjectDistanceM = primary.SubjectDistanceM ?? secondary.SubjectDistanceM,
            Rating = primary.Rating ?? secondary.Rating,
            HasGps = primary.HasGps || secondary.HasGps,
            Latitude = primary.Latitude ?? secondary.Latitude,
            Longitude = primary.Longitude ?? secondary.Longitude,
            Warnings = [.. primary.Warnings, .. secondary.Warnings],
            Extras = extras,
        };
    }
}
