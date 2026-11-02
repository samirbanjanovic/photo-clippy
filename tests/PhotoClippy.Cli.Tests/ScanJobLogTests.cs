using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Cli.Serve;

namespace PhotoClippy.Cli.Tests;

public sealed class ScanJobLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pc-jobs-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "state", "scan-jobs.json");

    [Fact]
    public void FinishedJobsSurviveARestartButRunningOnesDoNot()
    {
        var log = new ScanJobLog(File, NullLogger.Instance);
        var started = DateTimeOffset.UtcNow;
        log.Upsert(Job("a", "running", started));
        Assert.False(System.IO.File.Exists(File));

        log.Upsert(Job("a", "completed", started) with
        {
            FinishedUtc = started.AddSeconds(3),
            Stages = [new StageStat { Stage = "metadata", Seconds = 2, Completed = 10, Total = 10, Cached = 8 }],
        });
        log.Upsert(Job("b", "running", started.AddMinutes(1)));

        var reloaded = new ScanJobLog(File, NullLogger.Instance);
        var job = Assert.Single(reloaded.Recent());
        Assert.Equal("a", job.Id);
        Assert.Equal(3, job.Seconds);
        Assert.Equal(0.8, job.CacheHitShare);
        Assert.Equal(1, job.Stages[0].ItemsPerSecond);
    }

    [Fact]
    public void KeepsTheNewestJobsUpToCapacity()
    {
        var log = new ScanJobLog(File, NullLogger.Instance);
        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < ScanJobLog.Capacity + 5; i++)
        {
            log.Upsert(Job($"j{i}", "completed", started.AddMinutes(i)));
        }

        var recent = log.Recent();
        Assert.Equal(ScanJobLog.Capacity, recent.Count);
        Assert.Equal($"j{ScanJobLog.Capacity + 4}", recent[0].Id);
        Assert.Equal("j5", recent[^1].Id);
        Assert.Equal(recent[0], log.Last);
    }

    [Fact]
    public void UnreadableHistoryStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ not json");

        Assert.Empty(new ScanJobLog(File, NullLogger.Instance).Recent());
    }

    private static ScanJob Job(string id, string state, DateTimeOffset started) =>
        new() { Id = id, Trigger = ScanTriggers.Web, StartedUtc = started, State = state };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
