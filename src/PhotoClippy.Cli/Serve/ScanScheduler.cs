using Microsoft.Extensions.Logging;

namespace PhotoClippy.Cli.Serve;

/// <summary>
/// Periodic rescans driven by <c>serve.rescanHours</c>. <see cref="Reschedule"/> re-reads the interval, so
/// configuration edits made in the web UI take effect without a restart.
/// </summary>
internal sealed class ScanScheduler(AppRuntime runtime, ScanCoordinator coordinator, ILogger<ScanScheduler> logger) : IDisposable
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromDays(30);
    private CancellationTokenSource _wake = new();
    private DateTimeOffset? _next;

    public DateTimeOffset? NextRunUtc => _next;

    public void Reschedule() => Interlocked.Exchange(ref _wake, new CancellationTokenSource()).Cancel();

    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var wake = Volatile.Read(ref _wake);
            var hours = runtime.Options.Serve.RescanHours;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, wake.Token);
            try
            {
                if (hours <= 0 || double.IsNaN(hours))
                {
                    _next = null;
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
                    continue;
                }

                var interval = TimeSpan.FromHours(hours);
                _next = DateTimeOffset.UtcNow + interval;
                var remaining = interval;
                while (remaining > TimeSpan.Zero)
                {
                    var step = remaining < MaxDelay ? remaining : MaxDelay;
                    await Task.Delay(step, linked.Token).ConfigureAwait(false);
                    remaining -= step;
                }

                if (!coordinator.TryStartScan(stopping))
                {
                    logger.LogInformation("Scheduled rescan skipped: a scan is already running");
                }
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                logger.LogDebug("Rescan schedule changed to every {Hours} h", runtime.Options.Serve.RescanHours);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _next = null;
    }

    public void Dispose() => Volatile.Read(ref _wake).Dispose();
}
