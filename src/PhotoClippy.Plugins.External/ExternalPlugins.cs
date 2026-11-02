using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// Entry point for out-of-process plugins (any language) speaking JSON-RPC 2.0 over stdio or HTTP.
/// See <c>examples/PROTOCOL.md</c> for the wire contract.
/// </summary>
public static class ExternalPlugins
{
    /// <summary>
    /// Starts (stdio) or connects to (http) the plugin, performs the <c>initialize</c> handshake and returns an adapter
    /// implementing the component interface for <see cref="ExternalPluginOptions.Kind"/> plus <see cref="IExternalPlugin"/>.
    /// Dispose the result to stop the child process.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="ExternalPluginException">The plugin could not be started or its manifest was rejected.</exception>
    public static async Task<IPlugin> CreateAsync(ExternalPluginOptions options, PluginContext context, CancellationToken ct = default)
    {
        var connection = await ExternalPluginConnection.ConnectAsync(options, context, ct).ConfigureAwait(false);
        return options.Kind switch
        {
            ComponentKind.Metadata => new ExternalMetadataExtractor(connection),
            ComponentKind.Composition => new ExternalCompositionAnalyzer(connection),
            ComponentKind.Color => new ExternalColorAnalyzer(connection),
            ComponentKind.Recommendation => new ExternalRecommender(connection),
            ComponentKind.Report => new ExternalReportRenderer(connection),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Kind, "Unsupported plugin kind."),
        };
    }
}
