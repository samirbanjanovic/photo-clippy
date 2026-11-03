using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.BehavioralFocal;

public sealed record CropGeometry(
    double RetainedWidth,
    double RetainedHeight,
    double PixelsRetained,
    double CropDependence,
    double? FinalEquivalentFocal35Mm,
    double HorizontalEquivalentFocal35Mm,
    double VerticalEquivalentFocal35Mm,
    double Left,
    double Top,
    double Right,
    double Bottom);

public sealed record CropGeometryResult(CropGeometry? Geometry, string? ExclusionReason)
{
    public bool IsEligible => Geometry is not null;
}

public static class CropGeometryCalculator
{
    private const double AspectTolerance = 0.02;
    private const double SameAspectTolerance = 1e-6;

    public static double? NormalizeFocalEquivalent35Mm(double? focalMm, double? cropFactor)
    {
        if (!IsPositiveFinite(focalMm) || !IsPositiveFinite(cropFactor))
        {
            return null;
        }

        var result = focalMm!.Value * cropFactor!.Value;
        return double.IsFinite(result) && result > 0 ? result : null;
    }

    public static CropGeometryResult Calculate(double? focalEquivalent35Mm, CropProvenance? evidence)
    {
        if (evidence is null)
        {
            return Excluded("crop-provenance-absent");
        }

        if (evidence.Status != CropEvidenceStatus.Verified)
        {
            return Excluded(IsReasonCode(evidence.ExclusionReason)
                ? evidence.ExclusionReason!
                : $"crop-evidence-{evidence.Status.ToString().ToLowerInvariant()}");
        }

        if (evidence.Source != CropEvidenceSource.ExternalMetadataPlugin)
        {
            return Excluded("crop-provenance-source-unsupported");
        }

        if (!IsOpaqueIdentity(evidence.OriginalCaptureId) || !IsOpaqueIdentity(evidence.FinalCaptureId) ||
            string.Equals(evidence.OriginalCaptureId, evidence.FinalCaptureId, StringComparison.Ordinal) ||
            !IsOpaqueIdentity(evidence.IndependentSessionId))
        {
            return Excluded("crop-link-or-session-identity-missing");
        }

        if (!IsPositiveFinite(focalEquivalent35Mm) || evidence.FocalEquivalentIncludesCrop is null)
        {
            return Excluded("focal-normalization-missing-or-invalid");
        }

        if (!IsPositive(evidence.OriginalWidth) || !IsPositive(evidence.OriginalHeight) ||
            !IsPositive(evidence.FinalWidth) || !IsPositive(evidence.FinalHeight) ||
            !IsOrientation(evidence.OriginalOrientation) || !IsOrientation(evidence.FinalOrientation))
        {
            return Excluded("crop-dimensions-or-orientation-invalid");
        }

        if (!IsUnit(evidence.Left) || !IsUnit(evidence.Top) || !IsUnit(evidence.Right) || !IsUnit(evidence.Bottom) ||
            evidence.Left >= evidence.Right || evidence.Top >= evidence.Bottom)
        {
            return Excluded("crop-bounds-invalid");
        }

        var originalWidth = evidence.OriginalWidth.GetValueOrDefault();
        var originalHeight = evidence.OriginalHeight.GetValueOrDefault();
        var originalOrientation = evidence.OriginalOrientation.GetValueOrDefault();
        var finalWidthEncoded = evidence.FinalWidth.GetValueOrDefault();
        var finalHeightEncoded = evidence.FinalHeight.GetValueOrDefault();
        var finalOrientation = evidence.FinalOrientation.GetValueOrDefault();
        var left = evidence.Left.GetValueOrDefault();
        var top = evidence.Top.GetValueOrDefault();
        var right = evidence.Right.GetValueOrDefault();
        var bottom = evidence.Bottom.GetValueOrDefault();
        var sourceWidth = DisplayWidth(originalWidth, originalHeight, originalOrientation);
        var sourceHeight = DisplayHeight(originalWidth, originalHeight, originalOrientation);
        var finalWidth = DisplayWidth(finalWidthEncoded, finalHeightEncoded, finalOrientation);
        var finalHeight = DisplayHeight(finalWidthEncoded, finalHeightEncoded, finalOrientation);
        var retainedWidth = right - left;
        var retainedHeight = bottom - top;
        var cropAspect = (sourceWidth / (double)sourceHeight) * retainedWidth / retainedHeight;
        var finalAspect = finalWidth / (double)finalHeight;
        if (!double.IsFinite(cropAspect) || !double.IsFinite(finalAspect) ||
            Math.Abs(cropAspect - finalAspect) / finalAspect > AspectTolerance)
        {
            return Excluded("crop-aspect-orientation-mismatch");
        }

        var focal = focalEquivalent35Mm!.Value;
        var includesCrop = evidence.FocalEquivalentIncludesCrop.Value;
        var equivalentWidth = includesCrop ? finalWidth : sourceWidth;
        var equivalentHeight = includesCrop ? finalHeight : sourceHeight;
        var diagonal35Mm = Math.Sqrt(36 * 36 + 24 * 24);
        var equivalentDiagonal = Math.Sqrt((double)equivalentWidth * equivalentWidth + (double)equivalentHeight * equivalentHeight);
        var horizontalEquivalent = focal * (36 / diagonal35Mm) * (equivalentDiagonal / equivalentWidth);
        var verticalEquivalent = focal * (24 / diagonal35Mm) * (equivalentDiagonal / equivalentHeight);
        if (!includesCrop)
        {
            horizontalEquivalent /= retainedWidth;
            verticalEquivalent /= retainedHeight;
        }
        if (!double.IsFinite(horizontalEquivalent) || !double.IsFinite(verticalEquivalent))
        {
            return Excluded("crop-geometry-nonfinite");
        }

        double? finalEquivalent = Math.Abs(retainedWidth - retainedHeight) <= SameAspectTolerance
            ? includesCrop ? focal : focal / retainedWidth
            : null;
        var retainedArea = retainedWidth * retainedHeight;
        return new CropGeometryResult(
            new CropGeometry(
                retainedWidth,
                retainedHeight,
                retainedArea,
                1 - retainedArea,
                finalEquivalent,
                horizontalEquivalent,
                verticalEquivalent,
                left,
                top,
                right,
                bottom),
            null);
    }

    private static CropGeometryResult Excluded(string reason) => new(null, reason);

    private static bool IsPositiveFinite(double? value) => value is { } v && double.IsFinite(v) && v > 0;

    private static bool IsPositive(int? value) => value is > 0;

    private static bool IsOrientation(int? value) => value is >= 1 and <= 8;

    private static bool IsUnit(double? value) => value is { } v && double.IsFinite(v) && v is >= 0 and <= 1;

    private static bool IsOpaqueIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsReasonCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static int DisplayWidth(int width, int height, int orientation) => orientation is >= 5 and <= 8 ? height : width;

    private static int DisplayHeight(int width, int height, int orientation) => orientation is >= 5 and <= 8 ? width : height;
}
