using Microsoft.Extensions.Logging;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Plugins;

namespace PhotoClippy.Cli.Web;

/// <summary>Keeps the selected lens catalog plugin alive for the server and runs background updates (same work as <c>photo-clippy lenses update</c>).</summary>
internal sealed class LensCatalogService(AppRuntime runtime, ScanCoordinator coordinator, ILogger<LensCatalogService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PluginSet? _plugins;
    private string? _pluginId;
    private int _updating;

    public bool Updating => Volatile.Read(ref _updating) == 1;

    public string? LastMessage { get; private set; }

    public bool LastFailed { get; private set; }

    public event Action? Changed;

    /// <summary>The selected catalog, or null when none is configured or it failed to load (see <see cref="Problem"/>).</summary>
    public async Task<ILensCatalog?> GetAsync(CancellationToken cancellationToken)
    {
        var id = runtime.Options.Plugins.LensCatalog;
        if (string.IsNullOrWhiteSpace(id))
        {
            Problem = "No lens database is selected (plugins.lensCatalog).";
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_plugins is null || !string.Equals(_pluginId, id, StringComparison.OrdinalIgnoreCase))
            {
                if (_plugins is not null)
                {
                    await _plugins.DisposeAsync().ConfigureAwait(false);
                }

                _plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind> { ComponentKind.LensCatalog }, checkHealth: false, cancellationToken).ConfigureAwait(false);
                _pluginId = id;
                Problem = _plugins.LensCatalog is null ? string.Join(" ", _plugins.Notes.DefaultIfEmpty($"Lens database '{id}' is not available.")) : null;
            }

            return _plugins.LensCatalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Why <see cref="GetAsync"/> returned null.</summary>
    public string? Problem { get; private set; }

    public bool TryStartUpdate(CancellationToken stopping)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
        {
            return false;
        }

        LastMessage = null;
        LastFailed = false;
        Changed?.Invoke();
        _ = Task.Run(() => UpdateAsync(stopping), CancellationToken.None);
        return true;
    }

    private async Task UpdateAsync(CancellationToken stopping)
    {
        try
        {
            var catalog = await GetAsync(stopping).ConfigureAwait(false) ?? throw new InvalidOperationException(Problem);
            var info = await catalog.RefreshAsync(stopping).ConfigureAwait(false);
            LastMessage = $"{info.LensCount:N0} lenses from {info.Makers.Count} makers.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Lens database update failed");
            LastFailed = true;
            LastMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            LastMessage = "Cancelled.";
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
            coordinator.Notify("lenses.updated", new { LastMessage, LastFailed });
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_plugins is not null)
        {
            await _plugins.DisposeAsync().ConfigureAwait(false);
            _plugins = null;
        }

        _gate.Dispose();
    }
}
