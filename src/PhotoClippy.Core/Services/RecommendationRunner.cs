using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Profiling;

namespace PhotoClippy.Core.Services;

public sealed class TargetNotFoundException(string message, IReadOnlyList<TargetSystem> available) : Exception(message)
{
    public IReadOnlyList<TargetSystem> Available { get; } = available;
}

/// <summary>Ranks runtime-supplied candidate lenses against a stored analysis snapshot.</summary>
public static class RecommendationRunner
{
    /// <summary>Parses candidate shorthand from config/CLI entries (each entry may itself be a comma/semicolon list).</summary>
    public static IReadOnlyList<LensSpec> ParseCandidates(IEnumerable<string> entries, out IReadOnlyList<string> errors)
    {
        var all = new List<LensSpec>();
        var allErrors = new List<string>();
        foreach (var entry in entries)
        {
            all.AddRange(LensSpec.ParseList(entry, out var entryErrors));
            allErrors.AddRange(entryErrors);
        }

        errors = allErrors;
        return [.. all.DistinctBy(l => l.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The target body: <paramref name="body"/> when given, otherwise the snapshot's auto-detected target (null if none).</summary>
    public static TargetSystem? ResolveTarget(AnalysisSnapshot snapshot, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return snapshot.Profile.Target;
        }

        return ProfileBuilder.MatchBody(snapshot.Systems, body.Trim())
            ?? throw new TargetNotFoundException(
                snapshot.Systems.Count == 0
                    ? $"No interchangeable-lens body matches '{body}': the library has none."
                    : $"No interchangeable-lens body matches '{body}'. Available: {string.Join(", ", snapshot.Systems.Select(s => s.BodyDisplay ?? s.BodyKey))}.",
                snapshot.Systems);
    }

    public static RecommendationRequest BuildRequest(AnalysisSnapshot snapshot, TargetSystem target, IReadOnlyList<LensSpec> candidates, RecommendationOptions options, int highIsoThreshold) => new()
    {
        Profile = snapshot.Profile with { Target = target },
        Target = target,
        Samples = snapshot.Samples,
        Candidates = candidates,
        Owned = target.BodyKey is { } key && snapshot.OwnedByBody.TryGetValue(key, out var owned) ? owned : [],
        Taxonomy = snapshot.Taxonomy,
        Preferences = new RecommendationPreferences
        {
            Preset = options.Preset,
            Weights = new Dictionary<string, double>(options.Weights, StringComparer.OrdinalIgnoreCase),
            BudgetUsd = options.BudgetUsd,
            MaxWeightG = options.MaxWeightG,
            HighIsoThreshold = highIsoThreshold,
            BootstrapReplicates = options.BootstrapReplicates,
            Seed = options.Seed,
            TopN = options.TopN,
        },
    };

    /// <summary>
    /// Ranks <paramref name="candidates"/> (empty → the recommender suggests archetypes) and returns an updated snapshot
    /// carrying the recommendation, the (possibly re-targeted) profile and regenerated insights.
    /// </summary>
    public static async Task<AnalysisSnapshot> RunAsync(
        AnalysisSnapshot snapshot,
        IRecommender recommender,
        IReadOnlyList<LensSpec> candidates,
        RecommendationOptions options,
        int highIsoThreshold,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(snapshot, options.Body);
        if (target is null)
        {
            return snapshot with
            {
                Recommendation = null,
                Insights = InsightGenerator.Generate(snapshot.Profile, null, snapshot.Taxonomy),
            };
        }

        var request = BuildRequest(snapshot, target, candidates, options, highIsoThreshold);
        var set = await recommender.RecommendAsync(request, cancellationToken).ConfigureAwait(false);
        return snapshot with
        {
            Profile = request.Profile,
            Owned = request.Owned,
            Recommendation = set,
            Insights = InsightGenerator.Generate(request.Profile, set, snapshot.Taxonomy),
        };
    }
}
