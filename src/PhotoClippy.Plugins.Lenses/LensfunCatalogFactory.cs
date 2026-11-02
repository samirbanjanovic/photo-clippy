using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Lenses;

/// <summary>Creates the <c>builtin.lenses.lensfun</c> lens catalog.</summary>
public sealed class LensfunCatalogFactory : IPluginFactory
{
    public const string PluginId = "builtin.lenses.lensfun";

    public string Id => PluginId;

    public ComponentKind Kind => ComponentKind.LensCatalog;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.GetSettings<LensfunSettings>() ?? new LensfunSettings();
        return new LensfunCatalog(settings, context.StateDirectory, context.LoggerFactory.CreateLogger<LensfunCatalog>());
    }
}
