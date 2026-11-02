using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// Implemented by every adapter returned from <see cref="ExternalPlugins.CreateAsync"/>, in addition to the
/// component interface of <see cref="ExternalPluginOptions.Kind"/>. Disposing stops the child process.
/// </summary>
public interface IExternalPlugin : IPlugin, IAsyncDisposable, IDisposable
{
    ExternalPluginOptions Options { get; }

    /// <summary>True once the restart budget is exhausted; every request then fails fast.</summary>
    bool IsDegraded { get; }

    int RestartCount { get; }

    /// <summary>Process id of the current child (stdio transport), null otherwise.</summary>
    int? ProcessId { get; }
}
