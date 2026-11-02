namespace PhotoClippy.Contracts;

/// <summary>Compact per-capture record used by recommenders (no paths, no GPS).</summary>
public sealed record CaptureSample
{
    public required double Focal35 { get; init; }
    public double? FocalMm { get; init; }
    public double? FNumber { get; init; }
    /// <summary>The used lens' maximum aperture at this focal length (EXIF LensSpecification or observed).</summary>
    public double? LensMaxApertureAtFocal { get; init; }
    public int? Iso { get; init; }
    public double? ExposureS { get; init; }
    public double? Ev100 { get; init; }
    public bool? Flash { get; init; }
    public string? LensKey { get; init; }
    public bool LensIsZoom { get; init; }
    public bool AtWideEnd { get; init; }
    public bool AtTeleEnd { get; init; }
    public string? Genre { get; init; }
    public double GenreConfidence { get; init; }
    public double? Separation { get; init; }
    public DateTime? When { get; init; }
    /// <summary>Sample weight (recency / rating weighting); 1 by default.</summary>
    public double Weight { get; init; } = 1;
}

public sealed record RecommendationPreferences
{
    /// <summary>balanced | low-light | reach | versatility | portability | value.</summary>
    public string Preset { get; init; } = "balanced";
    /// <summary>Criterion id → weight override; merged over the preset.</summary>
    public IReadOnlyDictionary<string, double> Weights { get; init; } = new Dictionary<string, double>();
    /// <summary>Hard filter, applied only to candidates whose price was supplied.</summary>
    public double? BudgetUsd { get; init; }
    /// <summary>Hard filter, applied only to candidates whose weight was supplied.</summary>
    public double? MaxWeightG { get; init; }
    public int HighIsoThreshold { get; init; } = 3200;
    public int BootstrapReplicates { get; init; } = 500;
    public int Seed { get; init; } = 1729;
    /// <summary>Maximum ranked lenses returned; 0 or less keeps all.</summary>
    public int TopN { get; init; } = 8;
}

public sealed record RecommendationRequest
{
    public required ShootingProfile Profile { get; init; }
    public required TargetSystem Target { get; init; }
    public required IReadOnlyList<CaptureSample> Samples { get; init; }
    /// <summary>
    /// Lenses to evaluate, supplied by the user at run time. When empty, a recommender may propose
    /// <see cref="LensSpecSource.Suggested"/> spec archetypes derived from the profile.
    /// </summary>
    public IReadOnlyList<LensSpec> Candidates { get; init; } = [];
    public IReadOnlyList<OwnedLens> Owned { get; init; } = [];
    public GenreTaxonomy Taxonomy { get; init; } = new();
    public RecommendationPreferences Preferences { get; init; } = new();
}

public sealed record CriterionInfo(string Id, string Name, string Description);

public sealed record LensRecommendation
{
    public required LensSpec Lens { get; init; }
    public required int Rank { get; init; }
    /// <summary>Final score 0..1 (weighted criteria × redundancy multiplier).</summary>
    public required double Score { get; init; }
    public double ScoreLow { get; init; }
    public double ScoreHigh { get; init; }
    /// <summary>Bootstrap probability of ranking first.</summary>
    public double ProbabilityBest { get; init; }
    /// <summary>Criterion id → normalized 0..1 score.</summary>
    public IReadOnlyDictionary<string, double> Criteria { get; init; } = new Dictionary<string, double>();
    /// <summary>Raw, explainable metrics (coverageShare, stopsGained, …).</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();
    public double Redundancy { get; init; }
    public string? RedundantWith { get; init; }
    public bool AlreadyOwned { get; init; }
    public string? Verdict { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> TradeOffs { get; init; } = [];
    public IReadOnlyList<string> WinsInsteadWhen { get; init; } = [];
}

public sealed record RecommendationSet
{
    public required string RecommenderId { get; init; }
    public required string Preset { get; init; }
    public required TargetSystem Target { get; init; }
    public IReadOnlyDictionary<string, double> Weights { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<CriterionInfo> Criteria { get; init; } = [];
    public int SampleCount { get; init; }
    public int CandidateCount { get; init; }
    public IReadOnlyList<LensRecommendation> Ranked { get; init; } = [];
    /// <summary>Preset id → id of the lens that ranks first under it.</summary>
    public IReadOnlyDictionary<string, string> PresetWinners { get; init; } = new Dictionary<string, string>();
    public string? Headline { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
}

/// <summary>Recommendation component.</summary>
public interface IRecommender : IPlugin
{
    Task<RecommendationSet> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default);
}

public static class CriterionIds
{
    public const string Fit = "fit";
    public const string LowLight = "lowLight";
    public const string Isolation = "isolation";
    public const string Reach = "reach";
    public const string Genre = "genre";
    /// <summary>Only scored when every candidate has a weight.</summary>
    public const string Portability = "portability";
    /// <summary>Only scored when every candidate has a price.</summary>
    public const string Value = "value";
}
