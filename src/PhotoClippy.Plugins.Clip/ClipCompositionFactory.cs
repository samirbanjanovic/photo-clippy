using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Clip;

/// <summary>Host entry point for <c>builtin.composition.clip</c>; reads <see cref="ClipSettings"/> from <c>plugins:builtin.composition.clip</c>.</summary>
public sealed class ClipCompositionFactory : IPluginFactory
{
    public string Id => ClipCompositionAnalyzer.PluginId;

    public ComponentKind Kind => ComponentKind.Composition;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ClipCompositionAnalyzer(context, context.GetSettings<ClipSettings>());
    }
}
