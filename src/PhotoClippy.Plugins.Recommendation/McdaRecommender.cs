using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

/// <summary>
/// Multi-criteria lens recommender: scores user-supplied candidate lenses (or spec archetypes) against the user's
/// own capture samples on fit, low light, isolation, reach, genre, portability and value, with a Poisson bootstrap
/// for uncertainty and an analytic weight-sensitivity analysis. Stateless and thread-safe.
/// </summary>
public sealed class McdaRecommender : IRecommender
{
    private readonly McdaSettings _settings;
    private readonly GenreTaxonomy _taxonomy;
    private readonly ILogger _logger;

    public McdaRecommender(McdaSettings? settings = null, GenreTaxonomy? taxonomy = null, ILogger<McdaRecommender>? logger = null)
    {
        _settings = (settings ?? new McdaSettings()).Normalized();
        _taxonomy = taxonomy ?? new GenreTaxonomy();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Manifest = new PluginManifest
        {
            Id = McdaRecommenderFactory.PluginId,
            Name = "Multi-criteria lens recommender",
            Version = "1.0.0",
            Kind = ComponentKind.Recommendation,
            Description = "Ranks user-supplied lenses (or spec archetypes) against your own shooting: fit, low light, isolation, reach, genre, portability, value; bootstrap uncertainty and weight sensitivity.",
            Capabilities = ["presets", "bootstrap", "sensitivity", "archetypes", "redundancy"],
            Fingerprint = Fingerprint(_settings),
        };
    }

    public PluginManifest Manifest { get; }

    public McdaSettings Settings => _settings;

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PluginHealth.Ok());

    public Task<RecommendationSet> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var taxonomy = request.Taxonomy.Genres.Count > 0 ? request.Taxonomy : _taxonomy;
        var result = new McdaRun(_settings, request, taxonomy, cancellationToken).Execute();
        _logger.LogDebug(
            "MCDA ranked {Candidates} candidates over {Samples} samples with {Replicates} replicates in {Elapsed} ms",
            result.CandidateCount,
            result.SampleCount,
            request.Preferences.BootstrapReplicates,
            sw.ElapsedMilliseconds);
        return Task.FromResult(result);
    }

    private static string Fingerprint(McdaSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, ContractJson.Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
