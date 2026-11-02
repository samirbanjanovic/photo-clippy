using System.Text.Json;

namespace PhotoClippy.Plugins.External;

internal interface IRpcTransport : IAsyncDisposable
{
    /// <summary>True when frames are exchanged as files on a shared filesystem (stdio); false for inline base64 (http).</summary>
    bool PassesFramesByPath { get; }

    bool IsDegraded { get; }

    int RestartCount { get; }

    int? ProcessId { get; }

    /// <summary>Raised with the <c>initialize</c> result after an automatic restart.</summary>
    Action<JsonElement>? Reinitialized { get; set; }

    /// <summary>Connects / spawns the plugin and performs <c>initialize</c>; returns its result.</summary>
    Task<JsonElement> StartAsync(CancellationToken cancellationToken);

    Task<JsonElement> CallAsync(string method, ParamsWriter? writeParams, TimeSpan timeout, CancellationToken cancellationToken);
}
