using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Composition;

/// <summary>Everything a genre-prior rule may look at: capture facts (nullable) plus pixel features.</summary>
internal sealed record PriorSignals
{
    public DeviceClass Device { get; init; }

    /// <summary>The image comes from a camera exposure (camera device class or exposure metadata present).</summary>
    public bool IsCamera { get; init; }

    public double? Focal35 { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureS { get; init; }
    public int? Iso { get; init; }
    public double? Ev100 { get; init; }
    public bool? Flash { get; init; }
    public bool MacroLens { get; init; }
    public bool PortraitOrientation { get; init; }
    public required PixelFeatures Px { get; init; }

    /// <summary>Focal 50–135 mm eq. at f/2.8 or faster.</summary>
    public bool PortraitLens => Focal35 >= 50 && Focal35 <= 135 && FNumber <= 2.8;

    /// <summary>Long, dark exposure at a wide focal length (typical star-field settings).</summary>
    public bool AstroExposure => Ev100 < 1 && ExposureS >= 5 && Focal35 <= 35;

    /// <summary>
    /// Subject placed the way people are usually framed: near the centre, on a thirds point, or horizontally
    /// central in the upper 60 % of the frame (head-and-shoulders framing).
    /// </summary>
    public bool WellPlacedSubject =>
        Px.CenterScore >= 0.5 || Px.ThirdsScore >= 0.5 || (Px.SubjectX is >= 0.38 and <= 0.62 && Px.SubjectY <= 0.6);

    public static PriorSignals From(CaptureFacts? facts, PixelFeatures px, Frame frame)
    {
        DeviceClass device = facts?.Device ?? DeviceClass.Unknown;
        bool cameraDevice = device is DeviceClass.InterchangeableLens or DeviceClass.FixedLens or DeviceClass.Phone or DeviceClass.Drone;
        bool exposureData = facts is not null
            && (facts.Make is not null || facts.Focal35Mm is not null || facts.FNumber is not null || facts.ExposureS is not null);
        return new PriorSignals
        {
            Device = device,
            IsCamera = cameraDevice || (device == DeviceClass.Unknown && exposureData),
            Focal35 = facts?.Focal35Mm,
            FNumber = facts?.FNumber,
            ExposureS = facts?.ExposureS,
            Iso = facts?.Iso,
            Ev100 = facts?.Ev100,
            Flash = facts?.Flash,
            MacroLens = ContainsMacro(facts?.LensDisplay) || ContainsMacro(facts?.LensKey),
            PortraitOrientation = frame.Height > frame.Width * 1.05,
            Px = px,
        };
    }

    private static bool ContainsMacro(string? lens) =>
        lens is not null && (lens.Contains("macro", StringComparison.OrdinalIgnoreCase) || lens.Contains("micro", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One genre contribution of a rule.</summary>
internal readonly record struct PriorEmit(string Genre, double Weight);

/// <summary>A readable "when … then add …" prior rule. Nullable comparisons are false when the fact is missing.</summary>
internal sealed record PriorRule(string Description, Func<PriorSignals, bool> When, params PriorEmit[] Emits);

/// <summary>
/// Low-confidence genre priors. These are fused by the host with a CLIP analyzer at a small weight, so the table
/// favours calibration over coverage: every weight is a rough probability bump, each genre is capped at
/// <see cref="MaxPerGenre"/> and the total at <see cref="MaxTotal"/> (the remainder is implicit uncertainty).
/// </summary>
internal sealed class GenrePriorModel
{
    public const double MaxPerGenre = 0.45;
    public const double MaxTotal = 0.6;

    public static readonly IReadOnlyList<PriorRule> Rules =
    [
        // Junk: screenshots and documents.
        new("Software-generated image", s => s.Device == DeviceClass.Software,
            E("screenshots", 0.30), E("documents", 0.08)),
        new("Software image with large flat UI areas", s => s.Device == DeviceClass.Software && s.Px.FlatShare >= 0.3,
            E("screenshots", 0.15)),
        new("No camera data, flat and colourless", s => !s.IsCamera && s.Device != DeviceClass.Software && s.Px.FlatShare >= 0.4 && s.Px.MeanChroma < 0.04,
            E("screenshots", 0.25), E("documents", 0.08)),
        new("No camera data, white page with text lines", s => !s.IsCamera && s.Px.WhiteShare >= 0.5 && s.Px.MeanChroma < 0.03 && s.Px.HorizontalEdges >= 0.45,
            E("documents", 0.20), E("screenshots", 0.10)),
        new("Scanner", s => s.Device == DeviceClass.Scanner,
            E("documents", 0.35)),
        new("Camera photo of a page", s => s.IsCamera && s.Px.WhiteShare >= 0.5 && s.Px.MeanChroma < 0.03 && s.Px.HorizontalEdges >= 0.45 && s.Px.FlatShare < 0.4,
            E("documents", 0.20)),

        // Light level.
        new("Long dark exposure at a wide focal length (star field)", s => s.AstroExposure,
            E("astro", 0.35), E("night", 0.10)),
        new("Very long exposure of a dark frame", s => s.ExposureS >= 8 && s.Px.MeanLightness < 0.25,
            E("astro", 0.15)),
        new("Low light (EV100 < 4)", s => s.Ev100 < 4 && !s.AstroExposure,
            E("night", 0.25)),
        new("Camera without exposure data, very dark frame", s => s.IsCamera && s.Ev100 is null && s.Px.DarkShare >= 0.6,
            E("night", 0.10)),

        // Reach.
        new("Super-telephoto (>= 300 mm eq.)", s => s.Focal35 >= 300,
            E("wildlife", 0.24), E("birds", 0.20), E("sports", 0.05)),
        new("Telephoto (135-300 mm eq.)", s => s.Focal35 > 135 && s.Focal35 < 300,
            E("wildlife", 0.10), E("sports", 0.08), E("birds", 0.05)),
        new("Long lens with an action shutter speed", s => s.Focal35 >= 200 && s.ExposureS <= 0.001,
            E("sports", 0.08), E("birds", 0.05)),
        new("Dim stage light: long lens, high ISO, no flash", s => s.Ev100 < 7 && s.Focal35 >= 70 && s.Iso >= 3200 && s.Flash != true,
            E("performance", 0.20)),

        // People.
        new("Portrait lens wide open (50-135 mm eq., f/2.8 or faster)", s => s.PortraitLens,
            E("portrait", 0.14)),
        new("Portrait lens with an isolated, well-placed subject", s => s.PortraitLens && s.Px.SeparationScore >= 0.5 && s.WellPlacedSubject,
            E("portrait", 0.10)),
        new("Portrait lens in portrait orientation", s => s.PortraitLens && s.PortraitOrientation,
            E("portrait", 0.06)),
        new("Flash in dim light", s => s.Flash == true && s.Ev100 < 8,
            E("people", 0.10), E("interior", 0.04)),

        // Places and structures.
        new("Ultra-wide (<= 24 mm eq.) with a horizon", s => s.Focal35 <= 24 && s.Px.HasHorizon,
            E("landscape", 0.28)),
        new("Wide with a horizon under a bright sky", s => s.Focal35 <= 35 && s.Px.HasHorizon && s.Px.BrightSky >= 0.3,
            E("landscape", 0.15)),
        new("Camera horizon without focal data", s => s.IsCamera && s.Focal35 is null && s.Px.HasHorizon,
            E("landscape", 0.10)),
        new("Ultra-wide with strong verticals", s => s.Focal35 <= 24 && s.Px.VerticalEdges >= 0.38,
            E("architecture", 0.22), E("interior", 0.06)),
        new("Manhattan structure: verticals and horizontals, few diagonals, no horizon", s => s.IsCamera && !s.Px.HasHorizon && s.Px.VerticalEdges >= 0.35 && s.Px.HorizontalEdges >= 0.3 && s.Px.DiagonalEdges <= 0.25,
            E("architecture", 0.10)),
        new("Wide, indoor light level, no horizon", s => s.Focal35 <= 35 && s.Ev100 >= 4 && s.Ev100 < 8 && !s.Px.HasHorizon,
            E("interior", 0.15)),
        new("Drone", s => s.Device == DeviceClass.Drone,
            E("landscape", 0.20), E("cityscape", 0.12)),
        new("City lights: dark, wide, structured", s => s.Ev100 < 5 && s.Focal35 <= 50 && s.Px.DarkShare >= 0.4 && s.Px.VerticalEdges >= 0.35,
            E("cityscape", 0.10)),

        // Optics.
        new("Macro lens", s => s.MacroLens,
            E("macro", 0.30)),
    ];

    private readonly (Func<PriorSignals, bool> When, (int Genre, double Weight)[] Emits)[] _rules;
    private readonly string[] _genres;

    public GenrePriorModel(GenreTaxonomy taxonomy)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        var genres = new List<string>();
        var rules = new List<(Func<PriorSignals, bool>, (int, double)[])>();
        foreach (PriorRule rule in Rules)
        {
            var emits = new List<(int, double)>();
            foreach (PriorEmit emit in rule.Emits)
            {
                GenreDefinition? def = taxonomy.Find(emit.Genre);
                if (def is null)
                {
                    continue;
                }

                int index = genres.IndexOf(def.Id);
                if (index < 0)
                {
                    index = genres.Count;
                    genres.Add(def.Id);
                }

                emits.Add((index, emit.Weight));
            }

            if (emits.Count > 0)
            {
                rules.Add((rule.When, emits.ToArray()));
            }
        }

        _genres = genres.ToArray();
        _rules = rules.ToArray();
    }

    /// <summary>Taxonomy ids this model can emit (canonical casing).</summary>
    public IReadOnlyList<string> GenreIds => _genres;

    public IReadOnlyDictionary<string, double>? Evaluate(PriorSignals signals)
    {
        if (_genres.Length == 0)
        {
            return null;
        }

        Span<double> mass = _genres.Length <= 64 ? stackalloc double[_genres.Length] : new double[_genres.Length];
        mass.Clear();
        bool any = false;
        foreach ((Func<PriorSignals, bool> when, (int Genre, double Weight)[] emits) in _rules)
        {
            if (!when(signals))
            {
                continue;
            }

            any = true;
            foreach ((int genre, double weight) in emits)
            {
                mass[genre] += weight;
            }
        }

        if (!any)
        {
            return null;
        }

        double total = 0;
        for (int i = 0; i < mass.Length; i++)
        {
            mass[i] = Math.Min(mass[i], MaxPerGenre);
            total += mass[i];
        }

        double k = total > MaxTotal ? MaxTotal / total : 1.0;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < mass.Length; i++)
        {
            double v = Math.Floor(mass[i] * k * 10000.0) / 10000.0;
            if (v > 0)
            {
                result[_genres[i]] = v;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static PriorEmit E(string genre, double weight) => new(genre, weight);
}
