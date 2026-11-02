using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using PhotoClippy.Cli.Serve;

namespace PhotoClippy.Cli.Web;

/// <summary>Base for pages that re-render on daemon events (scan progress, new rankings, applied settings).</summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Action? _unsubscribe;

    [Inject]
    internal ScanCoordinator Coordinator { get; set; } = default!;

    protected CancellationToken Disposing => _cts.Token;

    protected override void OnInitialized()
    {
        var reader = Coordinator.Subscribe(out _unsubscribe);
        _ = PumpAsync(reader);
    }

    private protected virtual Task OnServerEventAsync(ServerEvent serverEvent) => Task.CompletedTask;

    private async Task PumpAsync(ChannelReader<ServerEvent> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    await InvokeAsync(() => OnServerEventAsync(item)).ConfigureAwait(false);
                }

                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _unsubscribe?.Invoke();
            _cts.Dispose();
        }
    }
}
