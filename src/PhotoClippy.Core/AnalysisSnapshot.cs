using PhotoClippy.Contracts;

namespace PhotoClippy.Core;

/// <summary>
/// Persisted result of an analysis run: everything needed to re-rank candidate lenses or re-render reports
/// without rescanning the library. Contains no file paths or coordinates.
/// </summary>
public sealed record AnalysisSnapshot
{
    public required RunInfo Run { get; init; }
    public required ShootingProfile Profile { get; init; }
    public IReadOnlyList<CaptureSample> Samples { get; init; } = [];
    public IReadOnlyList<OwnedLens> Owned { get; init; } = [];
    /// <summary>Owned lenses per interchangeable-lens body key (lets <c>recommend --body</c> switch targets without a rescan).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<OwnedLens>> OwnedByBody { get; init; } = new Dictionary<string, IReadOnlyList<OwnedLens>>();
    /// <summary>Selectable target bodies (interchangeable-lens bodies seen in the library), most relevant first.</summary>
    public IReadOnlyList<TargetSystem> Systems { get; init; } = [];
    public IReadOnlyList<Insight> Insights { get; init; } = [];
    /// <summary>Optional ranking, set only by an explicit recommendation request and reused by <c>report</c>. A new scan leaves this null.</summary>
    public RecommendationSet? Recommendation { get; init; }
    public GenreTaxonomy Taxonomy { get; init; } = new();
}
