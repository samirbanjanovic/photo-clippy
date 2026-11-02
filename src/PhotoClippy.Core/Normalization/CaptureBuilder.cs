using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Normalization;

public sealed record BodyInfo
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public string? Make { get; init; }
    public double? CropFactor { get; init; }
    public string? Format { get; init; }
    /// <summary>"exif-35mm", "focal-plane" or null when unknown.</summary>
    public string? CropSource { get; init; }
    public DeviceClass Device { get; init; }
}

/// <summary>One capture with the files it was merged from, ordered by frame-decoding preference.</summary>
public sealed record CaptureRecord(CaptureFacts Facts, IReadOnlyList<SourceFile> FrameCandidates);

public sealed record CaptureSet
{
    public IReadOnlyList<CaptureRecord> Captures { get; init; } = [];
    public IReadOnlyDictionary<string, BodyInfo> Bodies { get; init; } = new Dictionary<string, BodyInfo>();
    /// <summary>Lens key → resolved spec (Source Exif or Observed). Unknown/unnamed lenses are absent.</summary>
    public IReadOnlyDictionary<string, LensSpec> LensSpecs { get; init; } = new Dictionary<string, LensSpec>();
    public int RawPlusJpeg { get; init; }
    public int MergedExports { get; init; }
    public int FilesWithMetadata { get; init; }
    public int MetadataErrors { get; init; }
}

/// <summary>Turns per-file metadata into captures: names, device class, crop factor, lens specs, RAW+JPEG merging.</summary>
public static partial class CaptureBuilder
{
    public const string UnknownLensPrefix = "unknown";
    public const string FixedLensPrefix = "fixed:";

    public static FileFacts ToFileFacts(SourceFile file, PhotoMetadata? metadata)
    {
        if (metadata is null)
        {
            return new FileFacts { File = file, MetadataError = true };
        }

        var make = CameraNames.NormalizeMake(metadata.Make);
        var display = CameraNames.BodyDisplay(metadata.Make, metadata.Model);
        var software = CameraNames.Clean(metadata.Software);
        var (width, height) = (metadata.Width, metadata.Height);
        if (metadata.Orientation is >= 5 and <= 8 && width is not null && height is not null)
        {
            (width, height) = (height, width);
        }

        var hasCameraData = make is not null || display is not null || metadata.FNumber is not null || metadata.CaptureLocal is not null;
        return new FileFacts
        {
            File = file,
            MetadataError = !hasCameraData && metadata.Warnings.Count > 0 && metadata.Width is null,
            Make = make,
            BodyKey = display is null ? null : CameraNames.Slug(display),
            BodyDisplay = display,
            BodySerial = CameraNames.Clean(metadata.BodySerial),
            Software = software,
            IsEdited = software is not null && EditorSoftware().IsMatch(software),
            LensDisplay = CameraNames.LensDisplay(metadata.LensModel),
            LensSpec = metadata.LensSpec is { MinFocalMm: > 0, MaxFocalMm: > 0 } spec && spec.MaxFocalMm >= spec.MinFocalMm ? spec : null,
            FocalMm = Positive(metadata.FocalLengthMm),
            Focal35Exif = Positive(metadata.FocalLength35mm),
            FNumber = metadata.FNumber is > 0.4 and < 256 ? metadata.FNumber : null,
            ExposureS = metadata.ExposureTimeS is > 0 and < 86400 ? metadata.ExposureTimeS : null,
            Iso = metadata.Iso is > 0 and < 10_000_000 ? metadata.Iso : null,
            ExposureBiasEv = metadata.ExposureBiasEv,
            ExposureProgram = metadata.ExposureProgram,
            Flash = metadata.FlashFired,
            CaptureLocal = metadata.CaptureLocal is { Year: > 1900 } t ? t : null,
            SubSec = CameraNames.Clean(metadata.SubSec),
            ImageUniqueId = CameraNames.Clean(metadata.ImageUniqueId) is { Length: >= 8 } uid && !AllZero().IsMatch(uid) ? uid : null,
            Width = width,
            Height = height,
            Orientation = metadata.Orientation,
            FocalPlaneXResolutionPerMm = Positive(metadata.FocalPlaneXResolutionPerMm),
            Rating = metadata.Rating,
            HasGps = metadata.HasGps,
        };
    }

    public static CaptureSet Build(IReadOnlyList<FileFacts> files)
    {
        var bodies = ResolveBodies(files);
        var groups = GroupFiles(files, out var rawPlusJpeg, out var mergedExports);
        var captures = new List<(CaptureFacts Facts, IReadOnlyList<SourceFile> Candidates)>(groups.Count);
        foreach (var group in groups)
        {
            captures.Add(CreateCapture(group, bodies));
        }

        var lensSpecs = ResolveLensSpecs(captures.Select(c => c.Facts), files);
        var records = new List<CaptureRecord>(captures.Count);
        foreach (var (facts, candidates) in captures)
        {
            var updated = facts;
            if (facts.LensKey is not null && lensSpecs.TryGetValue(facts.LensKey, out var spec))
            {
                updated = facts with
                {
                    LensIsZoom = spec.IsZoom,
                    LensMinFocalMm = spec.FocalMinMm,
                    LensMaxFocalMm = spec.FocalMaxMm,
                    LensMaxApertureWide = spec.MaxApertureWide,
                    LensMaxApertureTele = spec.MaxApertureTele,
                };
            }

            records.Add(new CaptureRecord(updated, candidates));
        }

        records.Sort((a, b) => Nullable.Compare(a.Facts.CaptureLocal, b.Facts.CaptureLocal) is var c && c != 0 ? c : string.CompareOrdinal(a.Facts.PrimaryPath, b.Facts.PrimaryPath));
        return new CaptureSet
        {
            Captures = records,
            Bodies = bodies,
            LensSpecs = lensSpecs,
            RawPlusJpeg = rawPlusJpeg,
            MergedExports = mergedExports,
            FilesWithMetadata = files.Count(f => f.BodyDisplay is not null || f.HasExposure || f.CaptureLocal is not null),
            MetadataErrors = files.Count(f => f.MetadataError),
        };
    }

    private static Dictionary<string, BodyInfo> ResolveBodies(IReadOnlyList<FileFacts> files)
    {
        var result = new Dictionary<string, BodyInfo>(StringComparer.Ordinal);
        foreach (var group in files.Where(f => f.BodyKey is not null).GroupBy(f => f.BodyKey!, StringComparer.Ordinal))
        {
            var ratios = new List<double>();
            var planeCrops = new List<double>();
            var hasExposure = false;
            var hasLens = false;
            string? software = null;
            foreach (var f in group)
            {
                hasExposure |= f.HasExposure;
                hasLens |= f.LensDisplay is not null || f.LensSpec is not null;
                software ??= f.Software;
                if (f is { FocalMm: > 0, Focal35Exif: > 0 })
                {
                    var ratio = f.Focal35Exif.Value / f.FocalMm.Value;
                    if (ratio is > 0.3 and < 12)
                    {
                        ratios.Add(ratio);
                    }
                }
                else if (f is { FocalPlaneXResolutionPerMm: > 0, Width: > 0, Height: > 0 })
                {
                    var sensorWidth = Math.Max(f.Width.Value, f.Height.Value) / f.FocalPlaneXResolutionPerMm.Value;
                    var crop = 36.0 / sensorWidth;
                    if (crop is > 0.4 and < 12)
                    {
                        planeCrops.Add(crop);
                    }
                }
            }

            double? cropFactor = null;
            string? cropSource = null;
            if (ratios.Count > 0)
            {
                cropFactor = Math.Round(Stats.Median(ratios), 2);
                cropSource = "exif-35mm";
            }
            else if (planeCrops.Count > 0)
            {
                cropFactor = Math.Round(Stats.Median(planeCrops), 2);
                cropSource = "focal-plane";
            }

            var first = group.First();
            var model = first.BodyDisplay!;
            var device = DeviceClassifier.Classify(first.Make, model, software, hasExposure, cropFactor, hasLens);
            if (cropFactor is null && device == DeviceClass.InterchangeableLens)
            {
                cropFactor = 1.0;
                cropSource = null;
            }

            result[group.Key] = new BodyInfo
            {
                Key = group.Key,
                Display = model,
                Make = first.Make,
                CropFactor = cropFactor,
                CropSource = cropSource,
                Format = cropFactor is { } c ? SensorFormats.FromCrop(c) : null,
                Device = device,
            };
        }

        return result;
    }

    /// <summary>
    /// RAW+JPEG pairs (same folder and stem) form one capture; captures of the same body taken at the same instant
    /// (same sub-second, same image unique id, or an edited export of an original) are merged as well.
    /// </summary>
    private static List<List<FileFacts>> GroupFiles(IReadOnlyList<FileFacts> files, out int rawPlusJpeg, out int mergedExports)
    {
        var parent = new int[files.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        var byStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i].File.Path;
            var stemKey = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            if (byStem.TryGetValue(stemKey, out var other) && SameInstant(files[i], files[other], toleranceSeconds: 2))
            {
                Union(i, other);
            }
            else
            {
                byStem[stemKey] = i;
            }
        }

        var stemGroups = CountMultiKindGroups(files, Find);
        rawPlusJpeg = stemGroups;

        var byInstant = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (f.BodyKey is null || f.CaptureLocal is null)
            {
                continue;
            }

            var key = $"{f.BodyKey}|{f.CaptureLocal.Value.Ticks}";
            if (!byInstant.TryGetValue(key, out var list))
            {
                byInstant[key] = list = [];
            }

            list.Add(i);
        }

        var merges = 0;
        foreach (var list in byInstant.Values)
        {
            if (list.Count < 2)
            {
                continue;
            }

            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    var fa = files[list[a]];
                    var fb = files[list[b]];
                    if (Find(list[a]) == Find(list[b]))
                    {
                        continue;
                    }

                    var sameSubSec = fa.SubSec is not null && string.Equals(fa.SubSec, fb.SubSec, StringComparison.Ordinal);
                    var sameUid = fa.ImageUniqueId is not null && string.Equals(fa.ImageUniqueId, fb.ImageUniqueId, StringComparison.OrdinalIgnoreCase);
                    var export = (fa.IsEdited != fb.IsEdited) && (fa.SubSec is null || fb.SubSec is null || sameSubSec);
                    if (sameSubSec || sameUid || export)
                    {
                        Union(list[a], list[b]);
                        merges++;
                    }
                }
            }
        }

        mergedExports = merges;
        var groups = new Dictionary<int, List<FileFacts>>();
        for (var i = 0; i < files.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                groups[root] = group = [];
            }

            group.Add(files[i]);
        }

        return [.. groups.Values];
    }

    private static int CountMultiKindGroups(IReadOnlyList<FileFacts> files, Func<int, int> find)
    {
        var kinds = new Dictionary<int, (bool Raw, bool Other)>();
        for (var i = 0; i < files.Count; i++)
        {
            var root = find(i);
            kinds.TryGetValue(root, out var k);
            kinds[root] = files[i].File.Kind == MediaKind.Raw ? (true, k.Other) : (k.Raw, true);
        }

        return kinds.Values.Count(k => k.Raw && k.Other);
    }

    private static bool SameInstant(FileFacts a, FileFacts b, double toleranceSeconds) =>
        a.CaptureLocal is null || b.CaptureLocal is null || Math.Abs((a.CaptureLocal.Value - b.CaptureLocal.Value).TotalSeconds) <= toleranceSeconds;

    private static (CaptureFacts Facts, IReadOnlyList<SourceFile> Candidates) CreateCapture(List<FileFacts> group, IReadOnlyDictionary<string, BodyInfo> bodies)
    {
        group.Sort((a, b) => FactsPriority(a).CompareTo(FactsPriority(b)) is var c && c != 0 ? c : string.CompareOrdinal(a.File.Path, b.File.Path));
        var p = group[0];
        T? Pick<T>(Func<FileFacts, T?> selector) where T : class
        {
            foreach (var f in group)
            {
                if (selector(f) is { } v)
                {
                    return v;
                }
            }

            return null;
        }

        T? PickValue<T>(Func<FileFacts, T?> selector) where T : struct
        {
            foreach (var f in group)
            {
                if (selector(f) is { } v)
                {
                    return v;
                }
            }

            return null;
        }

        var bodyKey = Pick(f => f.BodyKey);
        var body = bodyKey is not null && bodies.TryGetValue(bodyKey, out var b) ? b : null;
        var device = body?.Device ?? (group.Any(f => f.HasExposure) ? DeviceClass.Unknown : DeviceClass.Software);
        var focal = PickValue(f => f.FocalMm);
        var focal35 = PickValue(f => f.Focal35Exif) ?? (focal is not null && body?.CropFactor is { } crop ? Math.Round(focal.Value * crop, 1) : null);
        var fNumber = PickValue(f => f.FNumber);
        var exposure = PickValue(f => f.ExposureS);
        var iso = PickValue(f => f.Iso);
        double? ev100 = fNumber is not null && exposure is not null && iso is not null
            ? Math.Round(Math.Log2(fNumber.Value * fNumber.Value / exposure.Value) - Math.Log2(iso.Value / 100.0), 2)
            : null;

        var lensDisplay = Pick(f => f.LensDisplay);
        var lensSpec = Pick(f => f.LensSpec);
        string? lensKey;
        if (lensDisplay is not null)
        {
            lensKey = device is DeviceClass.Phone or DeviceClass.FixedLens or DeviceClass.Drone
                ? FixedLensPrefix + CameraNames.Slug($"{bodyKey} {lensDisplay}")
                : CameraNames.Slug(lensDisplay);
        }
        else if (device is DeviceClass.Phone or DeviceClass.FixedLens or DeviceClass.Drone && bodyKey is not null)
        {
            lensKey = FixedLensPrefix + bodyKey;
            lensDisplay = $"{body?.Display ?? bodyKey} (built-in)";
        }
        else if (device == DeviceClass.InterchangeableLens && (focal is not null || lensSpec is not null))
        {
            if (lensSpec is not null)
            {
                var shorthand = LensSpec.Shorthand(lensSpec.MinFocalMm, lensSpec.MaxFocalMm, lensSpec.MaxApertureAtMinFocal ?? 0, lensSpec.MaxApertureAtMaxFocal ?? lensSpec.MaxApertureAtMinFocal ?? 0);
                lensKey = $"{UnknownLensPrefix}:{shorthand}";
                lensDisplay = $"Unnamed lens {FormatSpec(lensSpec)}";
            }
            else
            {
                lensKey = UnknownLensPrefix;
                lensDisplay = "Unknown lens";
            }
        }
        else
        {
            lensKey = null;
        }

        var paths = group.Select(f => f.File.Path).ToArray();
        var candidates = group.Select(f => f.File).OrderBy(FramePriority).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var facts = new CaptureFacts
        {
            CaptureId = StableId(p.File.Path),
            PrimaryPath = p.File.Path,
            Paths = paths,
            Kinds = [.. group.Select(f => f.File.Kind).Distinct()],
            CaptureLocal = PickValue(f => f.CaptureLocal),
            Device = device,
            BodyKey = bodyKey,
            BodyDisplay = body?.Display ?? Pick(f => f.BodyDisplay),
            Make = body?.Make ?? Pick(f => f.Make),
            SensorFormat = body?.Format,
            CropFactor = body?.CropFactor,
            LensKey = lensKey,
            LensDisplay = lensDisplay,
            FocalMm = focal,
            Focal35Mm = focal35,
            FNumber = fNumber,
            ExposureS = exposure,
            Iso = iso,
            Ev100 = ev100,
            ExposureBiasEv = PickValue(f => f.ExposureBiasEv),
            Flash = PickValue(f => f.Flash),
            ExposureProgram = Pick(f => f.ExposureProgram),
            Width = PickValue(f => f.Width),
            Height = PickValue(f => f.Height),
            Rating = group.Max(f => f.Rating),
            HasGps = group.Any(f => f.HasGps),
            Software = Pick(f => f.Software),
        };
        return (facts, candidates);
    }

    /// <summary>Lens specs from EXIF LensSpecification (most common) or from observed focal range / minimum f-number.</summary>
    private static Dictionary<string, LensSpec> ResolveLensSpecs(IEnumerable<CaptureFacts> captures, IReadOnlyList<FileFacts> files)
    {
        var byDisplay = files.Where(f => f.LensSpec is not null && f.LensDisplay is not null).GroupBy(f => f.LensDisplay!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.GroupBy(f => f.LensSpec!).OrderByDescending(x => x.Count()).First().Key, StringComparer.Ordinal);

        var result = new Dictionary<string, LensSpec>(StringComparer.Ordinal);
        foreach (var group in captures.Where(c => c.LensKey is not null && c.LensKey != UnknownLensPrefix).GroupBy(c => c.LensKey!, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var display = list[0].LensDisplay ?? group.Key;
            var observed = list.Where(c => c.FocalMm is > 0).ToList();
            byDisplay.TryGetValue(display, out var exif);
            if (exif is null && group.Key.StartsWith(UnknownLensPrefix + ":", StringComparison.Ordinal))
            {
                exif = files.FirstOrDefault(f => f.LensDisplay is null && f.LensSpec is not null &&
                    $"{UnknownLensPrefix}:{LensSpec.Shorthand(f.LensSpec.MinFocalMm, f.LensSpec.MaxFocalMm, f.LensSpec.MaxApertureAtMinFocal ?? 0, f.LensSpec.MaxApertureAtMaxFocal ?? f.LensSpec.MaxApertureAtMinFocal ?? 0)}" == group.Key)?.LensSpec;
            }

            double minFocal, maxFocal;
            LensSpecSource source;
            if (exif is not null)
            {
                (minFocal, maxFocal, source) = (exif.MinFocalMm, exif.MaxFocalMm, LensSpecSource.Exif);
            }
            else if (observed.Count > 0)
            {
                (minFocal, maxFocal, source) = (observed.Min(c => c.FocalMm!.Value), observed.Max(c => c.FocalMm!.Value), LensSpecSource.Observed);
            }
            else
            {
                continue;
            }

            var isZoom = maxFocal > minFocal + 0.5;
            var withF = observed.Where(c => c.FNumber is > 0).ToList();
            double? ObservedMaxAperture(Func<double, bool> nearFocal)
            {
                var near = withF.Where(c => nearFocal(c.FocalMm!.Value)).Select(c => c.FNumber!.Value).ToList();
                return near.Count > 0 ? near.Min() : null;
            }

            var overall = withF.Count > 0 ? withF.Min(c => c.FNumber!.Value) : (double?)null;
            var wide = exif?.MaxApertureAtMinFocal ?? (isZoom ? ObservedMaxAperture(f => f <= minFocal * 1.1 + 0.5) : overall) ?? overall;
            var tele = exif?.MaxApertureAtMaxFocal ?? (isZoom ? ObservedMaxAperture(f => f >= maxFocal / 1.1 - 0.5) : wide) ?? wide;
            if (wide is null)
            {
                continue;
            }

            tele ??= wide;
            if (tele < wide)
            {
                tele = wide;
            }

            result[group.Key] = new LensSpec
            {
                Id = LensSpec.Shorthand(minFocal, maxFocal, wide.Value, tele.Value),
                Name = display,
                FocalMinMm = minFocal,
                FocalMaxMm = isZoom ? maxFocal : minFocal,
                MaxApertureWide = wide.Value,
                MaxApertureTele = tele.Value,
                Source = source,
            };
        }

        return result;
    }

    private static int FactsPriority(FileFacts f) => (f.File.Kind, f.IsEdited) switch
    {
        (MediaKind.Raw, _) => 0,
        (_, false) when f.BodyKey is not null => 1,
        (_, false) => 2,
        _ => 3,
    };

    private static int FramePriority(SourceFile f) => f.Kind switch
    {
        MediaKind.Jpeg => 0,
        MediaKind.Png => 1,
        MediaKind.Other => 2,
        MediaKind.Raw => 3,
        MediaKind.Tiff => 4,
        MediaKind.Heif => 5,
        _ => 6,
    };

    private static string FormatSpec(LensSpecification spec)
    {
        var focal = spec.MaxFocalMm > spec.MinFocalMm + 0.5
            ? string.Create(CultureInfo.InvariantCulture, $"{spec.MinFocalMm:0.#}-{spec.MaxFocalMm:0.#}mm")
            : string.Create(CultureInfo.InvariantCulture, $"{spec.MinFocalMm:0.#}mm");
        return spec.MaxApertureAtMinFocal is { } a ? string.Create(CultureInfo.InvariantCulture, $"{focal} f/{a:0.#}") : focal;
    }

    public static string StableId(string path)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(path), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static double? Positive(double? value) => value is > 0 and < 100_000 ? value : null;

    [GeneratedRegex(@"lightroom|photoshop|capture one|darktable|dxo|luminar|affinity|gimp|rawtherapee|\bon1\b|pixelmator|snapseed|vsco|acdsee|picasa|topaz|exposure x|aurora hdr|skylum|polarr|silkypix|photolab|photo ?raw|iridient|lightzone|digikam|shotwell|google photos|instagram|apple photos|^photos \d", RegexOptions.IgnoreCase)]
    private static partial Regex EditorSoftware();

    [GeneratedRegex("^0+$")]
    private static partial Regex AllZero();
}
