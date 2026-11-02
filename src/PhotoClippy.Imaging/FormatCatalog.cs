namespace PhotoClippy.Imaging;

internal static class FormatCatalog
{
    private static readonly HashSet<string> Raster = new(StringComparer.Ordinal)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "webp", "gif", "bmp", "dib", "ico", "wbmp",
    };

    private static readonly HashSet<string> Tiff = new(StringComparer.Ordinal) { "tif", "tiff" };

    private static readonly HashSet<string> TiffRaw = new(StringComparer.Ordinal)
    {
        "nef", "nrw", "arw", "srf", "sr2", "cr2", "dng", "orf", "pef", "rw2", "srw", "3fr", "erf", "kdc",
        "mef", "mos", "iiq", "raw", "rwl", "dcr", "fff", "gpr",
    };

    private static readonly HashSet<string> OtherRaw = new(StringComparer.Ordinal)
    {
        "raf", "cr3", "crw", "mrw", "x3f",
    };

    private static readonly HashSet<string> HeifFamily = new(StringComparer.Ordinal) { "heic", "heif", "avif", "hif" };

    private static readonly HashSet<string> Video = new(StringComparer.Ordinal)
    {
        "mp4", "mov", "avi", "mkv", "m4v", "mts", "m2ts", "3gp", "wmv", "webm", "mpg", "mpeg", "flv",
    };

    public static string Extension(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        if (ext.Length <= 1)
        {
            return string.Empty;
        }

        return ext[1..].ToString().ToLowerInvariant();
    }

    public static bool IsVideo(string extension) => Video.Contains(extension);

    public static bool IsRaw(string extension) => TiffRaw.Contains(extension) || OtherRaw.Contains(extension);

    public static bool IsTiff(string extension) => Tiff.Contains(extension);

    public static bool IsHeifFamily(string extension) => HeifFamily.Contains(extension);

    public static bool IsKnownImage(string extension) =>
        Raster.Contains(extension) || Tiff.Contains(extension) || IsRaw(extension) || HeifFamily.Contains(extension);

    public static bool IsSupportedExtension(string extension) =>
        Raster.Contains(extension) || Tiff.Contains(extension) || IsRaw(extension)
        || (HeifFamily.Contains(extension) && SkiaCapabilities.CanDecodeHeif);
}
