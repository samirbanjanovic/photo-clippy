using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// <c>composition.analyze</c> / <c>color.analyze</c> adapters. Plugin-reported errors (JSON-RPC error responses or
/// undecodable results) are logged and yield empty features; transport failures (timeouts, crashes, degraded
/// plugin) propagate as <see cref="ExternalPluginException"/> so the host can count them.
/// </summary>
internal abstract class ExternalFrameAnalyzer<TFeatures>(ExternalPluginConnection connection, string method)
    : ExternalPluginBase(connection)
    where TFeatures : class, new()
{
    public FrameRequirements Requirements => new(Connection.Descriptor.MaxEdge);

    public async ValueTask<TFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Connection.AnalyzeFrameAsync(method, frame, context, cancellationToken).ConfigureAwait(false);
            return Sanitize(Connection.Decode<TFeatures>(result, method));
        }
        catch (ExternalPluginException ex) when (IsPluginError(ex))
        {
            Logger.LogWarning("External plugin {PluginId} failed '{Method}' for {CaptureId}: {Error}", Options.Id, method, context.CaptureId, ex.Message);
            return new TFeatures();
        }
    }

    protected abstract TFeatures Sanitize(TFeatures features);
}

internal sealed class ExternalCompositionAnalyzer(ExternalPluginConnection connection)
    : ExternalFrameAnalyzer<CompositionFeatures>(connection, "composition.analyze"), ICompositionAnalyzer
{
    protected override CompositionFeatures Sanitize(CompositionFeatures features) => features with
    {
        Genres = SanitizeGenres(features.Genres),
        Extras = SanitizeExtras(features.Extras),
    };

    private IReadOnlyDictionary<string, double>? SanitizeGenres(IReadOnlyDictionary<string, double>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return genres;
        }

        var taxonomy = Connection.Taxonomy;
        var acceptAll = taxonomy.Genres.Count == 0;
        var clean = new Dictionary<string, double>(genres.Count, StringComparer.Ordinal);
        foreach (var (id, score) in genres)
        {
            if (!double.IsFinite(score) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var canonical = acceptAll ? id : taxonomy.Find(id)?.Id;
            if (canonical is null)
            {
                Logger.LogDebug("External plugin {PluginId} returned unknown genre '{Genre}'", Options.Id, id);
                continue;
            }

            clean[canonical] = Math.Clamp(score, 0, 1);
        }

        var sum = clean.Values.Sum();
        if (sum > 1)
        {
            foreach (var key in clean.Keys.ToArray())
            {
                clean[key] /= sum;
            }
        }

        return clean;
    }
}

internal sealed class ExternalColorAnalyzer(ExternalPluginConnection connection)
    : ExternalFrameAnalyzer<ColorFeatures>(connection, "color.analyze"), IColorAnalyzer
{
    protected override ColorFeatures Sanitize(ColorFeatures features) => features with
    {
        Extras = SanitizeExtras(features.Extras),
    };
}
