using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Composition;

/// <summary>
/// <c>builtin.composition.heuristic</c>: model-free composition analysis (saliency subject, thirds/centre placement,
/// symmetry, negative space, subject separation, horizon, edge orientation) plus low-confidence genre priors from
/// capture facts. Stateless after construction and thread-safe.
/// </summary>
public sealed class HeuristicCompositionAnalyzer : ICompositionAnalyzer
{
    public const string PluginId = "builtin.composition.heuristic";

    /// <summary>Bump when an algorithm change alters outputs for the same settings (part of the fingerprint).</summary>
    public const int AlgorithmRevision = 1;

    /// <summary>Requested frame size; larger frames are fine and are area-averaged to <see cref="HeuristicCompositionSettings.WorkingEdge"/>.</summary>
    public const int DefaultMaxEdge = 384;

    private const string ExtrasPrefix = "composition.";

    private readonly int _workingEdge;
    private readonly GenrePriorModel? _priors;

    public HeuristicCompositionAnalyzer(HeuristicCompositionSettings? settings = null, GenreTaxonomy? taxonomy = null)
    {
        Settings = settings ?? new HeuristicCompositionSettings();
        _workingEdge = Math.Clamp(Settings.WorkingEdge, 32, 1024);
        _priors = Settings.EmitGenrePriors && taxonomy is not null ? new GenrePriorModel(taxonomy) : null;
        Requirements = new FrameRequirements(Math.Max(DefaultMaxEdge, _workingEdge));

        var capabilities = new List<string>
        {
            "subject", "thirds", "center", "symmetry", "negative-space", "separation", "horizon", "edge-orientation",
        };
        if (_priors is not null)
        {
            capabilities.Add("genre-priors");
        }

        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Heuristic composition",
            Version = "1.0.0",
            Kind = ComponentKind.Composition,
            Description = "Model-free composition analysis: frequency-tuned saliency subject, rule-of-thirds and centre placement, "
                + "symmetry, negative space, subject separation, Hough horizon, edge orientation and low-confidence genre priors.",
            Capabilities = capabilities,
            Fingerprint = ComputeFingerprint(Settings, taxonomy, _priors),
        };
    }

    public HeuristicCompositionSettings Settings { get; }

    public PluginManifest Manifest { get; }

    public FrameRequirements Requirements { get; }

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PluginHealth.Ok());

    public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        PixelFeatures px = CompositionEngine.Analyze(frame, _workingEdge);
        IReadOnlyDictionary<string, double>? genres = _priors?.Evaluate(PriorSignals.From(context.Facts, px, frame));
        return ValueTask.FromResult(ToContract(px, genres));
    }

    private static CompositionFeatures ToContract(PixelFeatures px, IReadOnlyDictionary<string, double>? genres)
    {
        var extras = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ExtrasPrefix + "subjectCount"] = px.SubjectCount,
            [ExtrasPrefix + "saliencySpread"] = R(px.SaliencySpread),
            [ExtrasPrefix + "saliencyPeak"] = R(px.SaliencyPeak),
            [ExtrasPrefix + "brightSky"] = R(px.BrightSky),
            [ExtrasPrefix + "edgeDensity"] = R(px.EdgeDensity),
            [ExtrasPrefix + "flatShare"] = R(px.FlatShare),
            [ExtrasPrefix + "whiteShare"] = R(px.WhiteShare),
            [ExtrasPrefix + "darkShare"] = R(px.DarkShare),
            [ExtrasPrefix + "meanChroma"] = R(px.MeanChroma),
            [ExtrasPrefix + "meanLightness"] = R(px.MeanLightness),
        };
        if (px.SubjectContrast is double contrast)
        {
            extras[ExtrasPrefix + "subjectContrast"] = R(contrast);
        }

        if (px.HorizonSpan is double span && px.HorizonContrast is double horizonContrast)
        {
            extras[ExtrasPrefix + "horizonSpan"] = R(span);
            extras[ExtrasPrefix + "horizonContrast"] = R(horizonContrast);
        }

        if (genres is not null)
        {
            double mass = 0;
            foreach (double v in genres.Values)
            {
                mass += v;
            }

            extras[ExtrasPrefix + "priorMass"] = R(mass);
        }

        return new CompositionFeatures
        {
            Genres = genres,
            SubjectX = R(px.SubjectX),
            SubjectY = R(px.SubjectY),
            SubjectArea = R(px.SubjectArea),
            ThirdsScore = R(px.ThirdsScore),
            CenterScore = R(px.CenterScore),
            SymmetryScore = R(px.SymmetryScore),
            NegativeSpace = R(px.NegativeSpace),
            SeparationScore = R(px.SeparationScore),
            HorizonTiltDeg = px.HorizonTiltDeg is double tilt ? Math.Round(tilt, 2) : null,
            HorizonY = R(px.HorizonY),
            HorizontalEdges = R(px.HorizontalEdges),
            VerticalEdges = R(px.VerticalEdges),
            DiagonalEdges = R(px.DiagonalEdges),
            Extras = extras,
        };
    }

    private static double R(double v) => Math.Round(v, 4);

    private static double? R(double? v) => v is double d ? Math.Round(d, 4) : null;

    private static string ComputeFingerprint(HeuristicCompositionSettings settings, GenreTaxonomy? taxonomy, GenrePriorModel? priors)
    {
        var sb = new StringBuilder();
        sb.Append("rev=").Append(AlgorithmRevision).Append(';');
        sb.Append(JsonSerializer.Serialize(settings, ContractJson.Options)).Append(';');
        if (priors is not null && taxonomy is not null)
        {
            sb.Append("taxonomy=").Append(taxonomy.Version).Append(';');
            sb.AppendJoin(',', priors.GenreIds);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }
}
