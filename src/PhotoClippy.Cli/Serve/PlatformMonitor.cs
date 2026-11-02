using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;
using PhotoClippy.Core.Storage;

namespace PhotoClippy.Cli.Serve;

/// <summary>One point on the live charts; rates are per second over the preceding interval.</summary>
internal sealed record PlatformSample
{
    public required DateTimeOffset Utc { get; init; }
    /// <summary>Process CPU, % of one core (400 = four cores busy).</summary>
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public double AllocatedBytesPerSecond { get; init; }
    public double GcPausePercent { get; init; }
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
    public int Threads { get; init; }
    public int PoolThreads { get; init; }
    public long PoolQueue { get; init; }
    public double PoolItemsPerSecond { get; init; }
    public double? StorageReadBytesPerSecond { get; init; }
    public double? StorageWriteBytesPerSecond { get; init; }
    public double RequestsPerSecond { get; init; }
    public double? RequestMsP95 { get; init; }
    public int Sessions { get; init; }
    /// <summary>Files or frames processed per second by a running scan (cache hits excluded).</summary>
    public double? ScanItemsPerSecond { get; init; }
    public string? ScanStage { get; init; }
}

internal sealed record LogEntry(DateTimeOffset Utc, LogLevel Level, string Category, string Message, string? Exception);

internal sealed record HostInfo(
    string Machine,
    string Os,
    string Architecture,
    string Runtime,
    string RuntimeIdentifier,
    bool Container,
    int Processors,
    double? CpuLimitCores,
    long? MemoryLimitBytes,
    long? ContainerMemoryBytes,
    long TotalAvailableMemoryBytes,
    bool ServerGc,
    string GcLatency,
    int ProcessId,
    string TimeZone);

internal sealed record StoragePart(string Name, long Bytes);

internal sealed record VolumeInfo(string Role, string Mount, string Format, long TotalBytes, long FreeBytes);

internal sealed record CacheInfo(long MetadataRows, long FeatureRows, long Snapshots, long Bytes);

internal sealed record StorageInfo(string Home, long HomeBytes, IReadOnlyList<StoragePart> Parts, IReadOnlyList<VolumeInfo> Volumes, CacheInfo? Cache, DateTimeOffset MeasuredUtc);

/// <summary>
/// Samples process, GC, thread-pool, I/O, HTTP and scan throughput every <see cref="Interval"/> into a ring buffer
/// for the Platform page and <c>GET /api/v1/platform</c>, and keeps the most recent log entries.
/// </summary>
internal sealed class PlatformMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public const int Capacity = 450; // 15 minutes at 2 s
    public const int LogCapacity = 300;

    private readonly ScanCoordinator _coordinator;
    private readonly Lock _lock = new();
    private readonly Queue<PlatformSample> _samples = new(Capacity);
    private readonly ConcurrentQueue<LogEntry> _log = new();
    private readonly ConcurrentQueue<double> _latencies = new();
    private readonly ConcurrentDictionary<int, long> _statusClasses = new();
    private ProcessReading _previous = ProcessReading.Take();
    private long _requests;
    private long _requestsAtPrevious;
    private long _inFlight;
    private int _sessions;
    private long _scanDoneAtPrevious;
    private string? _scanStageAtPrevious;

    public PlatformMonitor(ScanCoordinator coordinator)
    {
        _coordinator = coordinator;
        StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    }

    public DateTimeOffset StartedUtc { get; }

    public long TotalRequests => Interlocked.Read(ref _requests);

    public long InFlightRequests => Interlocked.Read(ref _inFlight);

    public int Sessions => Volatile.Read(ref _sessions);

    public IReadOnlyDictionary<string, long> StatusClasses => _statusClasses.ToDictionary(p => $"{p.Key}xx", p => p.Value);

    public IReadOnlyList<PlatformSample> Samples
    {
        get
        {
            lock (_lock)
            {
                return [.. _samples];
            }
        }
    }

    public IReadOnlyList<LogEntry> Log => [.. _log.Reverse()];

    public ILoggerProvider LoggerProvider => new BufferLoggerProvider(this);

    public CircuitHandler CircuitHandler => new SessionCounter(this);

    public async Task RunAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                Sample();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void Sample()
    {
        var now = ProcessReading.Take();
        var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_previous.TimestampTicks, now.TimestampTicks).TotalSeconds);
        var requests = Interlocked.Read(ref _requests);
        var latencies = new List<double>();
        while (_latencies.TryDequeue(out var ms))
        {
            latencies.Add(ms);
        }

        latencies.Sort();
        var state = _coordinator.State;
        double? scanRate = null;
        if (state.State == "running")
        {
            var done = state.Completed - state.Cached;
            if (state.Stage == _scanStageAtPrevious)
            {
                scanRate = Math.Max(0, done - _scanDoneAtPrevious) / seconds;
            }

            _scanDoneAtPrevious = done;
            _scanStageAtPrevious = state.Stage;
            _coordinator.ObserveWorkingSet(now.WorkingSet);
        }
        else
        {
            _scanStageAtPrevious = null;
        }

        var sample = new PlatformSample
        {
            Utc = DateTimeOffset.UtcNow,
            CpuPercent = (now.CpuTime - _previous.CpuTime).TotalSeconds / seconds * 100,
            WorkingSetBytes = now.WorkingSet,
            GcHeapBytes = now.GcHeap,
            AllocatedBytesPerSecond = Math.Max(0, now.Allocated - _previous.Allocated) / seconds,
            GcPausePercent = (now.GcPause - _previous.GcPause).TotalSeconds / seconds * 100,
            Gen0 = now.Gen0 - _previous.Gen0,
            Gen1 = now.Gen1 - _previous.Gen1,
            Gen2 = now.Gen2 - _previous.Gen2,
            Threads = now.Threads,
            PoolThreads = now.PoolThreads,
            PoolQueue = now.PoolQueue,
            PoolItemsPerSecond = Math.Max(0, now.PoolCompleted - _previous.PoolCompleted) / seconds,
            StorageReadBytesPerSecond = now.StorageRead - _previous.StorageRead is { } read ? Math.Max(0, read) / seconds : null,
            StorageWriteBytesPerSecond = now.StorageWrite - _previous.StorageWrite is { } write ? Math.Max(0, write) / seconds : null,
            RequestsPerSecond = (requests - _requestsAtPrevious) / seconds,
            RequestMsP95 = latencies.Count == 0 ? null : latencies[(int)Math.Min(latencies.Count - 1, Math.Ceiling(latencies.Count * 0.95) - 1)],
            Sessions = Sessions,
            ScanItemsPerSecond = scanRate,
            ScanStage = state.State == "running" ? state.Stage : null,
        };

        _previous = now;
        _requestsAtPrevious = requests;
        lock (_lock)
        {
            if (_samples.Count == Capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
        }
    }

    /// <summary>Counts requests by status class and records latency. Long-lived Blazor and SSE connections are not timed.</summary>
    public async Task TrackRequestAsync(Microsoft.AspNetCore.Http.HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path;
        var streaming = path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/api/v1/events");
        var start = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _inFlight);
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref _requests);
            _statusClasses.AddOrUpdate(context.Response.StatusCode / 100, 1, (_, n) => n + 1);
            if (!streaming)
            {
                _latencies.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                while (_latencies.Count > 10_000 && _latencies.TryDequeue(out _))
                {
                }
            }
        }
    }

    private void Append(LogEntry entry)
    {
        _log.Enqueue(entry);
        while (_log.Count > LogCapacity && _log.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
    }

    /// <summary>Static facts about the host, runtime and container.</summary>
    public static HostInfo Host() => new(
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.RuntimeIdentifier,
        Cgroup.InContainer,
        Environment.ProcessorCount,
        Cgroup.CpuLimitCores(),
        Cgroup.MemoryLimitBytes(),
        Cgroup.MemoryCurrentBytes(),
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        GCSettings.IsServerGC,
        GCSettings.LatencyMode.ToString(),
        Environment.ProcessId,
        TimeZoneInfo.Local.Id);

    private sealed class SessionCounter(PlatformMonitor owner) : CircuitHandler
    {
        public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._sessions);
            return Task.CompletedTask;
        }

        public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            Interlocked.Decrement(ref owner._sessions);
            return Task.CompletedTask;
        }
    }

    private sealed class BufferLoggerProvider(PlatformMonitor owner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new BufferLogger(owner, categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class BufferLogger(PlatformMonitor owner, string category) : ILogger
    {
        private readonly string _category = category.StartsWith("PhotoClippy.", StringComparison.Ordinal) ? category["PhotoClippy.".Length..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            owner.Append(new LogEntry(DateTimeOffset.UtcNow, logLevel, _category, message, exception is null ? null : $"{exception.GetType().Name}: {exception.Message}"));
        }
    }
}

/// <summary>Disk usage of the data directory and the library volume, cached briefly because it walks the tree.</summary>
internal sealed class StorageProbe(AppRuntime runtime)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly Lock _lock = new();
    private StorageInfo? _cached;

    public StorageInfo Get()
    {
        lock (_lock)
        {
            if (_cached is { } c && DateTimeOffset.UtcNow - c.MeasuredUtc < Ttl)
            {
                return c;
            }
        }

        var value = Measure();
        lock (_lock)
        {
            _cached = value;
        }

        return value;
    }

    private StorageInfo Measure()
    {
        var paths = runtime.Paths;
        var database = runtime.Paths.Database;
        long DbFiles() => Size(database) + Size(database + "-wal") + Size(database + "-shm");
        var report = Path.GetFullPath(runtime.Options.Report.Output);
        var parts = new List<(string Name, string Path, long Bytes)>
        {
            ("analysis cache", database, DbFiles()),
            ("models", paths.Models, DirectorySize(paths.Models)),
            ("reports", report, DirectorySize(report)),
            ("plugin state", paths.State, DirectorySize(paths.State)),
            ("plugins", paths.Plugins, DirectorySize(paths.Plugins)),
        };
        var home = DirectorySize(paths.Home);
        var known = parts.Where(p => IsUnder(p.Path, paths.Home)).Sum(p => p.Bytes);
        parts.Add(("other", paths.Home, Math.Max(0, home - known)));

        StoreStats? stats = null;
        try
        {
            stats = runtime.Store.GetStats();
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
        }

        return new StorageInfo(
            paths.Home,
            home,
            [.. parts.Where(p => p.Bytes > 0).Select(p => new StoragePart(p.Name, p.Bytes))],
            [.. Volumes([("data", paths.Home), .. runtime.Options.Library.Roots.Select(r => ("library", r))])],
            stats is null ? null : new CacheInfo(stats.MetadataRows, stats.FeatureRows, stats.Snapshots, DbFiles()),
            DateTimeOffset.UtcNow);
    }

    private static IEnumerable<VolumeInfo> Volumes(IEnumerable<(string Role, string Path)> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (role, path) in paths)
        {
            DriveInfo drive;
            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                drive = new DriveInfo(path);
                if (!seen.Add(role + drive.Name))
                {
                    continue;
                }

                _ = drive.TotalSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            yield return new VolumeInfo(role, drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace);
        }
    }

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static long Size(string file)
    {
        try
        {
            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(directory).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
