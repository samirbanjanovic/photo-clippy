using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Composition;

public sealed class HeuristicCompositionFactory : IPluginFactory
{
    public string Id => HeuristicCompositionAnalyzer.PluginId;

    public ComponentKind Kind => ComponentKind.Composition;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.GetSettings<HeuristicCompositionSettings>() ?? new HeuristicCompositionSettings();
        return new HeuristicCompositionAnalyzer(settings, context.Taxonomy);
    }
}
