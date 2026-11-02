using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Color;

/// <summary>Host entry point for <c>builtin.color</c>; reads <see cref="ColorSettings"/> from <c>plugins:builtin.color</c>.</summary>
public sealed class ColorAnalyzerFactory : IPluginFactory
{
    public string Id => ColorAnalyzer.PluginId;

    public ComponentKind Kind => ComponentKind.Color;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ColorAnalyzer(context.GetSettings<ColorSettings>());
    }
}
