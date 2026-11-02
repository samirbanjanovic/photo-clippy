using Microsoft.Extensions.Logging;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Services;

namespace PhotoClippy.Cli.Web;

internal sealed record ModelStatusRow(string PluginId, string RelativePath, long? SizeBytes, string? License, bool Present);

/// <summary>Model status and background download (the same work as <c>photo-clippy models fetch</c>) for the web UI.</summary>
internal sealed class ModelService(AppRuntime runtime, ScanCoordinator coordinator, ILogger<ModelService> logger)
{
    private int _downloading;

    public bool Downloading => Volatile.Read(ref _downloading) == 1;

    public ModelFetchProgress? Progress { get; private set; }

    public string? LastMessage { get; private set; }

    public bool LastFailed { get; private set; }

    public event Action? Changed;

    public async Task<IReadOnlyList<ModelStatusRow>> StatusAsync(CancellationToken cancellationToken)
    {
        var (models, fetcher, http) = await CollectAsync(cancellationToken).ConfigureAwait(false);
        using (http)
        {
            return [.. models.Select(m => new ModelStatusRow(m.PluginId, m.Asset.RelativePath, m.Asset.SizeBytes, m.Asset.License, fetcher.IsPresent(m.Asset)))];
        }
    }

    public bool TryStartDownload(CancellationToken stopping)
    {
        if (Interlocked.CompareExchange(ref _downloading, 1, 0) != 0)
        {
            return false;
        }

        LastMessage = null;
        LastFailed = false;
        Progress = null;
        Changed?.Invoke();
        _ = Task.Run(() => DownloadAsync(stopping), CancellationToken.None);
        return true;
    }

    private async Task DownloadAsync(CancellationToken stopping)
    {
        try
        {
            var (models, fetcher, http) = await CollectAsync(stopping).ConfigureAwait(false);
            using (http)
            {
                var lastTick = 0L;
                var progress = new Progress<ModelFetchProgress>(p =>
                {
                    Progress = p;
                    var now = Environment.TickCount64;
                    if (now - lastTick > 500)
                    {
                        lastTick = now;
                        Changed?.Invoke();
                    }
                });
                var results = await fetcher.FetchAsync(models, progress: progress, cancellationToken: stopping).ConfigureAwait(false);
                var failed = results.Where(r => r.Status == ModelFetchStatus.Failed).ToList();
                LastFailed = failed.Count > 0;
                LastMessage = failed.Count > 0
                    ? string.Join("; ", failed.Select(f => $"{f.Model.Asset.RelativePath}: {f.Error}"))
                    : results.Any(r => r.Status == ModelFetchStatus.Downloaded) ? "Models downloaded. The next scan uses them." : "All models are already present.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Model download failed");
            LastFailed = true;
            LastMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            LastMessage = "Cancelled.";
        }
        finally
        {
            Volatile.Write(ref _downloading, 0);
            coordinator.Notify("models.updated", new { LastMessage, LastFailed });
            Changed?.Invoke();
        }
    }

    private async Task<(IReadOnlyList<PluginModel> Models, ModelFetcher Fetcher, HttpClient Http)> CollectAsync(CancellationToken cancellationToken)
    {
        await using var plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind>(Enum.GetValues<ComponentKind>()), checkHealth: false, cancellationToken).ConfigureAwait(false);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"photo-clippy/{AppRuntime.ToolVersion}");
        return (ModelFetcher.Collect(plugins), new ModelFetcher(http, runtime.Paths.Models, runtime.CreateLogger("Models")), http);
    }
}
