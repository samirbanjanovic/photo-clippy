using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>Creates <see cref="NativeMetadataExtractor"/> (<c>builtin.metadata.native</c>). No settings.</summary>
public sealed class NativeMetadataExtractorFactory : IPluginFactory
{
    public string Id => NativeMetadataExtractor.PluginId;

    public ComponentKind Kind => ComponentKind.Metadata;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new NativeMetadataExtractor();
    }
}

/// <summary>
/// Creates <see cref="ExifToolMetadataExtractor"/> (<c>builtin.metadata.exiftool</c>) configured from the
/// <c>plugins:builtin.metadata.exiftool</c> section (<see cref="ExifToolSettings"/>). Never throws when exiftool is
/// missing; use <see cref="IPlugin.CheckHealthAsync"/> to detect that.
/// </summary>
public sealed class ExifToolMetadataExtractorFactory : IPluginFactory
{
    public string Id => ExifToolMetadataExtractor.PluginId;

    public ComponentKind Kind => ComponentKind.Metadata;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExifToolSettings? settings = null;
        try
        {
            settings = context.GetSettings<ExifToolSettings>();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new ExifToolMetadataExtractor(settings ?? new ExifToolSettings());
    }
}
