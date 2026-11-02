using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Profiling;

/// <summary>One capture after frame analysis and feature fusion.</summary>
public sealed record CaptureAnalysis
{
    public required CaptureFacts Facts { get; init; }
    /// <summary>Included in shooting statistics (scope and date filters passed).</summary>
    public bool InScope { get; init; }
    public CompositionFeatures? Composition { get; init; }
    public ColorFeatures? Color { get; init; }
    /// <summary>Fused top-1 genre id (null = unclassified).</summary>
    public string? Genre { get; init; }
    public double GenreConfidence { get; init; }
    public bool Junk { get; init; }
    public bool FrameAnalyzed => Composition is not null || Color is not null;
}

public sealed record ProfileBuilderOptions
{
    public int HighIsoThreshold { get; init; } = 3200;
    public double RecencyHalfLifeYears { get; init; }
    public IReadOnlyDictionary<string, LensSpec> LensSpecs { get; init; } = new Dictionary<string, LensSpec>();
    public IReadOnlyDictionary<string, Normalization.BodyInfo> Bodies { get; init; } = new Dictionary<string, Normalization.BodyInfo>();
    /// <summary>Body key or display substring that overrides the automatic target choice.</summary>
    public string? TargetBody { get; init; }
}

public sealed record ProfileResult
{
    public required ShootingProfile Profile { get; init; }
    public required IReadOnlyList<CaptureSample> Samples { get; init; }
    public required IReadOnlyList<TargetSystem> Systems { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<OwnedLens>> OwnedByBody { get; init; }
}

/// <summary>
/// Streaming accumulator that turns per-capture analyses into a <see cref="ShootingProfile"/>. Memory is
/// proportional to the number of captures only for compact numeric lists (percentiles) and samples.
/// </summary>
public sealed class ProfileBuilder(GenreTaxonomy taxonomy, ProfileBuilderOptions options)
{
    private const int MaxLensRows = 10;
    private const int MaxExtensionKeys = 32;

    private static readonly string[] WeekdayKeys = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] MonthKeys = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    /// <summary>
    /// "Within ⅓ stop of maximum aperture". Nominal ⅓-stop f-number steps differ by ratios of 1.09–1.15 and ⅔ stops by ≥ 1.26,
    /// so 1.2 separates them robustly despite EXIF rounding.
    /// </summary>
    private const double CeilingRatio = 1.2;

    private readonly BinnedAccumulator _focal35 = new(StandardBins.Focal);
    private readonly BinnedAccumulator _focalNative = new(StandardBins.Focal);
    private readonly BinnedAccumulator _aperture = new(StandardBins.Aperture);
    private readonly BinnedAccumulator _stopsFromMax = new(StandardBins.StopsFromMax);
    private readonly BinnedAccumulator _iso = new(StandardBins.Iso);
    private readonly BinnedAccumulator _shutter = new(StandardBins.Shutter, keepValues: false);
    private readonly BinnedAccumulator _ev = new(StandardBins.Ev100);
    private readonly BinnedAccumulator _bias = new(StandardBins.ExposureBias);
    private readonly BinnedAccumulator _hour = new(StandardBins.Hour, keepValues: false);
    private readonly CategoryAccumulator _program = new();
    private readonly CategoryAccumulator _flash = new();
    private readonly CategoryAccumulator _lens = new();
    private readonly CategoryAccumulator _body = new();
    private readonly CategoryAccumulator _device = new();
    private readonly CategoryAccumulator _genre = new();
    private readonly CategoryAccumulator _genreGroup = new();
    private readonly CategoryAccumulator _orientation = new();
    private readonly CategoryAccumulator _aspect = new();
    private readonly CategoryAccumulator _weekday = new();
    private readonly CategoryAccumulator _month = new();
    private readonly CategoryAccumulator _year = new();
    private readonly CategoryAccumulator _timeline = new();
    private readonly CategoryAccumulator _rating = new();

    private readonly CategoryAccumulator _colorKey = new();
    private readonly CategoryAccumulator _mono = new();
    private readonly BinnedAccumulator _colorfulness = new(StandardBins.Unit);
    private readonly BinnedAccumulator _contrast = new(StandardBins.Unit);
    private readonly BinnedAccumulator _warmth = new(StandardBins.Signed);
    private readonly BinnedAccumulator _saturation = new(StandardBins.Unit);
    private readonly BinnedAccumulator _sharpness = new(StandardBins.Unit);
    private readonly BinnedAccumulator _brightness = new(StandardBins.Unit);
    private readonly BinnedAccumulator _highlightClip = new(StandardBins.Unit);
    private readonly BinnedAccumulator _shadowClip = new(StandardBins.Unit);
    private readonly BinnedAccumulator _tone = new(BinScheme.Linear(0, 1, 16, "0.00"), keepValues: false);
    private readonly BinnedAccumulator _hue = new(BinScheme.Integers(0, 11, i => (i * 30).ToString(System.Globalization.CultureInfo.InvariantCulture), AxisScale.Categorical), keepValues: false);
    private readonly PaletteAccumulator _palette = new();

    private readonly BinnedAccumulator _thirds = new(StandardBins.Unit);
    private readonly BinnedAccumulator _center = new(StandardBins.Unit);
    private readonly BinnedAccumulator _symmetry = new(StandardBins.Unit);
    private readonly BinnedAccumulator _negativeSpace = new(StandardBins.Unit);
    private readonly BinnedAccumulator _separation = new(StandardBins.Unit);
    private readonly BinnedAccumulator _subjectArea = new(StandardBins.Unit);
    private readonly BinnedAccumulator _horizon = new(StandardBins.HorizonTilt);
    private readonly CategoryAccumulator _edges = new();

    private readonly CrossTab _focalByAperture = new();
    private readonly CrossTab _isoByShutter = new();
    private readonly CrossTab _genreByFocal = new();
    private readonly CrossTab _genreByAperture = new();
    private readonly CrossTab _lensByGenre = new();
    private readonly CrossTab _lensByYear = new();
    private readonly CrossTab _hourByWeekday = new();
    private readonly CrossTab _placement = new();

    private readonly FocalPeakFinder _peaks = new();
    private readonly ShareCounter _ceiling = new();
    private readonly ShareCounter _lightStarved = new();
    private readonly ShareCounter _highIso = new();
    private readonly ShareCounter _slow = new();
    private readonly ShareCounter _wideEnd = new();
    private readonly ShareCounter _teleEnd = new();
    private readonly ShareCounter _flashShare = new();
    private readonly ShareCounter _prime = new();
    private readonly List<double> _focalValues = [];
    private readonly List<double> _apertureValues = [];
    private readonly List<double> _isoValues = [];
    private readonly List<double> _evValues = [];
    private readonly List<double> _separationValues = [];
    private readonly List<double> _genreConfidence = [];

    private readonly Dictionary<string, LensAccumulator> _lenses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BodyAccumulator> _bodies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _extensions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _genreSources = new(StringComparer.Ordinal);
    private readonly List<PendingSample> _samples = [];

    private int _captures;
    private int _inScope;
    private int _framesAnalyzed;
    private int _junk;
    private DateTime? _first;
    private DateTime? _last;
    private int _missingFocal;
    private int _missingAperture;
    private int _missingIso;
    private int _missingShutter;
    private int _missingLens;
    private int _missingDate;
    private int _missingBody;

    /// <summary>Records which composition analyzers produced genre probabilities (for <see cref="DataQuality.GenreSource"/>).</summary>
    public void AddGenreSource(string source) => _genreSources.Add(source);

    public void Add(CaptureAnalysis analysis)
    {
        var f = analysis.Facts;
        _captures++;
        _device.Add(f.Device.ToString(), DeviceLabel(f.Device));
        if (f.CaptureLocal is { } any)
        {
            _first = _first is null || any < _first ? any : _first;
            _last = _last is null || any > _last ? any : _last;
        }

        if (analysis.Junk)
        {
            _junk++;
            return;
        }

        if (f.BodyKey is not null)
        {
            if (!_bodies.TryGetValue(f.BodyKey, out var body))
            {
                body = _bodies[f.BodyKey] = new BodyAccumulator(f.BodyKey, f.BodyDisplay ?? f.BodyKey, f.Make, f.SensorFormat, f.CropFactor, f.Device);
            }

            body.Add(f);
            _body.Add(f.BodyKey, f.BodyDisplay);
        }

        var lensSpec = f.LensKey is not null ? options.LensSpecs.GetValueOrDefault(f.LensKey) : null;
        var maxApAtFocal = lensSpec is not null && f.FocalMm is { } fm ? lensSpec.MaxApertureAt(fm) : (double?)null;
        bool? atCeiling = f.FNumber is { } n && maxApAtFocal is { } m ? n <= m * CeilingRatio : null;
        bool? slow = f.ExposureS is { } t && (f.Focal35Mm ?? f.FocalMm) is { } fl && fl > 0 ? t > 1.0 / fl : null;
        bool? highIso = f.Iso is { } iso ? iso >= options.HighIsoThreshold : null;
        bool? starved = atCeiling is { } c ? c && (highIso == true || slow == true) : null;
        var isZoom = lensSpec?.IsZoom ?? f.LensIsZoom;
        bool? atWide = isZoom == true && lensSpec is not null && f.FocalMm is { } fw ? fw <= lensSpec.FocalMinMm + EndTolerance(lensSpec.FocalMinMm) : null;
        bool? atTele = isZoom == true && lensSpec is not null && f.FocalMm is { } ft ? ft >= lensSpec.FocalMaxMm - EndTolerance(lensSpec.FocalMaxMm) : null;

        if (f.LensKey is not null)
        {
            if (!_lenses.TryGetValue(f.LensKey, out var lens))
            {
                lens = _lenses[f.LensKey] = new LensAccumulator(f, lensSpec);
            }

            lens.Add(f, analysis.InScope ? analysis.Genre : null, atCeiling, starved, atWide, atTele);
        }

        if (!analysis.InScope)
        {
            return;
        }

        _inScope++;
        AddExposure(f, atCeiling, starved, highIso, slow, isZoom, atWide, atTele, maxApAtFocal);
        AddTime(f);
        AddFrame(analysis);

        if (f.Focal35Mm is { } f35)
        {
            _samples.Add(new PendingSample(new CaptureSample
            {
                Focal35 = f35,
                FocalMm = f.FocalMm,
                FNumber = f.FNumber,
                LensMaxApertureAtFocal = maxApAtFocal,
                Iso = f.Iso,
                ExposureS = f.ExposureS,
                Ev100 = f.Ev100,
                Flash = f.Flash,
                LensKey = f.LensKey,
                LensIsZoom = isZoom == true,
                AtWideEnd = atWide == true,
                AtTeleEnd = atTele == true,
                Genre = analysis.Genre,
                GenreConfidence = analysis.GenreConfidence,
                Separation = analysis.Composition?.SeparationScore,
                When = f.CaptureLocal,
            }, f.Rating));
        }
    }

    private static double EndTolerance(double focal) => Math.Max(0.5, focal * 0.02);

    private void AddExposure(CaptureFacts f, bool? atCeiling, bool? starved, bool? highIso, bool? slow, bool? isZoom, bool? atWide, bool? atTele, double? maxAp)
    {
        _focal35.Add(f.Focal35Mm);
        _focalNative.Add(f.FocalMm);
        _aperture.Add(f.FNumber);
        _iso.Add(f.Iso);
        _shutter.Add(f.ExposureS);
        _ev.Add(f.Ev100);
        _bias.Add(f.ExposureBiasEv);
        _peaks.Add(f.Focal35Mm);
        if (f.FNumber is { } n && maxAp is { } m && m > 0)
        {
            _stopsFromMax.Add(Math.Max(0, 2 * Math.Log2(n / m)));
        }

        _program.Add(f.ExposureProgram);
        if (f.Flash is { } flash)
        {
            _flash.Add(flash ? "fired" : "off", flash ? "Fired" : "Did not fire");
        }

        if (f.LensKey is not null)
        {
            _lens.Add(f.LensKey, f.LensDisplay ?? f.LensKey);
        }

        if (f.Focal35Mm is { } f35)
        {
            _focalValues.Add(f35);
        }

        if (f.FNumber is { } fn)
        {
            _apertureValues.Add(fn);
        }

        if (f.Iso is { } iso)
        {
            _isoValues.Add(iso);
        }

        if (f.Ev100 is { } ev)
        {
            _evValues.Add(ev);
        }

        _ceiling.Add(atCeiling);
        _lightStarved.Add(starved);
        _highIso.Add(highIso);
        _slow.Add(slow);
        _wideEnd.Add(atWide);
        _teleEnd.Add(atTele);
        _flashShare.Add(f.Flash);
        _prime.Add(isZoom is { } z ? !z : null);

        if (f.Focal35Mm is { } x && f.FNumber is { } y)
        {
            _focalByAperture.Add(StandardBins.Aperture.Labels[StandardBins.Aperture.IndexOf(y)], StandardBins.Focal.Labels[StandardBins.Focal.IndexOf(x)]);
        }

        if (f.Iso is { } i && f.ExposureS is { } s)
        {
            _isoByShutter.Add(StandardBins.Iso.Labels[StandardBins.Iso.IndexOf(i)], StandardBins.Shutter.Labels[StandardBins.Shutter.IndexOf(s)]);
        }

        if (f.Focal35Mm is null) { _missingFocal++; }
        if (f.FNumber is null) { _missingAperture++; }
        if (f.Iso is null) { _missingIso++; }
        if (f.ExposureS is null) { _missingShutter++; }
        if (f.LensKey is null || f.LensKey.StartsWith(Normalization.CaptureBuilder.UnknownLensPrefix, StringComparison.Ordinal)) { _missingLens++; }
        if (f.CaptureLocal is null) { _missingDate++; }
        if (f.BodyKey is null) { _missingBody++; }

        if (f.Width is { } w && f.Height is { } h && w > 0 && h > 0)
        {
            var ratio = (double)Math.Max(w, h) / Math.Min(w, h);
            _orientation.Add(ratio < 1.03 ? "square" : w > h ? "landscape" : "portrait", ratio < 1.03 ? "Square" : w > h ? "Landscape" : "Portrait");
            var aspect = AspectLabel(ratio);
            _aspect.Add(aspect, aspect);
        }

        var rating = f.Rating is > 0 and <= 5 ? f.Rating.Value : 0;
        _rating.Add(rating.ToString(System.Globalization.CultureInfo.InvariantCulture), rating == 0 ? "Unrated" : new string('★', rating));
    }

    private void AddTime(CaptureFacts f)
    {
        if (f.CaptureLocal is not { } when)
        {
            return;
        }

        var weekday = WeekdayKeys[((int)when.DayOfWeek + 6) % 7];
        var hour = when.Hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        _hour.Add(when.Hour);
        _weekday.Add(weekday);
        _month.Add(MonthKeys[when.Month - 1]);
        _year.Add(when.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _timeline.Add(when.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        _hourByWeekday.Add(weekday, hour);
        if (f.LensKey is not null)
        {
            _lensByYear.Add(f.LensKey, when.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void AddFrame(CaptureAnalysis a)
    {
        var f = a.Facts;
        if (a.FrameAnalyzed)
        {
            _framesAnalyzed++;
        }

        if (a.Composition is { } c)
        {
            if (c.Genres is { Count: > 0 })
            {
                var key = a.Genre ?? "unclassified";
                var def = a.Genre is null ? null : taxonomy.Find(a.Genre);
                _genre.Add(key, def?.Name ?? "Unclassified");
                _genreGroup.Add(def?.Group ?? "Unclassified", def?.Group ?? "Unclassified");
                if (a.Genre is not null)
                {
                    _genreConfidence.Add(a.GenreConfidence);
                }

                if (f.Focal35Mm is { } f35)
                {
                    _genreByFocal.Add(key, StandardBins.Focal.Labels[StandardBins.Focal.IndexOf(f35)]);
                }

                if (f.FNumber is { } fn)
                {
                    _genreByAperture.Add(key, StandardBins.Aperture.Labels[StandardBins.Aperture.IndexOf(fn)]);
                }

                if (f.LensKey is not null)
                {
                    _lensByGenre.Add(f.LensKey, key);
                }
            }

            _thirds.Add(c.ThirdsScore);
            _center.Add(c.CenterScore);
            _symmetry.Add(c.SymmetryScore);
            _negativeSpace.Add(c.NegativeSpace);
            _separation.Add(c.SeparationScore);
            _subjectArea.Add(c.SubjectArea);
            _horizon.Add(c.HorizonTiltDeg);
            if (c.SeparationScore is { } sep)
            {
                _separationValues.Add(sep);
            }

            if (c.HorizontalEdges is { } he && c.VerticalEdges is { } ve && c.DiagonalEdges is { } de)
            {
                _edges.Add("horizontal", "Horizontal", he);
                _edges.Add("vertical", "Vertical", ve);
                _edges.Add("diagonal", "Diagonal", de);
            }

            if (c.SubjectX is { } sx && c.SubjectY is { } sy)
            {
                var col = Math.Clamp((int)(sx * 3), 0, 2);
                var row = Math.Clamp((int)(sy * 3), 0, 2);
                _placement.Add(PlacementRows[row], PlacementCols[col]);
            }

            AddExtras("composition", c.Extras);
        }

        if (a.Color is { } color)
        {
            _colorKey.Add(color.Key, color.Key switch { "low" => "Low key", "high" => "High key", "mid" => "Mid key", _ => color.Key });
            if (color.Monochrome is { } mono)
            {
                _mono.Add(mono ? "mono" : "color", mono ? "Monochrome" : "Colour");
            }

            _colorfulness.Add(color.Colorfulness);
            _contrast.Add(color.Contrast);
            _warmth.Add(color.Warmth);
            _saturation.Add(color.Saturation);
            _sharpness.Add(color.Sharpness);
            _brightness.Add(color.MeanLuma);
            _highlightClip.Add(color.HighlightClip);
            _shadowClip.Add(color.ShadowClip);
            if (color.LumaHistogram is { Count: > 0 } luma)
            {
                _tone.AddVector(luma);
            }

            if (color.HueHistogram is { Count: > 0 } hue && color.Monochrome != true)
            {
                _hue.AddVector(hue, color.MeanChroma ?? color.Colorfulness ?? 1);
            }

            _palette.Add(color.Palette);
            AddExtras("color", color.Extras);
        }
    }

    private static readonly string[] PlacementRows = ["Top", "Middle", "Bottom"];
    private static readonly string[] PlacementCols = ["Left", "Centre", "Right"];

    private void AddExtras(string component, IReadOnlyDictionary<string, double>? extras)
    {
        if (extras is null)
        {
            return;
        }

        foreach (var (name, value) in extras)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            var key = name.StartsWith(component + ".", StringComparison.Ordinal) ? name : $"{component}.{name}";
            if (!_extensions.TryGetValue(key, out var list))
            {
                if (_extensions.Count >= MaxExtensionKeys)
                {
                    continue;
                }

                list = _extensions[key] = [];
            }

            list.Add(value);
        }
    }

    private static string AspectLabel(double ratio)
    {
        (double R, string Label)[] known = [(1.0, "1:1"), (1.25, "5:4"), (4.0 / 3, "4:3"), (1.5, "3:2"), (16.0 / 9, "16:9"), (2.0, "2:1")];
        foreach (var (r, label) in known)
        {
            if (Math.Abs(ratio - r) / r < 0.025)
            {
                return label;
            }
        }

        return ratio > 2.2 ? "Panorama" : "Other";
    }

    private static string DeviceLabel(DeviceClass device) => device switch
    {
        DeviceClass.InterchangeableLens => "Interchangeable lens",
        DeviceClass.FixedLens => "Fixed lens",
        DeviceClass.Phone => "Phone",
        DeviceClass.Drone => "Drone",
        DeviceClass.Scanner => "Scanner",
        DeviceClass.Software => "Software / screenshots",
        _ => "Unknown",
    };

    public ProfileResult Build(LibraryInfo library)
    {
        var distributions = new Dictionary<string, Distribution>(StringComparer.Ordinal);
        void Put(Distribution? d)
        {
            if (d is not null)
            {
                distributions[d.Id] = d;
            }
        }

        Put(_focal35.Build(DistributionIds.Focal35, "Focal length (35 mm equivalent)", "mm"));
        Put(_focalNative.Build(DistributionIds.FocalNative, "Focal length (native)", "mm"));
        Put(_aperture.Build(DistributionIds.Aperture, "Aperture", "f"));
        Put(_stopsFromMax.Build(DistributionIds.StopsFromMax, "Stops from maximum aperture", "stops"));
        Put(_iso.Build(DistributionIds.Iso, "ISO", "ISO"));
        Put(_shutter.Build(DistributionIds.Shutter, "Shutter speed", "s"));
        Put(_ev.Build(DistributionIds.Ev100, "Scene brightness (EV100)", "EV"));
        Put(_bias.Build(DistributionIds.ExposureBias, "Exposure compensation", "EV"));
        Put(_program.Build(DistributionIds.ExposureProgram, "Exposure program"));
        Put(_flash.Build(DistributionIds.Flash, "Flash"));
        Put(_lens.Build(DistributionIds.Lens, "Lenses", maxBins: 12));
        Put(_body.Build(DistributionIds.Body, "Cameras", maxBins: 10));
        Put(_device.Build(DistributionIds.DeviceClass, "Device types"));
        Put(_genre.Build(DistributionIds.Genre, "Subjects"));
        Put(_genreGroup.Build(DistributionIds.GenreGroup, "Subject groups"));
        Put(_orientation.Build(DistributionIds.Orientation, "Orientation", ["landscape", "portrait", "square"]));
        Put(_aspect.Build(DistributionIds.AspectRatio, "Aspect ratio"));
        Put(_hour.Build(DistributionIds.Hour, "Hour of day", trim: false));
        Put(_weekday.Build(DistributionIds.Weekday, "Day of week", WeekdayKeys, scale: AxisScale.Ordinal, includeEmpty: true));
        Put(_month.Build(DistributionIds.Month, "Month", MonthKeys, scale: AxisScale.Ordinal, includeEmpty: true));
        Put(_year.Build(DistributionIds.Year, "Year", ContinuousYears(), scale: AxisScale.Ordinal, includeEmpty: true));
        Put(_timeline.Build(DistributionIds.Timeline, "Captures per month", ContinuousMonths(), scale: AxisScale.Ordinal, includeEmpty: true));
        if (_rating.Counts.Keys.Any(k => k != "0"))
        {
            Put(_rating.Build(DistributionIds.Rating, "Rating", ["0", "1", "2", "3", "4", "5"], scale: AxisScale.Ordinal));
        }

        Put(_colorKey.Build(DistributionIds.ColorKey, "Tonal key", ["low", "mid", "high"]));
        Put(_mono.Build(DistributionIds.Monochrome, "Colour vs monochrome", ["color", "mono"]));
        Put(_colorfulness.Build(DistributionIds.Colorfulness, "Colourfulness", trim: false));
        Put(_contrast.Build(DistributionIds.Contrast, "Contrast", trim: false));
        Put(_warmth.Build(DistributionIds.Warmth, "Warmth (cool ← → warm)", trim: false));
        Put(_saturation.Build(DistributionIds.Saturation, "Saturation", trim: false));
        Put(_sharpness.Build(DistributionIds.Sharpness, "Detail / sharpness", trim: false));
        Put(_brightness.Build(DistributionIds.Brightness, "Brightness", trim: false));
        Put(_tone.Build(DistributionIds.Tone, "Average tonal histogram", trim: false));
        Put(_hue.Build(DistributionIds.Hue, "Hue wheel (chroma-weighted)", "°", trim: false));
        Put(_highlightClip.Build(DistributionIds.HighlightClip, "Clipped highlights", trim: false));
        Put(_shadowClip.Build(DistributionIds.ShadowClip, "Crushed shadows", trim: false));

        Put(_thirds.Build(DistributionIds.Thirds, "Rule-of-thirds placement", trim: false));
        Put(_center.Build(DistributionIds.Center, "Centred subjects", trim: false));
        Put(_symmetry.Build(DistributionIds.Symmetry, "Symmetry", trim: false));
        Put(_negativeSpace.Build(DistributionIds.NegativeSpace, "Negative space", trim: false));
        Put(_separation.Build(DistributionIds.Separation, "Subject separation", trim: false));
        Put(_subjectArea.Build(DistributionIds.SubjectArea, "Subject size", trim: false));
        Put(_horizon.Build(DistributionIds.HorizonTilt, "Horizon tilt", "°", trim: false));
        Put(_edges.Build(DistributionIds.EdgeOrientation, "Line orientation", ["horizontal", "vertical", "diagonal"]));

        var matrices = BuildMatrices();
        var extensions = BuildExtensions();
        var bodies = BuildBodies();
        var lenses = BuildLenses();
        var (systems, ownedByBody) = BuildSystems(bodies);
        var target = systems.FirstOrDefault();
        _focalValues.Sort();
        double? focalSpread = _focalValues.Count >= 4 ? Math.Log2(Stats.PercentileSorted(_focalValues, 0.75) / Stats.PercentileSorted(_focalValues, 0.25)) : null;

        var profile = new ShootingProfile
        {
            Library = new LibrarySummary
            {
                Roots = library.Roots,
                FilesSeen = library.FilesSeen,
                FilesWithMetadata = library.FilesWithMetadata,
                Captures = _captures,
                CapturesInScope = _inScope,
                FramesAnalyzed = _framesAnalyzed,
                RawPlusJpeg = library.RawPlusJpeg,
                FirstCapture = _first,
                LastCapture = _last,
                Scope = library.Scope,
                FileKinds = library.FileKinds,
                DeviceClasses = _device.Counts.ToDictionary(kv => kv.Key, kv => (int)kv.Value),
                TotalBytes = library.TotalBytes,
            },
            Target = target,
            Bodies = bodies,
            Lenses = lenses,
            Distributions = distributions,
            Matrices = matrices,
            Palette = _palette.Build(),
            FocalPeaks = _peaks.Find(),
            Signals = new ShootingSignals
            {
                SampleCount = _inScope,
                ApertureCeilingShare = _ceiling.Share,
                LightStarvedShare = _lightStarved.Share,
                HighIsoShare = _highIso.Share,
                SlowShutterShare = _slow.Share,
                WideEndShare = _wideEnd.Share,
                TeleEndShare = _teleEnd.Share,
                FlashShare = _flashShare.Share,
                PrimeShare = _prime.Share,
                ZoomShare = _prime.Total > 0 ? 1 - _prime.Share : 0,
                MedianFocal35 = MedianOrNull(_focalValues),
                MedianFNumber = MedianOrNull(_apertureValues),
                MedianIso = MedianOrNull(_isoValues),
                MedianEv100 = MedianOrNull(_evValues),
                FocalSpreadStops = focalSpread,
                MedianSeparation = MedianOrNull(_separationValues),
            },
            Extensions = extensions,
            Quality = new DataQuality
            {
                MissingFocal = _missingFocal,
                MissingAperture = _missingAperture,
                MissingIso = _missingIso,
                MissingShutter = _missingShutter,
                MissingLens = _missingLens,
                MissingDate = _missingDate,
                MissingBody = _missingBody,
                MetadataErrors = library.MetadataErrors,
                DecodeFailed = library.DecodeFailed,
                NotDecodable = library.NotDecodable,
                JunkExcluded = _junk,
                GenreSource = _genreSources.Count == 0 ? "none" : string.Join('+', _genreSources.Order(StringComparer.Ordinal)),
                MeanGenreConfidence = _genreConfidence.Count > 0 ? _genreConfidence.Average() : null,
                Notes = library.Notes,
            },
        };

        return new ProfileResult
        {
            Profile = profile,
            Samples = BuildSamples(),
            Systems = systems,
            OwnedByBody = ownedByBody,
        };
    }

    private static double? MedianOrNull(List<double> values) => values.Count == 0 ? null : Stats.Median(values);

    private IReadOnlyList<string>? ContinuousYears()
    {
        var years = _year.Counts.Keys.Select(int.Parse).ToList();
        return years.Count == 0 ? null : [.. Enumerable.Range(years.Min(), years.Max() - years.Min() + 1).Select(y => y.ToString(System.Globalization.CultureInfo.InvariantCulture))];
    }

    private IReadOnlyList<string>? ContinuousMonths()
    {
        if (_timeline.Counts.Count == 0)
        {
            return null;
        }

        var keys = _timeline.Counts.Keys.Order(StringComparer.Ordinal).ToList();
        var start = DateTime.ParseExact(keys[0], "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(keys[^1], "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var result = new List<string>();
        for (var d = start; d <= end; d = d.AddMonths(1))
        {
            result.Add(d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result;
    }

    private Dictionary<string, Matrix> BuildMatrices()
    {
        var matrices = new Dictionary<string, Matrix>(StringComparer.Ordinal);
        void Put(Matrix? m)
        {
            if (m is not null)
            {
                matrices[m.Id] = m;
            }
        }

        var focalAxis = Axis(_focal35, StandardBins.Focal);
        var apertureAxis = Axis(_aperture, StandardBins.Aperture);
        var isoAxis = Axis(_iso, StandardBins.Iso);
        var shutterAxis = Axis(_shutter, StandardBins.Shutter);
        var genres = _genre.Keys();
        var genreName = (string k) => _genre.Label(k);
        var topLenses = _lens.Keys().Where(k => _lensByGenre.HasRow(k) || _lensByYear.HasRow(k)).Take(MaxLensRows).ToList();
        var lensName = (string k) => _lens.Label(k);

        Put(_focalByAperture.Build(MatrixIds.FocalByAperture, "Focal length × aperture", "Focal length (35 mm eq.)", "Aperture", apertureAxis, focalAxis));
        Put(_isoByShutter.Build(MatrixIds.IsoByShutter, "ISO × shutter speed", "Shutter speed", "ISO", isoAxis, shutterAxis));
        Put(_genreByFocal.Build(MatrixIds.GenreByFocal, "Subject × focal length", "Focal length (35 mm eq.)", "Subject", genres, focalAxis, genreName));
        Put(_genreByAperture.Build(MatrixIds.GenreByAperture, "Subject × aperture", "Aperture", "Subject", genres, apertureAxis, genreName));
        Put(_lensByGenre.Build(MatrixIds.LensByGenre, "Lens × subject", "Subject", "Lens", [.. topLenses.Where(_lensByGenre.HasRow)], genres, lensName, genreName));
        Put(_lensByYear.Build(MatrixIds.LensByYear, "Lens × year", "Year", "Lens", [.. topLenses.Where(_lensByYear.HasRow)], ContinuousYears() ?? [], lensName));
        Put(_hourByWeekday.Build(MatrixIds.HourByWeekday, "Hour × weekday", "Hour", "Weekday", WeekdayKeys, StandardBins.Hour.Labels));
        Put(_placement.Build(MatrixIds.Placement, "Subject placement", "Horizontal", "Vertical", PlacementRows, PlacementCols));
        return matrices;
    }

    private static IReadOnlyList<string> Axis(BinnedAccumulator accumulator, BinScheme scheme)
    {
        if (accumulator.Total <= 0)
        {
            return [];
        }

        var (first, last) = accumulator.TrimRange();
        return scheme.Labels[first..(last + 1)];
    }

    /// <summary>"composition.clip.top1Prob" → "Clip · top 1 prob": drops the component prefix and splits camelCase.</summary>
    internal static string HumanizeExtension(string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var words = parts.Skip(parts.Length > 1 ? 1 : 0).Select(SplitCamel).Where(w => w.Length > 0).ToList();
        if (words.Count == 0)
        {
            return key;
        }

        var text = string.Join(" · ", words);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string SplitCamel(string segment)
    {
        var sb = new System.Text.StringBuilder(segment.Length + 8);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            var boundary = i > 0 && ((char.IsUpper(c) && !char.IsUpper(segment[i - 1])) || (char.IsDigit(c) != char.IsDigit(segment[i - 1])));
            if (boundary || c is '_' or '-')
            {
                sb.Append(' ');
            }

            if (c is not '_' and not '-')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Trim();
    }

    private Dictionary<string, Distribution> BuildExtensions()
    {
        var result = new Dictionary<string, Distribution>(StringComparer.Ordinal);
        foreach (var (key, values) in _extensions)
        {
            if (values.Count == 0)
            {
                continue;
            }

            var min = values.Min();
            var max = values.Max();
            if (min >= 0 && max <= 1)
            {
                min = 0;
                max = 1;
            }
            else if (max - min < 1e-9)
            {
                max = min + 1;
            }

            var acc = new BinnedAccumulator(BinScheme.Linear(min, max, 10, Math.Abs(max - min) >= 10 ? "0" : "0.0#"));
            foreach (var v in values)
            {
                acc.Add(v);
            }

            var d = acc.Build(key, HumanizeExtension(key), trim: false);
            if (d is not null)
            {
                result[key] = d;
            }
        }

        return result;
    }

    private List<BodyUsage> BuildBodies()
    {
        var total = _bodies.Values.Sum(b => b.Captures);
        return [.. _bodies.Values
            .OrderByDescending(b => b.Captures)
            .Select(b =>
            {
                var info = options.Bodies.GetValueOrDefault(b.Key);
                return new BodyUsage
                {
                    Key = b.Key,
                    Display = info?.Display ?? b.Display,
                    Make = info?.Make ?? b.Make,
                    Format = info?.Format ?? b.Format,
                    CropFactor = info?.CropFactor ?? b.Crop,
                    Device = info?.Device ?? b.Device,
                    Captures = b.Captures,
                    Share = total > 0 ? (double)b.Captures / total : 0,
                    First = b.First,
                    Last = b.Last,
                };
            })];
    }

    private List<LensUsage> BuildLenses()
    {
        var total = _lenses.Values.Sum(l => l.Captures);
        return [.. _lenses.Values
            .OrderByDescending(l => l.Captures)
            .Select(l => l.ToUsage(total, taxonomy, _bodies))];
    }

    private (List<TargetSystem> Systems, Dictionary<string, IReadOnlyList<OwnedLens>> Owned) BuildSystems(List<BodyUsage> bodies)
    {
        var ilc = bodies.Where(b => b.Device == DeviceClass.InterchangeableLens).ToList();
        var lastIlc = ilc.Select(b => b.Last).Where(d => d is not null).Max();
        var windowStart = lastIlc?.AddMonths(-24);
        var recent = ilc
            .Select(b => (Body: b, Recent: _bodies[b.Key].CapturesSince(windowStart)))
            .OrderByDescending(x => x.Recent)
            .ThenByDescending(x => x.Body.Captures)
            .ToList();

        var systems = recent.Select((x, i) => new TargetSystem
        {
            BodyKey = x.Body.Key,
            BodyDisplay = x.Body.Display,
            Format = x.Body.Format ?? Normalization.SensorFormats.FromCrop(x.Body.CropFactor ?? 1),
            CropFactor = x.Body.CropFactor ?? 1,
            Reason = i == 0 ? $"Most used interchangeable-lens body in the 24 months before {lastIlc:yyyy-MM} ({x.Recent} captures)." : null,
        }).ToList();

        if (!string.IsNullOrWhiteSpace(options.TargetBody))
        {
            var match = MatchBody(systems, options.TargetBody);
            if (match is not null)
            {
                systems.Remove(match);
                systems.Insert(0, match with { Reason = $"Selected with --body \"{options.TargetBody}\"." });
            }
        }

        var owned = new Dictionary<string, IReadOnlyList<OwnedLens>>(StringComparer.Ordinal);
        foreach (var system in systems)
        {
            owned[system.BodyKey!] = OwnedFor(system.BodyKey!);
        }

        return (systems, owned);
    }

    public static TargetSystem? MatchBody(IEnumerable<TargetSystem> systems, string query)
    {
        var list = systems.ToList();
        return list.FirstOrDefault(s => string.Equals(s.BodyKey, query, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(s => s.BodyDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            ?? list.FirstOrDefault(s => s.BodyKey?.Contains(Normalization.CameraNames.Slug(query), StringComparison.OrdinalIgnoreCase) == true);
    }

    private List<OwnedLens> OwnedFor(string bodyKey) =>
    [
        .. _lenses.Values
            .Where(l => !l.IsFixed && !l.Key.StartsWith(Normalization.CaptureBuilder.UnknownLensPrefix, StringComparison.Ordinal) && l.Spec is not null && l.BodyCounts.ContainsKey(bodyKey))
            .OrderByDescending(l => l.BodyCounts[bodyKey])
            .Select(l => new OwnedLens
            {
                Lens = l.Spec! with { Id = l.Key, Name = l.Display },
                Captures = l.BodyCounts[bodyKey],
                LensKey = l.Key,
            }),
    ];

    private List<CaptureSample> BuildSamples()
    {
        var reference = _samples.Select(s => s.Sample.When).Where(w => w is not null).Max();
        var halfLife = options.RecencyHalfLifeYears;
        return [.. _samples.Select(p =>
        {
            var weight = 1.0;
            if (halfLife > 0 && reference is { } r && p.Sample.When is { } when)
            {
                var ageYears = Math.Max(0, (r - when).TotalDays / 365.25);
                weight = Math.Max(0.2, Math.Pow(0.5, ageYears / halfLife));
            }

            if (p.Rating >= 4)
            {
                weight *= 1.25;
            }

            return weight == 1 ? p.Sample : p.Sample with { Weight = weight };
        })];
    }

    private sealed record PendingSample(CaptureSample Sample, int? Rating);

    private sealed class BodyAccumulator(string key, string display, string? make, string? format, double? crop, DeviceClass device)
    {
        private readonly List<DateTime> _dates = [];

        public string Key => key;
        public string Display => display;
        public string? Make => make;
        public string? Format => format;
        public double? Crop => crop;
        public DeviceClass Device => device;
        public int Captures { get; private set; }
        public DateTime? First { get; private set; }
        public DateTime? Last { get; private set; }

        public void Add(CaptureFacts f)
        {
            Captures++;
            if (f.CaptureLocal is { } when)
            {
                _dates.Add(when);
                First = First is null || when < First ? when : First;
                Last = Last is null || when > Last ? when : Last;
            }
        }

        public int CapturesSince(DateTime? start) => start is null ? Captures : _dates.Count(d => d >= start);
    }

    private sealed class LensAccumulator(CaptureFacts first, LensSpec? spec)
    {
        private readonly List<double> _focal = [];
        private readonly List<double> _aperture = [];
        private readonly List<double> _iso = [];
        private readonly ShareCounter _wideOpen = new();
        private readonly ShareCounter _wideEnd = new();
        private readonly ShareCounter _teleEnd = new();
        private readonly ShareCounter _starved = new();
        private readonly Dictionary<string, int> _genres = new(StringComparer.Ordinal);

        public string Key { get; } = first.LensKey!;
        public string Display { get; } = first.LensDisplay ?? first.LensKey!;
        public string? Make { get; } = first.Make;
        public bool IsFixed { get; } = first.Device is DeviceClass.FixedLens or DeviceClass.Phone or DeviceClass.Drone;
        public LensSpec? Spec => spec;
        public double? Crop { get; private set; } = first.CropFactor;
        public bool? IsZoom { get; } = spec?.IsZoom ?? first.LensIsZoom;
        public int Captures { get; private set; }
        public Dictionary<string, int> BodyCounts { get; } = new(StringComparer.Ordinal);
        public DateTime? First { get; private set; }
        public DateTime? Last { get; private set; }

        public void Add(CaptureFacts f, string? genre, bool? atCeiling, bool? starved, bool? atWide, bool? atTele)
        {
            Captures++;
            Crop ??= f.CropFactor;
            if (f.BodyKey is not null)
            {
                BodyCounts[f.BodyKey] = BodyCounts.GetValueOrDefault(f.BodyKey) + 1;
            }

            if (f.Focal35Mm is { } f35)
            {
                _focal.Add(f35);
            }

            if (f.FNumber is { } n)
            {
                _aperture.Add(n);
            }

            if (f.Iso is { } iso)
            {
                _iso.Add(iso);
            }

            _wideOpen.Add(atCeiling);
            _starved.Add(starved);
            _wideEnd.Add(atWide);
            _teleEnd.Add(atTele);
            if (genre is not null)
            {
                _genres[genre] = _genres.GetValueOrDefault(genre) + 1;
            }

            if (f.CaptureLocal is { } when)
            {
                First = First is null || when < First ? when : First;
                Last = Last is null || when > Last ? when : Last;
            }
        }

        public LensUsage ToUsage(int total, GenreTaxonomy taxonomy, Dictionary<string, BodyAccumulator> bodies)
        {
            var crop = Crop ?? 1;
            var topGenre = _genres.Count == 0 ? null : _genres.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            return new LensUsage
            {
                Key = Key,
                Display = Display,
                Make = Make,
                IsZoom = IsZoom == true,
                IsFixed = IsFixed,
                FocalMinMm = spec?.FocalMinMm,
                FocalMaxMm = spec?.FocalMaxMm,
                FocalMin35 = spec is null ? null : Math.Round(spec.FocalMinMm * crop, 1),
                FocalMax35 = spec is null ? null : Math.Round(spec.FocalMaxMm * crop, 1),
                MaxApertureWide = spec?.MaxApertureWide,
                MaxApertureTele = spec?.MaxApertureTele,
                SpecSource = spec?.Source ?? LensSpecSource.Observed,
                Captures = Captures,
                Share = total > 0 ? (double)Captures / total : 0,
                MedianFocal35 = _focal.Count > 0 ? Stats.Median(_focal) : null,
                MedianFNumber = _aperture.Count > 0 ? Stats.Median(_aperture) : null,
                MedianIso = _iso.Count > 0 ? Stats.Median(_iso) : null,
                WideOpenShare = _wideOpen.Share,
                WideEndShare = _wideEnd.Share,
                TeleEndShare = _teleEnd.Share,
                LightStarvedShare = _starved.Share,
                TopGenre = topGenre is null ? null : taxonomy.Find(topGenre)?.Name ?? topGenre,
                Bodies = [.. BodyCounts.OrderByDescending(kv => kv.Value).Select(kv => bodies.TryGetValue(kv.Key, out var b) ? b.Display : kv.Key)],
                First = First,
                Last = Last,
            };
        }
    }
}

/// <summary>Library facts known to the pipeline but not to the profile accumulator.</summary>
public sealed record LibraryInfo
{
    public IReadOnlyList<string> Roots { get; init; } = [];
    public int FilesSeen { get; init; }
    public int FilesWithMetadata { get; init; }
    public int RawPlusJpeg { get; init; }
    public string Scope { get; init; } = "interchangeable";
    public IReadOnlyDictionary<string, int> FileKinds { get; init; } = new Dictionary<string, int>();
    public long TotalBytes { get; init; }
    public int MetadataErrors { get; init; }
    public int DecodeFailed { get; init; }
    public int NotDecodable { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}
