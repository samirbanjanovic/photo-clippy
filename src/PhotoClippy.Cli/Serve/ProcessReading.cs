using System.Diagnostics;
using System.Globalization;

namespace PhotoClippy.Cli.Serve;

/// <summary>Cumulative process counters at one instant; deltas between two readings give rates.</summary>
internal readonly record struct ProcessReading(
    long TimestampTicks,
    TimeSpan CpuTime,
    long WorkingSet,
    long PrivateBytes,
    long GcHeap,
    long Allocated,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan GcPause,
    int Threads,
    int PoolThreads,
    long PoolQueue,
    long PoolCompleted,
    long? StorageRead,
    long? StorageWrite)
{
    public static ProcessReading Take()
    {
        using var process = Process.GetCurrentProcess();
        var (read, write) = LinuxIo.Read();
        return new ProcessReading(
            Stopwatch.GetTimestamp(),
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            GC.GetTotalAllocatedBytes(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            process.Threads.Count,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount,
            read,
            write);
    }
}

/// <summary>Storage I/O of this process from <c>/proc/self/io</c> (Linux only; bytes that reached the block layer).</summary>
internal static class LinuxIo
{
    private static bool _unavailable = !OperatingSystem.IsLinux();

    public static (long? Read, long? Write) Read()
    {
        if (_unavailable)
        {
            return (null, null);
        }

        try
        {
            long? read = null;
            long? write = null;
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                {
                    read = long.Parse(line.AsSpan(11).Trim(), CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                {
                    write = long.Parse(line.AsSpan(12).Trim(), CultureInfo.InvariantCulture);
                }
            }

            return (read, write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _unavailable = true;
            return (null, null);
        }
    }
}

/// <summary>Container limits from cgroup v2 (or v1) when running on Linux; null elsewhere or when unlimited.</summary>
internal static class Cgroup
{
    public static long? MemoryLimitBytes() =>
        ReadLimit("/sys/fs/cgroup/memory.max") ?? ReadLimit("/sys/fs/cgroup/memory/memory.limit_in_bytes", unlimitedAbove: 1L << 60);

    public static long? MemoryCurrentBytes() =>
        ReadLimit("/sys/fs/cgroup/memory.current") ?? ReadLimit("/sys/fs/cgroup/memory/memory.usage_in_bytes", unlimitedAbove: 1L << 60);

    /// <summary>CPU quota in cores (e.g. 2.5), from cpu.max or cfs_quota/cfs_period.</summary>
    public static double? CpuLimitCores()
    {
        try
        {
            if (File.Exists("/sys/fs/cgroup/cpu.max"))
            {
                var parts = File.ReadAllText("/sys/fs/cgroup/cpu.max").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0] != "max" &&
                    double.TryParse(parts[0], CultureInfo.InvariantCulture, out var quota) &&
                    double.TryParse(parts[1], CultureInfo.InvariantCulture, out var period) && period > 0)
                {
                    return quota / period;
                }

                return null;
            }

            var q = ReadLimit("/sys/fs/cgroup/cpu/cpu.cfs_quota_us");
            var p = ReadLimit("/sys/fs/cgroup/cpu/cpu.cfs_period_us");
            return q is > 0 && p is > 0 ? q.Value / (double)p.Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool InContainer =>
        File.Exists("/.dockerenv") || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static long? ReadLimit(string path, long unlimitedAbove = long.MaxValue)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 && value < unlimitedAbove ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
