using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using MetadataExtractor.Formats.Xmp;
using PhotoClippy.Contracts;
using XmpCore;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>Values read from an embedded XMP packet or an XMP sidecar; used only to fill gaps.</summary>
internal sealed record XmpValues
{
    public int? Rating { get; init; }
    public string? LensModel { get; init; }
    public string? LensMake { get; init; }
    public LensSpecification? LensSpec { get; init; }
    public string? BodySerial { get; init; }
    public string? LensSerial { get; init; }
    public string? SourcePath { get; init; }

    public bool IsEmpty =>
        Rating is null && LensModel is null && LensMake is null && LensSpec is null && BodySerial is null
        && LensSerial is null;

    public static XmpValues? From(IXmpMeta? meta, string? sourcePath = null)
    {
        if (meta is null)
        {
            return null;
        }

        var values = new XmpValues
        {
            Rating = ExifMapping.ValidRating(ExifMapping.ParseNumber(Get(meta, XmpConstants.NsXmp, "Rating"))),
            LensModel = ExifMapping.UsefulLensName(Get(meta, XmpConstants.NsExifAux, "Lens"))
                        ?? ExifMapping.UsefulLensName(Get(meta, XmpConstants.NsExifx, "LensModel")),
            LensMake = ExifMapping.Clean(Get(meta, XmpConstants.NsExifx, "LensMake")),
            LensSpec = ExifMapping.ParseLensInfo(Get(meta, XmpConstants.NsExifAux, "LensInfo")),
            BodySerial = ExifMapping.CleanSerial(Get(meta, XmpConstants.NsExifAux, "SerialNumber"))
                         ?? ExifMapping.CleanSerial(Get(meta, XmpConstants.NsExifx, "BodySerialNumber")),
            LensSerial = ExifMapping.CleanSerial(Get(meta, XmpConstants.NsExifAux, "LensSerialNumber"))
                         ?? ExifMapping.CleanSerial(Get(meta, XmpConstants.NsExifx, "LensSerialNumber")),
            SourcePath = sourcePath,
        };
        return values.IsEmpty ? null : values;
    }

    /// <summary>Reads an XMP sidecar file; returns null when it has no relevant values.</summary>
    public static XmpValues? ReadSidecar(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // XmpCore throws InvalidCastException on XML comments in some positions (e.g. inside rdf:RDF);
        // comments carry no metadata, so drop them before parsing.
        if (bytes.AsSpan().IndexOf("<!--"u8) >= 0)
        {
            bytes = StripXmlComments(bytes);
        }

        var directory = new XmpReader().Extract(bytes);
        if (directory.XmpMeta is null)
        {
            var error = directory.Errors.Count > 0 ? directory.Errors[0] : "no XMP packet";
            throw new InvalidDataException(error);
        }

        return From(directory.XmpMeta, path);
    }

    internal static byte[] StripXmlComments(byte[] bytes)
    {
        var output = new byte[bytes.Length];
        var written = 0;
        var rest = bytes.AsSpan();
        while (true)
        {
            var start = rest.IndexOf("<!--"u8);
            if (start < 0)
            {
                rest.CopyTo(output.AsSpan(written));
                written += rest.Length;
                break;
            }

            rest[..start].CopyTo(output.AsSpan(written));
            written += start;
            var end = rest[(start + 4)..].IndexOf("-->"u8);
            if (end < 0)
            {
                break;
            }

            rest = rest[(start + 4 + end + 3)..];
        }

        return output.AsSpan(0, written).ToArray();
    }

    private static string? Get(IXmpMeta meta, string ns, string name)
    {
        try
        {
            return meta.GetPropertyString(ns, name);
        }
        catch (XmpException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Locates "&lt;file&gt;.xmp" (darktable) or "&lt;name&gt;.xmp" (Lightroom) sidecars. Directory listings are cached
/// for a few seconds so batches from the same folder share one enumeration (cheap on SMB). Thread-safe.
/// </summary>
internal sealed class SidecarLocator
{
    private const long TtlMs = 30_000;
    private const int MaxDirectories = 2048;

    private static readonly EnumerationOptions XmpEnumeration = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Directory,
    };

    private readonly ConcurrentDictionary<string, Listing> _cache = new(StringComparer.Ordinal);

    public string? Find(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        var names = GetListing(directory);
        if (names.Count == 0)
        {
            return null;
        }

        var fileName = Path.GetFileName(imagePath);
        if (names.TryGetValue(fileName + ".xmp", out var actual))
        {
            return Path.Combine(directory, actual);
        }

        var stem = Path.GetFileNameWithoutExtension(imagePath);
        return names.TryGetValue(stem + ".xmp", out actual) && !string.Equals(actual, fileName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, actual)
            : null;
    }

    private FrozenDictionary<string, string> GetListing(string directory)
    {
        var now = Environment.TickCount64;
        if (_cache.TryGetValue(directory, out var cached) && now - cached.LoadedAt < TtlMs)
        {
            return cached.Names;
        }

        FrozenDictionary<string, string> names;
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.xmp", XmpEnumeration))
            {
                var name = Path.GetFileName(file);
                map.TryAdd(name, name);
            }

            names = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            names = FrozenDictionary<string, string>.Empty;
        }

        if (_cache.Count >= MaxDirectories)
        {
            _cache.Clear();
        }

        _cache[directory] = new Listing(now, names);
        return names;
    }

    private sealed record Listing(long LoadedAt, FrozenDictionary<string, string> Names);
}

internal static class SidecarExtensions
{
    /// <summary>Applies an XMP sidecar (if any) to fill gaps in <paramref name="draft"/>.</summary>
    public static void ApplySidecar(this MetadataDraft draft, SidecarLocator locator, string imagePath)
    {
        string? sidecar = null;
        try
        {
            sidecar = locator.Find(imagePath);
            if (sidecar is null)
            {
                return;
            }

            if (XmpValues.ReadSidecar(sidecar) is { } values)
            {
                draft.FillFrom(values, "sidecar");
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // XmpCore throws assorted exception types on malformed packets (e.g. InvalidCastException on RDF comments);
            // a broken sidecar must never cost the image's own metadata.
            draft.Warn(string.Create(CultureInfo.InvariantCulture,
                $"XMP sidecar '{Path.GetFileName(sidecar)}' unreadable: {ex.Message}"));
        }
    }
}
