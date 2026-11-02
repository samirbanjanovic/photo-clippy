using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Host entry point for <c>builtin.report.json</c>; takes no settings.</summary>
public sealed class JsonReportRendererFactory : IPluginFactory
{
    public string Id => JsonReportRenderer.PluginId;

    public ComponentKind Kind => ComponentKind.Report;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new JsonReportRenderer();
    }
}
