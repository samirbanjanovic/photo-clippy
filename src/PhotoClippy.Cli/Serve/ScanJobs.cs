using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Cli.Serve;

/// <summary>What started a scan.</summary>
internal static class ScanTriggers
{
    public const string Startup = "startup";
    public const string Web = "web";
    public const string Api = "api";
    public const string Schedule = "schedule";
}

/// <summary>Timing and throughput of one pipeline stage within a scan.</summary>
internal sealed record StageStat
{
    public required string Stage { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public double Seconds { get; init; }
    public long Completed { get; init; }
    public long? Total { get; init; }
    /// <summary>Items served from the analysis cache (metadata and frames stages).</summary>
    public long Cached { get; init; }

    public double? ItemsPerSecond => Seconds > 0.05 && Completed - Cached > 0 ? (Completed - Cached) / Seconds : null;
}

/// <summary>One scan as seen by the daemon: trigger, outcome, stage timings and the resources it used.</summary>
internal sealed record ScanJob
{
    public required string Id { get; init; }
    public required string Trigger { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public string State { get; init; } = "running";
    public string? Error { get; init; }
    public string? RunId { get; init; }
    public IReadOnlyList<StageStat> Stages { get; init; } = [];
    public int? FilesSeen { get; init; }
    public long? Bytes { get; init; }
    public int? Captures { get; init; }
    public int? CapturesInScope { get; init; }
    /// <summary>Files whose metadata had to be read (not cached).</summary>
    public long? FilesRead { get; init; }
    /// <summary>Frames whose pixels had to be analysed (not cached).</summary>
    public long? FramesAnalysed { get; init; }
    public double? CpuSeconds { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public long? AllocatedBytes { get; init; }
    public long? DiskReadBytes { get; init; }
    public int Gen2Collections { get; init; }

    public double? Seconds => FinishedUtc is { } f ? (f - StartedUtc).TotalSeconds : null;

    /// <summary>Share of discovered files whose metadata came from the cache.</summary>
    public double? CacheHitShare
    {
        get
        {
            var metadata = Stages.FirstOrDefault(s => s.Stage == "metadata");
            return metadata is { Total: > 0 } m ? m.Cached / (double)m.Total.Value : null;
        }
    }
}

/// <summary>Bounded scan history kept in memory and persisted to <c>&lt;state&gt;/scan-jobs.json</c>.</summary>
internal sealed class ScanJobLog(string path, ILogger logger)
{
    public const int Capacity = 100;
    private readonly Lock _lock = new();
    private readonly List<ScanJob> _jobs = Load(path, logger);

    public string Path { get; } = path;

    public IReadOnlyList<ScanJob> Recent(int count = Capacity)
    {
        lock (_lock)
        {
            return [.. _jobs.TakeLast(count).Reverse()];
        }
    }

    public ScanJob? Last
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Count == 0 ? null : _jobs[^1];
            }
        }
    }

    /// <summary>Adds or replaces (by id) a job; finished jobs are written to disk.</summary>
    public void Upsert(ScanJob job)
    {
        ScanJob[] copy;
        lock (_lock)
        {
            var index = _jobs.FindLastIndex(j => j.Id == job.Id);
            if (index >= 0)
            {
                _jobs[index] = job;
            }
            else
            {
                _jobs.Add(job);
                if (_jobs.Count > Capacity)
                {
                    _jobs.RemoveRange(0, _jobs.Count - Capacity);
                }
            }

            if (job.State == "running")
            {
                return;
            }

            copy = [.. _jobs.Where(j => j.State != "running")];
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, ContractJson.Options));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not save scan history to {Path}: {Error}", Path, ex.Message);
        }
    }

    private static List<ScanJob> Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                var jobs = JsonSerializer.Deserialize<List<ScanJob>>(File.ReadAllText(path), ContractJson.Options) ?? [];
                return [.. jobs.Where(j => j.State != "running").TakeLast(Capacity)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Ignoring unreadable scan history {Path}: {Error}", path, ex.Message);
        }

        return [];
    }
}
