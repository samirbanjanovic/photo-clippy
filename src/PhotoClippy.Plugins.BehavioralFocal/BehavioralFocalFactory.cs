using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.BehavioralFocal;

public sealed class BehavioralFocalFactory : IPluginFactory
{
    public string Id => BehavioralFocalAnalyzer.PluginId;

    public ComponentKind Kind => ComponentKind.Composition;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BehavioralFocalAnalyzer();
    }
}
