using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

/// <summary>Creates the <c>builtin.recommend.mcda</c> recommender.</summary>
public sealed class McdaRecommenderFactory : IPluginFactory
{
    public const string PluginId = "builtin.recommend.mcda";

    public string Id => PluginId;

    public ComponentKind Kind => ComponentKind.Recommendation;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.GetSettings<McdaSettings>() ?? new McdaSettings();
        return new McdaRecommender(settings, context.Taxonomy, context.LoggerFactory.CreateLogger<McdaRecommender>());
    }
}
