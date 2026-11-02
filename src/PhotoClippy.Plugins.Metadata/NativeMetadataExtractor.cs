using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;
using MetadataExtractor.Util;
using PhotoClippy.Contracts;
using MeDirectory = MetadataExtractor.Directory;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>
/// Metadata extractor built on MetadataExtractor. Only metadata segments are read (JPEG stops at SOS, TIFF-based
/// RAWs follow IFD offsets), so it is fast even on network shares. Stateless apart from a thread-safe sidecar
/// directory cache; safe to call concurrently.
/// </summary>
public sealed class NativeMetadataExtractor : IMetadataExtractor
{
    public const string PluginId = "builtin.metadata.native";
    private const string MappingVersion = "1";
    private const int BufferSize = 64 * 1024;

    private static readonly string LibraryVersion =
        typeof(ImageMetadataReader).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private readonly SidecarLocator _sidecars = new();

    public NativeMetadataExtractor()
    {
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Native metadata (MetadataExtractor)",
            Version = "1.0.0",
            Kind = ComponentKind.Metadata,
            Description = "Reads EXIF, maker-note lens names, embedded XMP and XMP sidecars with MetadataExtractor " + LibraryVersion + ".",
            Capabilities = ["exif", "xmp", "xmp-sidecar", "makernote-lens", "gps", "jpeg", "heif", "tiff", "png", "webp", "raw"],
            Fingerprint = ShortHash("native|" + MappingVersion + "|" + LibraryVersion),
        };
    }

    public PluginManifest Manifest { get; }

    public bool CanHandle(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return SupportedExtensions.Native.Contains(file.Extension);
    }

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PluginHealth.Ok("MetadataExtractor " + LibraryVersion));

    public Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PhotoMetadata>>([]);
        }

        return Task.Run(() => ExtractBatch(batch, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<PhotoMetadata> ExtractBatch(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken)
    {
        var results = new PhotoMetadata[batch.Count];
        for (var i = 0; i < results.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = ExtractFile(batch[i].Path);
        }

        return results;
    }

    internal PhotoMetadata ExtractFile(string path)
    {
        IReadOnlyList<MeDirectory> directories;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.SequentialScan);
            directories = ReadDirectories(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return MetadataDraft.Failed(path, PluginId, Describe(ex));
        }

        var draft = new MetadataDraft();
        try
        {
            Map(directories, draft);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            draft.Warn("metadata mapping failed: " + ex.Message);
        }

        draft.ApplySidecar(_sidecars, path);
        return draft.Build(path, PluginId);
    }

    private static IReadOnlyList<MeDirectory> ReadDirectories(FileStream stream, string fileName)
    {
        var fileType = FileTypeDetector.DetectFileType(stream, fileName);
        stream.Position = 0;
        if (fileType == FileType.Jpeg)
        {
            var directories = JpegMetadataReader.ReadMetadata(stream, [new JpegReader(), new ExifReader(), new SafeXmpReader()]);
            var list = new List<MeDirectory>(directories.Count + 1);
            list.AddRange(directories);
            list.Add(new FileTypeDirectory(FileType.Jpeg));
            return list;
        }

        if (fileType == FileType.Unknown)
        {
            throw new ImageProcessingException("file format could not be determined");
        }

        return ImageMetadataReader.ReadMetadata(stream, fileName);
    }

    /// <summary>
    /// <see cref="XmpReader"/> only catches <c>XmpException</c>; XmpCore throws other types on some malformed packets
    /// (e.g. comments inside rdf:RDF). Those must not discard the file's EXIF, so they become an error directory.
    /// </summary>
    private sealed class SafeXmpReader : IJpegSegmentMetadataReader
    {
        private readonly IJpegSegmentMetadataReader _inner = new XmpReader();

        public IReadOnlyCollection<JpegSegmentType> SegmentTypes => _inner.SegmentTypes;

        public IEnumerable<MeDirectory> ReadJpegSegments(IEnumerable<JpegSegment> segments)
        {
            try
            {
                return _inner.ReadJpegSegments(segments).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return [new ErrorDirectory("XMP unreadable: " + ex.Message)];
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "file not found",
        UnauthorizedAccessException => "access denied: " + ex.Message,
        ImageProcessingException => "unreadable or unsupported file: " + ex.Message,
        EndOfStreamException => "file is truncated: " + ex.Message,
        IOException => "read error: " + ex.Message,
        _ => "metadata read failed (" + ex.GetType().Name + "): " + ex.Message,
    };

    private static void Map(IReadOnlyList<MeDirectory> directories, MetadataDraft d)
    {
        var view = new ExifView(directories);

        d.Make = ExifMapping.Clean(view.Ifd0String(ExifDirectoryBase.TagMake));
        d.Model = ExifMapping.Clean(view.Ifd0String(ExifDirectoryBase.TagModel));
        d.Software = ExifMapping.Clean(view.Ifd0String(ExifDirectoryBase.TagSoftware));
        d.BodySerial = ExifMapping.CleanSerial(view.ExifString(ExifDirectoryBase.TagBodySerialNumber))
                       ?? ExifMapping.CleanSerial(view.Ifd0String(0xC62F));
        d.LensMake = ExifMapping.Clean(view.ExifString(ExifDirectoryBase.TagLensMake));
        d.LensModel = ExifMapping.UsefulLensName(view.ExifString(ExifDirectoryBase.TagLensModel));
        d.LensSerial = ExifMapping.CleanSerial(view.ExifString(ExifDirectoryBase.TagLensSerialNumber));
        d.LensSpec = LensSpecFrom(view.ExifObject(ExifDirectoryBase.TagLensSpecification));

        d.FocalLengthMm = ExifMapping.Positive(view.ExifNumber(ExifDirectoryBase.TagFocalLength));
        d.FocalLength35mm = ExifMapping.Positive(view.ExifNumber(ExifDirectoryBase.Tag35MMFilmEquivFocalLength));
        d.FNumber = ExifMapping.Positive(view.ExifNumber(ExifDirectoryBase.TagFNumber))
                    ?? ExifMapping.FNumberFromApex(view.ExifNumber(ExifDirectoryBase.TagAperture));
        d.ExposureTimeS = ExifMapping.Positive(view.ExifNumber(ExifDirectoryBase.TagExposureTime))
                          ?? ExifMapping.ExposureFromApex(view.ExifNumber(ExifDirectoryBase.TagShutterSpeed));
        d.ExposureBiasEv = view.ExifNumber(ExifDirectoryBase.TagExposureBias) is { } bias && double.IsFinite(bias) && Math.Abs(bias) <= 20
            ? bias
            : null;
        MapIso(view, d);

        if (view.ExifNumber(ExifDirectoryBase.TagExposureProgram) is { } program)
        {
            d.ExposureProgram = ExifMapping.ExposureProgramName((int)program);
        }

        if (view.ExifNumber(ExifDirectoryBase.TagMeteringMode) is { } metering)
        {
            d.MeteringMode = ExifMapping.MeteringModeName((int)metering);
        }

        if (view.ExifNumber(ExifDirectoryBase.TagWhiteBalanceMode) is { } wb)
        {
            d.WhiteBalance = ExifMapping.WhiteBalanceName((int)wb);
        }

        if (view.ExifNumber(ExifDirectoryBase.TagFlash) is { } flash && flash >= 0)
        {
            d.FlashFired = ((int)flash & 1) == 1;
        }

        MapDates(view, d);
        d.ImageUniqueId = ExifMapping.CleanSerial(view.ExifString(ExifDirectoryBase.TagImageUniqueId));
        d.Orientation = ExifMapping.ValidOrientation(view.Ifd0Number(ExifDirectoryBase.TagOrientation));
        MapDimensions(view, directories, d);

        d.FocalPlaneXResolutionPerMm = ExifMapping.FocalPlanePerMm(
            view.ExifNumber(ExifDirectoryBase.TagFocalPlaneXResolution),
            view.ExifNumber(ExifDirectoryBase.TagFocalPlaneResolutionUnit) is { } unit ? (int)unit : null);
        d.SubjectDistanceM = ExifMapping.SubjectDistance(view.ExifNumber(ExifDirectoryBase.TagSubjectDistance));
        d.Rating = ExifMapping.ValidRating(view.Ifd0Number(ExifDirectoryBase.TagRating));

        if (view.Gps is { } gps && gps.TryGetGeoLocation(out var location)
            && ExifMapping.ValidGps(location.Latitude, location.Longitude))
        {
            d.HasGps = true;
            d.Latitude = location.Latitude;
            d.Longitude = location.Longitude;
        }

        MakernoteFallbacks.Apply(directories, d);

        if (view.Xmp?.XmpMeta is { } xmpMeta && XmpValues.From(xmpMeta) is { } xmp)
        {
            d.FillFrom(xmp, "xmp");
        }

        if (view.FileType is { } fileType)
        {
            d.SetExtra("fileType", fileType);
        }

        foreach (var error in view.Errors)
        {
            d.Warn(error);
        }
    }

    private static void MapIso(ExifView view, MetadataDraft d)
    {
        var raw = view.ExifNumber(ExifDirectoryBase.TagIsoEquivalent);
        if (raw is { } r && r > 0 && r < 65535)
        {
            d.Iso = ExifMapping.ValidIso(r);
            return;
        }

        ReadOnlySpan<(int Tag, string Name)> fallbacks =
        [
            (ExifDirectoryBase.TagRecommendedExposureIndex, "recommendedExposureIndex"),
            (ExifDirectoryBase.TagIsoSpeed, "isoSpeed"),
            (ExifDirectoryBase.TagStandardOutputSensitivity, "standardOutputSensitivity"),
        ];
        foreach (var (tag, name) in fallbacks)
        {
            if (ExifMapping.ValidIso(view.ExifNumber(tag)) is { } iso)
            {
                d.Iso = iso;
                d.SetExtra("isoSource", name);
                return;
            }
        }

        if (MakernoteFallbacks.Iso(view.Directories) is { } makernoteIso)
        {
            d.Iso = makernoteIso;
            d.SetExtra("isoSource", "makernote");
            return;
        }

        if (raw is >= 65535)
        {
            // EXIF ISOSpeedRatings saturates at 65535; keep it as a lower bound when nothing better exists.
            d.Iso = 65535;
            d.SetExtra("isoSource", "saturated");
        }
    }

    private static void MapDates(ExifView view, MetadataDraft d)
    {
        ReadOnlySpan<(int Date, int Offset, int SubSec)> sources =
        [
            (ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagTimeZoneOriginal, ExifDirectoryBase.TagSubsecondTimeOriginal),
            (ExifDirectoryBase.TagDateTimeDigitized, ExifDirectoryBase.TagTimeZoneDigitized, ExifDirectoryBase.TagSubsecondTimeDigitized),
            (ExifDirectoryBase.TagDateTime, ExifDirectoryBase.TagTimeZone, ExifDirectoryBase.TagSubsecondTime),
        ];
        foreach (var (dateTag, offsetTag, subSecTag) in sources)
        {
            if (ExifMapping.ParseDateTime(view.ExifString(dateTag)) is { } date)
            {
                d.CaptureLocal = date;
                d.OffsetTime = ExifMapping.ParseOffset(view.ExifString(offsetTag));
                d.SubSec = ExifMapping.ParseSubSec(view.ExifString(subSecTag));
                if (dateTag != ExifDirectoryBase.TagDateTimeOriginal)
                {
                    d.SetExtra("captureSource", dateTag == ExifDirectoryBase.TagDateTime ? "dateTime" : "createDate");
                }

                return;
            }
        }
    }

    private static void MapDimensions(ExifView view, IReadOnlyList<MeDirectory> directories, MetadataDraft d)
    {
        var w = ExifMapping.ValidDimension(view.ExifNumber(ExifDirectoryBase.TagExifImageWidth));
        var h = ExifMapping.ValidDimension(view.ExifNumber(ExifDirectoryBase.TagExifImageHeight));
        if (w is not null && h is not null)
        {
            (d.Width, d.Height) = (w, h);
            return;
        }

        long bestArea = 0;
        foreach (var dir in directories)
        {
            (int Width, int Height)? size = dir switch
            {
                JpegDirectory j => Size(j, JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
                PngDirectory p => Size(p, PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
                WebPDirectory wp => Size(wp, WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight),
                HeicImagePropertiesDirectory hp => Size(hp, HeicImagePropertiesDirectory.TagImageWidth, HeicImagePropertiesDirectory.TagImageHeight),
                _ => null,
            };
            if (size is { } s && (long)s.Width * s.Height > bestArea)
            {
                bestArea = (long)s.Width * s.Height;
                (d.Width, d.Height) = (s.Width, s.Height);
            }
        }

        if (bestArea > 0)
        {
            return;
        }

        foreach (var dir in view.TiffImageDirectories)
        {
            var subfileType = ExifView.Number(dir, ExifDirectoryBase.TagNewSubfileType);
            if (subfileType is { } t && t != 0)
            {
                continue;
            }

            if (Size(dir, ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight) is { } s
                && (long)s.Width * s.Height > bestArea)
            {
                bestArea = (long)s.Width * s.Height;
                (d.Width, d.Height) = (s.Width, s.Height);
            }
        }
    }

    private static (int Width, int Height)? Size(MeDirectory dir, int widthTag, int heightTag) =>
        ExifMapping.ValidDimension(ExifView.Number(dir, widthTag)) is { } w
        && ExifMapping.ValidDimension(ExifView.Number(dir, heightTag)) is { } h
            ? (w, h)
            : null;

    internal static LensSpecification? LensSpecFrom(object? value)
    {
        if (value is Rational[] { Length: 4 } r)
        {
            return ExifMapping.LensSpec(ExifView.ToDouble(r[0]), ExifView.ToDouble(r[1]), ExifView.ToDouble(r[2]), ExifView.ToDouble(r[3]));
        }

        if (value is Array { Length: 4 } a)
        {
            return ExifMapping.LensSpec(ExifView.ToDouble(a.GetValue(0)), ExifView.ToDouble(a.GetValue(1)),
                ExifView.ToDouble(a.GetValue(2)), ExifView.ToDouble(a.GetValue(3)));
        }

        return null;
    }

    internal static string ShortHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}

/// <summary>Ordered view over the EXIF-style directories of one file.</summary>
internal sealed class ExifView
{
    private readonly List<MeDirectory> _exifFirst = new(4);
    private readonly List<MeDirectory> _ifd0First = new(4);
    private readonly List<string> _errors = [];

    public ExifView(IReadOnlyList<MeDirectory> directories)
    {
        Directories = directories;
        var subIfds = new List<MeDirectory>(2);
        var ifd0s = new List<MeDirectory>(1);
        var others = new List<MeDirectory>(2);
        foreach (var dir in directories)
        {
            switch (dir)
            {
                case ExifSubIfdDirectory:
                    subIfds.Add(dir);
                    break;
                case ExifIfd0Directory:
                    ifd0s.Add(dir);
                    break;
                case ExifImageDirectory or PanasonicRawIfd0Directory:
                    others.Add(dir);
                    break;
                case GpsDirectory gps:
                    Gps ??= gps;
                    break;
                case XmpDirectory xmp:
                    Xmp ??= xmp;
                    break;
                case FileTypeDirectory fileType:
                    FileType ??= fileType.GetString(FileTypeDirectory.TagDetectedFileTypeName);
                    break;
            }

            if (dir.HasError && dir is ExifDirectoryBase or XmpDirectory or ErrorDirectory && _errors.Count < 3)
            {
                _errors.Add(dir.Name + ": " + dir.Errors[0]);
            }
        }

        _exifFirst.AddRange(subIfds);
        _exifFirst.AddRange(ifd0s);
        _exifFirst.AddRange(others);
        _ifd0First.AddRange(ifd0s);
        _ifd0First.AddRange(subIfds);
        _ifd0First.AddRange(others);
    }

    public IReadOnlyList<MeDirectory> Directories { get; }
    public GpsDirectory? Gps { get; }
    public XmpDirectory? Xmp { get; }
    public string? FileType { get; }
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<MeDirectory> TiffImageDirectories => _ifd0First;

    public string? ExifString(int tag) => FindString(_exifFirst, tag);
    public string? Ifd0String(int tag) => FindString(_ifd0First, tag);
    public double? ExifNumber(int tag) => FindNumber(_exifFirst, tag);
    public double? Ifd0Number(int tag) => FindNumber(_ifd0First, tag);

    public object? ExifObject(int tag)
    {
        foreach (var dir in _exifFirst)
        {
            if (dir.GetObject(tag) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindString(List<MeDirectory> dirs, int tag)
    {
        foreach (var dir in dirs)
        {
            if (dir.ContainsTag(tag) && ExifMapping.Clean(dir.GetString(tag)) is { } s)
            {
                return s;
            }
        }

        return null;
    }

    private static double? FindNumber(List<MeDirectory> dirs, int tag)
    {
        foreach (var dir in dirs)
        {
            if (Number(dir, tag) is { } v)
            {
                return v;
            }
        }

        return null;
    }

    public static double? Number(MeDirectory dir, int tag) => ToDouble(dir.GetObject(tag));

    public static double? ToDouble(object? value) => value switch
    {
        null => null,
        Rational r => r.Denominator == 0 ? null : r.ToDouble(),
        double v => ExifMapping.Finite(v),
        float v => ExifMapping.Finite(v),
        int v => v,
        uint v => v,
        short v => v,
        ushort v => v,
        long v => v,
        ulong v => v,
        byte v => v,
        sbyte v => v,
        string s => ExifMapping.ParseNumber(s.Trim()),
        StringValue sv => ExifMapping.ParseNumber(sv.ToString(Encoding.ASCII).Trim('\0', ' ')),
        Array { Length: > 0 } a when a is not byte[] => ToDouble(a.GetValue(0)),
        byte[] { Length: > 0 } b => b[0],
        _ => null,
    };
}

/// <summary>Maker-note fallbacks for lens names, serials and ISO where MetadataExtractor decodes them.</summary>
internal static class MakernoteFallbacks
{
    public static void Apply(IReadOnlyList<MeDirectory> directories, MetadataDraft d)
    {
        foreach (var dir in directories)
        {
            switch (dir)
            {
                case CanonMakernoteDirectory canon:
                    FillLens(d, "makernote.canon",
                        canon.GetString(CanonMakernoteDirectory.TagLensModel),
                        canon.GetDescription(CanonMakernoteDirectory.CameraSettings.TagLensType));
                    if (d.BodySerial is null && ExifView.Number(canon, CanonMakernoteDirectory.TagCanonSerialNumber) is { } serial && serial > 0)
                    {
                        d.BodySerial = serial > 1e10
                            ? serial.ToString("F0", CultureInfo.InvariantCulture)
                            : ((long)serial).ToString("D10", CultureInfo.InvariantCulture);
                    }

                    break;
                case NikonType2MakernoteDirectory nikon:
                    FillLens(d, "makernote.nikon", nikon.GetDescription(NikonType2MakernoteDirectory.TagLens));
                    d.LensSpec ??= NativeMetadataExtractor.LensSpecFrom(nikon.GetObject(NikonType2MakernoteDirectory.TagLens));
                    if (ExifMapping.Clean(nikon.GetDescription(NikonType2MakernoteDirectory.TagLensType)) is { } lensType)
                    {
                        d.SetExtra("nikon.lensType", lensType);
                    }

                    FillSerial(d, nikon, NikonType2MakernoteDirectory.TagCameraSerialNumber, NikonType2MakernoteDirectory.TagCameraSerialNumber2);
                    break;
                case OlympusEquipmentMakernoteDirectory olympus:
                    FillLens(d, "makernote.olympus",
                        olympus.GetString(OlympusEquipmentMakernoteDirectory.TagLensModel),
                        olympus.GetDescription(OlympusEquipmentMakernoteDirectory.TagLensType));
                    FillSerial(d, olympus, OlympusEquipmentMakernoteDirectory.TagSerialNumber);
                    d.LensSerial ??= ExifMapping.CleanSerial(olympus.GetString(OlympusEquipmentMakernoteDirectory.TagLensSerialNumber));
                    break;
                case PanasonicMakernoteDirectory panasonic:
                    FillLens(d, "makernote.panasonic", panasonic.GetString(PanasonicMakernoteDirectory.TagLensType));
                    d.LensSerial ??= ExifMapping.CleanSerial(panasonic.GetString(PanasonicMakernoteDirectory.TagLensSerialNumber));
                    break;
                case SamsungType2MakernoteDirectory samsung:
                    FillLens(d, "makernote.samsung", samsung.GetDescription(SamsungType2MakernoteDirectory.TagLensType));
                    FillSerial(d, samsung, SamsungType2MakernoteDirectory.TagSerialNumber);
                    break;
                case LeicaMakernoteDirectory leica:
                    FillLens(d, "makernote.leica", leica.GetDescription(LeicaMakernoteDirectory.TagLensType));
                    FillSerial(d, leica, LeicaMakernoteDirectory.TagSerialNumber);
                    break;
                case LeicaType5MakernoteDirectory leica5:
                    FillLens(d, "makernote.leica", leica5.GetString(LeicaType5MakernoteDirectory.TagLensModel));
                    break;
                case SonyType1MakernoteDirectory sony:
                    FillLens(d, "makernote.sony", sony.GetDescription(SonyType1MakernoteDirectory.TagLensId));
                    break;
                case PentaxType2MakernoteDirectory pentax:
                    FillSerial(d, pentax, PentaxType2MakernoteDirectory.TagSerialNumber);
                    break;
                case FujifilmMakernoteDirectory fuji:
                    FillSerial(d, fuji, FujifilmMakernoteDirectory.TagSerialNumber);
                    break;
                case SigmaMakernoteDirectory sigma:
                    FillSerial(d, sigma, SigmaMakernoteDirectory.TagSerialNumber);
                    break;
            }
        }
    }

    public static int? Iso(IReadOnlyList<MeDirectory> directories)
    {
        foreach (var dir in directories)
        {
            var iso = dir switch
            {
                NikonType2MakernoteDirectory n => ExifMapping.IsoFromDescription(n.GetDescription(NikonType2MakernoteDirectory.TagIso1))
                                                  ?? ExifMapping.IsoFromDescription(n.GetDescription(NikonType2MakernoteDirectory.TagIsoRequested)),
                CanonMakernoteDirectory c => ExifMapping.IsoFromDescription(c.GetDescription(CanonMakernoteDirectory.CameraSettings.TagIso)),
                PanasonicMakernoteDirectory p => ExifView.Number(p, PanasonicMakernoteDirectory.TagProgramIso) is { } v && v < 65534
                    ? ExifMapping.ValidIso(v)
                    : null,
                PentaxMakernoteDirectory p => ExifMapping.IsoFromDescription(p.GetDescription(PentaxMakernoteDirectory.TagIsoSpeed)),
                PentaxType2MakernoteDirectory p => ExifMapping.IsoFromDescription(p.GetDescription(PentaxType2MakernoteDirectory.TagIso)),
                KodakMakernoteDirectory k => ExifMapping.ValidIso(ExifView.Number(k, KodakMakernoteDirectory.TagIso)),
                CasioType2MakernoteDirectory c => ExifMapping.ValidIso(ExifView.Number(c, CasioType2MakernoteDirectory.TagIsoSensitivity)),
                SamsungType2MakernoteDirectory s => ExifMapping.ValidIso(ExifView.Number(s, SamsungType2MakernoteDirectory.TagIso)),
                _ => null,
            };
            if (iso is not null)
            {
                return iso;
            }
        }

        return null;
    }

    private static void FillLens(MetadataDraft d, string source, params ReadOnlySpan<string?> candidates)
    {
        if (d.LensModel is not null)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (ExifMapping.UsefulLensName(candidate) is { } name)
            {
                d.LensModel = name;
                d.SetExtra("lensSource", source);
                return;
            }
        }
    }

    private static void FillSerial(MetadataDraft d, MeDirectory dir, params ReadOnlySpan<int> tags)
    {
        if (d.BodySerial is not null)
        {
            return;
        }

        foreach (var tag in tags)
        {
            if (ExifMapping.CleanSerial(dir.GetString(tag)) is { } serial)
            {
                d.BodySerial = serial;
                return;
            }
        }
    }
}
