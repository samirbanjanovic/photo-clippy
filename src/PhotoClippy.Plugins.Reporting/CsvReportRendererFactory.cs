using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Host entry point for <c>builtin.report.csv</c>; takes no settings.</summary>
public sealed class CsvReportRendererFactory : IPluginFactory
{
    public string Id => CsvReportRenderer.PluginId;

    public ComponentKind Kind => ComponentKind.Report;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new CsvReportRenderer();
    }
}
