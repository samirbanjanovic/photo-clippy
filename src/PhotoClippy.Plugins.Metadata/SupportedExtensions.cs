using System.Collections.Frozen;

namespace PhotoClippy.Plugins.Metadata;

internal static class SupportedExtensions
{
    private static readonly string[] RawExtensions =
    [
        ".dng", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".cr2", ".cr3", ".crw", ".raf", ".orf", ".rw2", ".pef",
        ".srw", ".3fr", ".erf", ".kdc", ".mef", ".mos", ".iiq", ".x3f", ".raw",
    ];

    private static readonly string[] CommonExtensions =
    [
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".hif", ".avif", ".tif", ".tiff", ".png", ".webp",
    ];

    // MetadataExtractor 2.9.3 cannot detect Canon CRW (CIFF) or Sigma X3F (FOVb) containers.
    private static readonly string[] NativeUnsupported = [".crw", ".x3f"];

    public static readonly FrozenSet<string> ExifTool =
        CommonExtensions.Concat(RawExtensions).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Native =
        CommonExtensions.Concat(RawExtensions).Except(NativeUnsupported).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
