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
    /// <summary>What started the scan: startup, web, api or schedule.</summary>
    public string? Trigger { get; init; }
    /// <summary>Items of the current stage served from the cache.</summary>
    public long Cached { get; init; }
    /// <summary>Id of the scan job in <c>GET /api/v1/platform</c>.</summary>
    public string? JobId { get; init; }
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
    private readonly Lock _jobLock = new();
    private ScanJob? _job;
    private ProcessReading _jobStart;
    private readonly Lock _subscribersLock = new();
    private readonly List<Channel<ServerEvent>> _subscribers = [];
    private ScanState _state = new();
    private Task _current = Task.CompletedTask;

    public ScanState State => Volatile.Read(ref _state);

    public string ReportDirectory => Path.GetFullPath(runtime.Options.Report.Output);

    public string ReportFile => Path.Combine(ReportDirectory, runtime.Options.Report.BaseName + ".html");

    public Task Current => Volatile.Read(ref _current);

    /// <summary>Scan history (newest last on disk, newest first from <see cref="ScanJobLog.Recent"/>).</summary>
    public ScanJobLog Jobs { get; } = new(Path.Combine(runtime.Paths.State, "scan-jobs.json"), logger);

    /// <summary>The scan in progress, if any.</summary>
    public ScanJob? RunningJob
    {
        get
        {
            lock (_jobLock)
            {
                return _job;
            }
        }
    }

    /// <summary>Number of live event subscribers (open pages and SSE clients).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_subscribersLock)
            {
                return _subscribers.Count;
            }
        }
    }

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

    public bool TryStartScan(string trigger, CancellationToken stopping)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            logger.LogInformation("Scan request ({Trigger}) ignored: a scan or ranking is already running", trigger);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new ScanJob { Id = now.ToString("yyyyMMdd'T'HHmmss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), Trigger = trigger, StartedUtc = now };
        lock (_jobLock)
        {
            _job = job;
            _jobStart = ProcessReading.Take();
        }

        Jobs.Upsert(job);
        logger.LogInformation("Scan {JobId} started ({Trigger})", job.Id, trigger);
        SetState(new ScanState { State = "running", StartedUtc = now, Stage = PipelineStages.Discover, Trigger = trigger, JobId = job.Id });
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
            var progress = new StateProgress(this);
            var snapshot = await Workflows.ScanAsync(runtime, progress, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> files = [];
            if (snapshot.Profile.Library.CapturesInScope > 0)
            {
                progress.Report(new PipelineProgress(PipelineStages.Render, 0, 1));
                var outcome = await Workflows.RenderAsync(runtime, snapshot, null, cancellationToken).ConfigureAwait(false);
                progress.Report(new PipelineProgress(PipelineStages.Render, 1, 1));
                LogFailures(outcome);
                files = outcome.Files;
            }
            else
            {
                logger.LogWarning("Scan found no captures in scope ({Files} files seen)", snapshot.Profile.Library.FilesSeen);
            }

            var library = snapshot.Profile.Library;
            FinishJob("completed", null, job => job with
            {
                RunId = snapshot.Run.RunId,
                FilesSeen = library.FilesSeen,
                Bytes = library.TotalBytes,
                Captures = library.Captures,
                CapturesInScope = library.CapturesInScope,
            });

            SetState(new ScanState
            {
                State = "completed",
                StartedUtc = started,
                FinishedUtc = DateTimeOffset.UtcNow,
                RunId = snapshot.Run.RunId,
                Captures = snapshot.Profile.Library.CapturesInScope,
                Files = files,
                Trigger = State.Trigger,
                JobId = State.JobId,
            });
            Publish("scan.completed", State);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishJob("cancelled", null, null);
            SetState(State with { State = "cancelled", FinishedUtc = DateTimeOffset.UtcNow });
            Publish("scan.cancelled", State);
        }
        catch (Exception ex)
        {
            var message = ex is CliException { Hint: { } hint } ? $"{ex.Message} {hint}" : ex.Message;
            logger.LogError(ex is CliException ? null : ex, "Scan failed: {Message}", message);
            FinishJob("failed", message, null);
            SetState(State with { State = "failed", FinishedUtc = DateTimeOffset.UtcNow, Error = message });
            Publish("scan.failed", State);
        }
    }

    /// <summary>Raises the running job's peak working set (called by the platform sampler).</summary>
    public void ObserveWorkingSet(long bytes)
    {
        lock (_jobLock)
        {
            if (_job is { } job && bytes > (job.PeakWorkingSetBytes ?? 0))
            {
                _job = job with { PeakWorkingSetBytes = bytes };
            }
        }
    }

    private void TrackStage(PipelineProgress value)
    {
        lock (_jobLock)
        {
            if (_job is not { } job)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var stages = job.Stages.ToList();
            var index = stages.FindIndex(s => s.Stage == value.Stage);
            if (index < 0)
            {
                CloseLastStage(stages, now);
                stages.Add(new StageStat { Stage = value.Stage, StartedUtc = now, Completed = value.Completed, Total = value.Total, Cached = value.Cached });
            }
            else
            {
                var stage = stages[index];
                stages[index] = stage with
                {
                    Seconds = (now - stage.StartedUtc).TotalSeconds,
                    Completed = value.Completed,
                    Total = value.Total,
                    Cached = Math.Max(stage.Cached, value.Cached),
                };
            }

            _job = job with { Stages = stages };
        }
    }

    /// <summary>A stage runs until the next one starts, even if it reported progress only once.</summary>
    private static void CloseLastStage(List<StageStat> stages, DateTimeOffset now)
    {
        if (stages.Count > 0 && stages[^1] is var last && now - last.StartedUtc is var elapsed && elapsed.TotalSeconds > last.Seconds)
        {
            stages[^1] = last with { Seconds = elapsed.TotalSeconds };
        }
    }

    private void FinishJob(string state, string? error, Func<ScanJob, ScanJob>? update)
    {
        ScanJob finished;
        lock (_jobLock)
        {
            if (_job is not { } job)
            {
                return;
            }

            var end = ProcessReading.Take();
            var start = _jobStart;
            var now = DateTimeOffset.UtcNow;
            var closed = job.Stages.ToList();
            CloseLastStage(closed, now);
            job = job with { Stages = closed };
            var metadata = job.Stages.FirstOrDefault(s => s.Stage == PipelineStages.Metadata);
            var frames = job.Stages.FirstOrDefault(s => s.Stage == PipelineStages.Frames);
            finished = job with
            {
                State = state,
                Error = error,
                FinishedUtc = now,
                FilesRead = metadata is null ? null : Math.Max(0, metadata.Completed - metadata.Cached),
                FramesAnalysed = frames is null ? null : Math.Max(0, frames.Completed - frames.Cached),
                CpuSeconds = (end.CpuTime - start.CpuTime).TotalSeconds,
                AllocatedBytes = end.Allocated - start.Allocated,
                DiskReadBytes = end.StorageRead - start.StorageRead,
                Gen2Collections = end.Gen2 - start.Gen2,
                PeakWorkingSetBytes = Math.Max(job.PeakWorkingSetBytes ?? 0, end.WorkingSet),
            };
            finished = update?.Invoke(finished) ?? finished;
            _job = null;
        }

        Jobs.Upsert(finished);
        logger.LogInformation(
            "Scan {JobId} {State} in {Seconds:0.###} s: {Read} of {Files} files read, {Frames} frames analysed, {Captures} captures in scope",
            finished.Id, state, finished.Seconds, finished.FilesRead ?? 0, finished.FilesSeen ?? 0, finished.FramesAnalysed ?? 0, finished.CapturesInScope ?? 0);
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
            owner.TrackStage(value);
            owner.SetState(owner.State with { Stage = value.Stage, Completed = value.Completed, Total = value.Total, Cached = value.Cached });
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
