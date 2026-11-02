using System.Collections.Frozen;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Discovery;

public static class MediaTypes
{
    private static readonly FrozenDictionary<string, MediaKind> Kinds = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Jpeg,
        [".jpeg"] = MediaKind.Jpeg,
        [".jpe"] = MediaKind.Jpeg,
        [".heic"] = MediaKind.Heif,
        [".heif"] = MediaKind.Heif,
        [".hif"] = MediaKind.Heif,
        [".avif"] = MediaKind.Heif,
        [".tif"] = MediaKind.Tiff,
        [".tiff"] = MediaKind.Tiff,
        [".png"] = MediaKind.Png,
        [".webp"] = MediaKind.Other,
        [".dng"] = MediaKind.Raw,
        [".nef"] = MediaKind.Raw,
        [".nrw"] = MediaKind.Raw,
        [".arw"] = MediaKind.Raw,
        [".srf"] = MediaKind.Raw,
        [".sr2"] = MediaKind.Raw,
        [".cr2"] = MediaKind.Raw,
        [".cr3"] = MediaKind.Raw,
        [".crw"] = MediaKind.Raw,
        [".raf"] = MediaKind.Raw,
        [".orf"] = MediaKind.Raw,
        [".ori"] = MediaKind.Raw,
        [".rw2"] = MediaKind.Raw,
        [".pef"] = MediaKind.Raw,
        [".srw"] = MediaKind.Raw,
        [".3fr"] = MediaKind.Raw,
        [".fff"] = MediaKind.Raw,
        [".erf"] = MediaKind.Raw,
        [".kdc"] = MediaKind.Raw,
        [".dcr"] = MediaKind.Raw,
        [".mef"] = MediaKind.Raw,
        [".mos"] = MediaKind.Raw,
        [".mrw"] = MediaKind.Raw,
        [".iiq"] = MediaKind.Raw,
        [".x3f"] = MediaKind.Raw,
        [".rwl"] = MediaKind.Raw,
        [".gpr"] = MediaKind.Raw,
        [".raw"] = MediaKind.Raw,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetKind(ReadOnlySpan<char> fileName, out MediaKind kind)
    {
        var dot = fileName.LastIndexOf('.');
        kind = MediaKind.Other;
        return dot >= 0 && Kinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(fileName[dot..], out kind);
    }

    public static bool IsSupported(string path) => TryGetKind(Path.GetFileName(path.AsSpan()), out _);

    public static IReadOnlyCollection<string> Extensions => Kinds.Keys;
}
