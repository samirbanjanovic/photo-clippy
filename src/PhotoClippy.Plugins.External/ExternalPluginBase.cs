using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>Shared plumbing for the component adapters: manifest, health, lifetime.</summary>
internal abstract class ExternalPluginBase(ExternalPluginConnection connection) : IExternalPlugin
{
    private static readonly TimeSpan MaxHealthTimeout = TimeSpan.FromSeconds(10);

    protected ExternalPluginConnection Connection { get; } = connection;

    protected ILogger Logger => Connection.Logger;

    public PluginManifest Manifest => Connection.Descriptor.Manifest;

    public ExternalPluginOptions Options => Connection.Options;

    public bool IsDegraded => Connection.IsDegraded;

    public int RestartCount => Connection.RestartCount;

    public int? ProcessId => Connection.ProcessId;

    public async ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (Connection.IsDisposed)
        {
            return PluginHealth.Unhealthy("plugin has been disposed");
        }

        if (Connection.IsDegraded)
        {
            return PluginHealth.Unhealthy($"degraded: plugin process failed {Connection.RestartCount} restart(s)");
        }

        var timeout = Connection.Timeout < MaxHealthTimeout ? Connection.Timeout : MaxHealthTimeout;
        try
        {
            var result = await Connection.CallDirectAsync("health", timeout, cancellationToken).ConfigureAwait(false);
            var health = Connection.Decode<HealthResult>(result, "health");
            return health.Healthy ? PluginHealth.Ok(health.Message) : PluginHealth.Unhealthy(health.Message ?? "plugin reported unhealthy");
        }
        catch (ExternalPluginException ex) when (ex.IsRemote && ex.Code == ExternalPluginErrorCodes.MethodNotFound)
        {
            return PluginHealth.Ok("responding (no health method)");
        }
        catch (ExternalPluginException ex)
        {
            return PluginHealth.Unhealthy(ex.Message);
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>True for failures the plugin itself reported (or garbled), as opposed to transport/process failures.</summary>
    protected static bool IsPluginError(ExternalPluginException ex) =>
        ex.IsRemote || ex.Code == ExternalPluginErrorCodes.InvalidResponse;

    protected static IReadOnlyDictionary<string, double> SanitizeExtras(IReadOnlyDictionary<string, double>? extras)
    {
        if (extras is null || extras.Count == 0)
        {
            return extras ?? new Dictionary<string, double>();
        }

        if (extras.Values.All(double.IsFinite))
        {
            return extras;
        }

        return extras.Where(kv => double.IsFinite(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    protected static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, ContractJson.Options);
}
