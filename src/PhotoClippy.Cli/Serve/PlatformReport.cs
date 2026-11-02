using PhotoClippy.Contracts;

namespace PhotoClippy.Cli.Serve;

internal sealed record PlatformTotals(long Requests, long InFlight, IReadOnlyDictionary<string, long> StatusClasses, int Sessions, int EventSubscribers);

internal sealed record LastRunInfo(string RunId, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, IReadOnlyDictionary<string, double> Timings, IReadOnlyList<PluginManifest> Plugins, LibrarySummary Library);

/// <summary>Everything the Platform page shows; also served as <c>GET /api/v1/platform</c>.</summary>
internal sealed record PlatformReport
{
    public required string Version { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public double UptimeSeconds { get; init; }
    public required HostInfo Host { get; init; }
    public PlatformSample? Now { get; init; }
    public required PlatformTotals Totals { get; init; }
    public required ScanState Scan { get; init; }
    public ScanJob? RunningJob { get; init; }
    public IReadOnlyList<ScanJob> Jobs { get; init; } = [];
    public DateTimeOffset? NextScheduledScanUtc { get; init; }
    public required StorageInfo Storage { get; init; }
    public LastRunInfo? LastRun { get; init; }
    public IReadOnlyList<PlatformSample> Samples { get; init; } = [];
    public IReadOnlyList<LogEntry> Log { get; init; } = [];

    public static async Task<LastRunInfo?> LoadLastRunAsync(AppRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.Store.LoadLatestSnapshotAsync(cancellationToken).ConfigureAwait(false) is { } snapshot
                ? new LastRunInfo(snapshot.Run.RunId, snapshot.Run.StartedUtc, snapshot.Run.FinishedUtc, snapshot.Run.Timings, snapshot.Run.Plugins, snapshot.Profile.Library)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    /// <summary>Cheap: everything is in memory except storage sizes, which <see cref="StorageProbe"/> caches.</summary>
    public static PlatformReport Build(
        ScanCoordinator coordinator,
        ScanScheduler scheduler,
        PlatformMonitor monitor,
        StorageProbe storage,
        LastRunInfo? lastRun,
        bool includeSamples,
        bool includeLog)
    {
        var samples = monitor.Samples;
        return new PlatformReport
        {
            Version = AppRuntime.ToolVersion,
            StartedUtc = monitor.StartedUtc,
            UptimeSeconds = (DateTimeOffset.UtcNow - monitor.StartedUtc).TotalSeconds,
            Host = PlatformMonitor.Host(),
            Now = samples.Count == 0 ? null : samples[^1],
            Totals = new PlatformTotals(monitor.TotalRequests, monitor.InFlightRequests, monitor.StatusClasses, monitor.Sessions, coordinator.SubscriberCount),
            Scan = coordinator.State,
            RunningJob = coordinator.RunningJob,
            Jobs = coordinator.Jobs.Recent(),
            NextScheduledScanUtc = scheduler.NextRunUtc,
            Storage = storage.Get(),
            LastRun = lastRun,
            Samples = includeSamples ? samples : [],
            Log = includeLog ? monitor.Log : [],
        };
    }
}
