using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Pipeline;
using PhotoClippy.Core.Services;

namespace PhotoClippy.Cli.Serve;

internal sealed record ScanState
{
    public string State { get; init; } = "idle";
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public string? Stage { get; init; }
    public long Completed { get; init; }
    public long? Total { get; init; }
    public string? RunId { get; init; }
    public int? Captures { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public string? Error { get; init; }
}

internal sealed record ServerEvent(string Type, object? Data);

internal sealed record RecommendRequest
{
    /// <summary>Shorthand entries; each may hold a comma-separated list ("24/1.4, 35/1.2").</summary>
    public IReadOnlyList<string>? Candidates { get; init; }
    public string? Body { get; init; }
    public string? Preset { get; init; }
    public double? BudgetUsd { get; init; }
    public double? MaxWeightG { get; init; }
    public int? Top { get; init; }
    public Dictionary<string, double>? Weights { get; init; }
    /// <summary>Re-render the report with the new ranking (default true).</summary>
    public bool? Render { get; init; }
}

internal sealed class BusyException(string message) : Exception(message);

/// <summary>
/// Serializes scans and re-rankings for the daemon (one at a time), tracks scan state and fans out events to SSE subscribers.
/// </summary>
internal sealed class ScanCoordinator(AppRuntime runtime, ILogger<ScanCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _subscribersLock = new();
    private readonly List<Channel<ServerEvent>> _subscribers = [];
    private ScanState _state = new();
    private Task _current = Task.CompletedTask;

    public ScanState State => Volatile.Read(ref _state);

    public string ReportDirectory => Path.GetFullPath(runtime.Options.Report.Output);

    public string ReportFile => Path.Combine(ReportDirectory, runtime.Options.Report.BaseName + ".html");

    public Task Current => Volatile.Read(ref _current);

    /// <summary>True while a scan or a re-ranking holds the coordinator.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Runs <paramref name="action"/> once no scan or ranking is in progress (used to hot-apply configuration).</summary>
    public async Task RunExclusiveAsync(Action action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes an application event (e.g. config.applied) to live subscribers.</summary>
    public void Notify(string type, object? data = null) => Publish(type, data);

    public bool TryStartScan(CancellationToken stopping)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            return false;
        }

        SetState(new ScanState { State = "running", StartedUtc = DateTimeOffset.UtcNow, Stage = PipelineStages.Discover });
        Publish("scan.started", State);
        Volatile.Write(ref _current, Task.Run(async () =>
        {
            try
            {
                await ScanCoreAsync(stopping).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None));
        return true;
    }

    public async Task<RecommendationSet?> RecommendAsync(RecommendRequest request, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
        {
            throw new BusyException("A scan is running; try again when it has finished (GET /api/v1/scan).");
        }

        try
        {
            var options = Merge(runtime.Options.Recommendation, request);
            var snapshot = await Workflows.LoadLatestAsync(runtime, cancellationToken).ConfigureAwait(false);
            var candidates = Workflows.ParseCandidates(options.Candidates);
            if (candidates.Count == 0 && request.Candidates is null && snapshot.Recommendation is { } previous)
            {
                candidates = [.. previous.Ranked.Select(r => r.Lens)];
            }

            snapshot = await Workflows.RecommendAsync(runtime, snapshot, candidates, cancellationToken, options).ConfigureAwait(false);
            if (request.Render != false)
            {
                LogFailures(await Workflows.RenderAsync(runtime, snapshot, null, cancellationToken).ConfigureAwait(false));
            }

            Publish("recommendation.updated", new { snapshot.Run.RunId, snapshot.Recommendation?.Headline });
            return snapshot.Recommendation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ChannelReader<ServerEvent> Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_subscribersLock)
        {
            _subscribers.Add(channel);
        }

        unsubscribe = () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        };
        return channel.Reader;
    }

    private async Task ScanCoreAsync(CancellationToken cancellationToken)
    {
        var started = State.StartedUtc;
        try
        {
            var snapshot = await Workflows.ScanAsync(runtime, new StateProgress(this), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> files = [];
            if (snapshot.Profile.Library.CapturesInScope > 0)
            {
                var outcome = await Workflows.RenderAsync(runtime, snapshot, null, cancellationToken).ConfigureAwait(false);
                LogFailures(outcome);
                files = outcome.Files;
            }
            else
            {
                logger.LogWarning("Scan found no captures in scope ({Files} files seen)", snapshot.Profile.Library.FilesSeen);
            }

            SetState(new ScanState
            {
                State = "completed",
                StartedUtc = started,
                FinishedUtc = DateTimeOffset.UtcNow,
                RunId = snapshot.Run.RunId,
                Captures = snapshot.Profile.Library.CapturesInScope,
                Files = files,
            });
            logger.LogInformation("Scan {RunId} finished: {Captures} captures in scope", snapshot.Run.RunId, snapshot.Profile.Library.CapturesInScope);
            Publish("scan.completed", State);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(State with { State = "cancelled", FinishedUtc = DateTimeOffset.UtcNow });
            Publish("scan.cancelled", State);
        }
        catch (Exception ex)
        {
            var message = ex is CliException { Hint: { } hint } ? $"{ex.Message} {hint}" : ex.Message;
            logger.LogError(ex is CliException ? null : ex, "Scan failed: {Message}", message);
            SetState(State with { State = "failed", FinishedUtc = DateTimeOffset.UtcNow, Error = message });
            Publish("scan.failed", State);
        }
    }

    private void LogFailures(ReportOutcome outcome)
    {
        foreach (var failure in outcome.Failures)
        {
            logger.LogWarning("Renderer {Renderer} failed: {Error}", failure.RendererId, failure.Error);
        }
    }

    private static RecommendationOptions Merge(RecommendationOptions o, RecommendRequest request)
    {
        var weights = new Dictionary<string, double>(o.Weights, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Weights ?? [])
        {
            weights[key] = value;
        }

        return new RecommendationOptions
        {
            Candidates = request.Candidates is { } c ? [.. c] : [.. o.Candidates],
            Preset = request.Preset ?? o.Preset,
            Weights = weights,
            BudgetUsd = request.BudgetUsd ?? o.BudgetUsd,
            MaxWeightG = request.MaxWeightG ?? o.MaxWeightG,
            Body = request.Body ?? o.Body,
            BootstrapReplicates = o.BootstrapReplicates,
            Seed = o.Seed,
            TopN = request.Top ?? o.TopN,
            Prompt = false,
        };
    }

    private void SetState(ScanState state) => Volatile.Write(ref _state, state);

    private void Publish(string type, object? data)
    {
        var item = new ServerEvent(type, data);
        lock (_subscribersLock)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(item);
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Mirrors pipeline progress into <see cref="State"/> and publishes at most one event per second per stage.</summary>
    private sealed class StateProgress(ScanCoordinator owner) : IProgress<PipelineProgress>
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, long> _lastPublished = [];

        public void Report(PipelineProgress value)
        {
            owner.SetState(owner.State with { Stage = value.Stage, Completed = value.Completed, Total = value.Total });
            var now = Environment.TickCount64;
            lock (_lock)
            {
                var done = value.Total is { } total && value.Completed >= total;
                if (!done && _lastPublished.TryGetValue(value.Stage, out var last) && now - last < 1000)
                {
                    return;
                }

                _lastPublished[value.Stage] = now;
            }

            owner.Publish("scan.progress", value);
        }
    }
}
