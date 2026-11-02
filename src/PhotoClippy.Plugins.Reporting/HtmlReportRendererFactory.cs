using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Host entry point for <c>builtin.report.html</c>.</summary>
public sealed class HtmlReportRendererFactory : IPluginFactory
{
    public string Id => HtmlReportRenderer.PluginId;

    public ComponentKind Kind => ComponentKind.Report;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new HtmlReportRenderer(context.GetSettings<HtmlReportSettings>(), context.DataDirectory);
    }
}
