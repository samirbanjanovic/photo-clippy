using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.External.Tests;

public sealed class TransportTests
{
    [PythonFact]
    public async Task ParallelCalls_AreMultiplexedOverOneProcess_AndThrottled()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition) with { MaxConcurrency = 8 });
        var plugin = (IExternalPlugin)analyzer;
        var pid = plugin.ProcessId;

        // Later requests sleep less, so responses arrive out of order and must be matched by id.
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            analyzer.AnalyzeAsync(Frames.Solid(8, 4, (byte)i, 0, 0), Frames.Context($"delay:{(32 - i) * 3}:c{i}")).AsTask()));

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i, results[i].Extras["mock.index"]);
            Assert.Equal(i, results[i].Extras["mock.meanR"]);
            Assert.Equal(1, results[i].Extras["mock.byPath"]);
        }

        var maxInflight = results.Max(r => r.Extras["mock.maxInflight"]);
        Assert.InRange(maxInflight, 2, 8);
        Assert.Equal(pid, plugin.ProcessId);
        Assert.Equal(0, plugin.RestartCount);
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task Frames_AreDownscaledToMaxEdge_AndTempFilesRemoved()
    {
        await using var scope = new TestScope();
        var stale = Path.Combine(scope.FrameTempDirectory, "old-frame.ppm");
        Directory.CreateDirectory(scope.FrameTempDirectory);
        await File.WriteAllTextAsync(stale, "P6");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        var env = new Dictionary<string, string> { ["MOCK_MAX_EDGE"] = "32" };
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition, env));
        Assert.False(File.Exists(stale));
        Assert.Equal(32, analyzer.Requirements.MaxEdge);

        var features = await analyzer.AnalyzeAsync(Frames.Solid(128, 64, 120, 0, 0), Frames.Context("c1"));
        Assert.Equal(32, features.Extras["mock.width"]);
        Assert.Equal(16, features.Extras["mock.height"]);
        Assert.Equal(120, features.Extras["mock.meanR"], 6);
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task SlowRequest_TimesOut_AndLaterRequestsStillWork()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition) with { TimeoutSeconds = 1 });

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ExternalPluginException>(() =>
            analyzer.AnalyzeAsync(Frames.Solid(4, 4, 1, 1, 1), Frames.Context("sleep:3")).AsTask());
        watch.Stop();

        Assert.Equal(ExternalPluginErrorCodes.Timeout, error.Code);
        Assert.False(error.IsRemote);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 2.5);

        var ok = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 7, 1, 1), Frames.Context("c5"));
        Assert.Equal(5, ok.Extras["mock.index"]);
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task CrashingProcess_IsRestarted_ThenDegradesAfterMaxRestarts()
    {
        await using var scope = new TestScope();
        var env = new Dictionary<string, string> { ["MOCK_CRASH_AFTER"] = "2" };
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition, env) with { MaxRestarts = 2 });
        var plugin = (IExternalPlugin)analyzer;
        var frame = Frames.Solid(4, 4, 9, 9, 9);
        var pids = new HashSet<int>();

        for (var round = 0; round <= 2; round++)
        {
            Assert.Equal(1, (await analyzer.AnalyzeAsync(frame, Frames.Context("c1"))).Extras["mock.index"]);
            Assert.Equal(2, (await analyzer.AnalyzeAsync(frame, Frames.Context("c2"))).Extras["mock.index"]);
            pids.Add(plugin.ProcessId!.Value);

            var crash = await Assert.ThrowsAsync<ExternalPluginException>(() => analyzer.AnalyzeAsync(frame, Frames.Context("c3")).AsTask());
            Assert.Equal(ExternalPluginErrorCodes.ProcessExited, crash.Code);
            Assert.Equal(Math.Min(round + 1, 2), plugin.RestartCount);
        }

        Assert.Equal(3, pids.Count);
        Assert.True(plugin.IsDegraded);
        Assert.Null(plugin.ProcessId);

        var watch = Stopwatch.StartNew();
        var unavailable = await Assert.ThrowsAsync<ExternalPluginException>(() => analyzer.AnalyzeAsync(frame, Frames.Context("c1")).AsTask());
        Assert.Equal(ExternalPluginErrorCodes.Unavailable, unavailable.Code);
        Assert.True(watch.ElapsedMilliseconds < 500, "degraded plugins must fail fast");

        var health = await analyzer.CheckHealthAsync();
        Assert.False(health.Healthy);
        Assert.Contains("degraded", health.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scope.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("degraded", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task MalformedOutputLines_AreLoggedAndSkipped()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition));

        var features = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 3, 3, 3), Frames.Context("garbage"));
        Assert.Equal(3, features.Extras["mock.meanR"]);

        var warnings = scope.Logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        Assert.Contains(warnings, m => m.Contains("malformed line", StringComparison.Ordinal) && m.Contains("this is not json", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("non-object", StringComparison.Ordinal));

        var next = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 3, 3, 3), Frames.Context("c9"));
        Assert.Equal(9, next.Extras["mock.index"]);
        Assert.Equal(0, ((IExternalPlugin)analyzer).RestartCount);
    }

    [PythonFact]
    public async Task PluginErrors_YieldEmptyFeatures_AndAreLogged()
    {
        await using var scope = new TestScope();
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition));

        var failed = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 3, 3, 3), Frames.Context("error"));
        Assert.Empty(failed.Extras);
        Assert.Null(failed.Genres);

        var empty = await analyzer.AnalyzeAsync(Frames.Solid(4, 4, 3, 3, 3), Frames.Context("noresult"));
        Assert.Empty(empty.Extras);

        var warnings = scope.Logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        Assert.Contains(warnings, m => m.Contains("mock failure", StringComparison.Ordinal) && m.Contains("-32000", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("neither 'result' nor 'error'", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task Stderr_IsForwardedToTheLogger()
    {
        await using var scope = new TestScope();
        await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!scope.Logs.Entries.Any(e => e.Message.Contains("mock stderr warning", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var entries = scope.Logs.Entries;
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Category == "PhotoClippy.Plugins.External.ext.mock" && e.Message.Contains("mock stderr warning", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Level == LogLevel.Information && e.Message.Contains("mock initialized", StringComparison.Ordinal));
    }

    [PythonFact]
    public async Task DisposeAsync_KillsAProcessThatIgnoresShutdown()
    {
        await using var scope = new TestScope();
        var env = new Dictionary<string, string> { ["MOCK_IGNORE_SHUTDOWN"] = "1" };
        var options = TestScope.Mock(ComponentKind.Composition, env) with { ShutdownGrace = TimeSpan.FromMilliseconds(200) };
        var plugin = (IExternalPlugin)await ExternalPlugins.CreateAsync(options, scope.Context);
        var pid = plugin.ProcessId!.Value;
        Assert.True(TestEnvironment.IsRunning(pid));

        var watch = Stopwatch.StartNew();
        await plugin.DisposeAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"dispose took {watch.Elapsed}");
        Assert.False(TestEnvironment.IsRunning(pid));
        Assert.Null(plugin.ProcessId);

        var health = await plugin.CheckHealthAsync();
        Assert.False(health.Healthy);
        await Assert.ThrowsAnyAsync<Exception>(() => ((ICompositionAnalyzer)plugin).AnalyzeAsync(Frames.Solid(2, 2, 0, 0, 0), Frames.Context("c1")).AsTask());
    }

    [PythonFact]
    public async Task Dispose_StopsTheProcessGracefully()
    {
        await using var scope = new TestScope();
        var plugin = (IExternalPlugin)await ExternalPlugins.CreateAsync(TestScope.Mock(ComponentKind.Color), scope.Context);
        var pid = plugin.ProcessId!.Value;

        plugin.Dispose();
        plugin.Dispose();

        Assert.False(TestEnvironment.IsRunning(pid));
    }

    [PythonFact]
    public async Task MissingExecutable_FailsWithStartFailed()
    {
        await using var scope = new TestScope();
        var options = TestScope.Mock(ComponentKind.Composition) with { Command = Path.Combine(scope.Root, "no-such-binary") };

        var error = await Assert.ThrowsAsync<ExternalPluginException>(() => ExternalPlugins.CreateAsync(options, scope.Context));
        Assert.Equal(ExternalPluginErrorCodes.StartFailed, error.Code);
    }

    [PythonFact]
    public async Task UnresponsiveInitialize_TimesOut()
    {
        await using var scope = new TestScope();
        var options = TestScope.Mock(ComponentKind.Composition) with
        {
            Args = ["-u", "-c", "import os, sys, time\nsys.stderr.write('info: mock initialized pid=%d\\n' % os.getpid())\nsys.stdin.readline()\ntime.sleep(30)"],
            TimeoutSeconds = 1,
        };

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ExternalPluginException>(() => ExternalPlugins.CreateAsync(options, scope.Context));

        Assert.Equal(ExternalPluginErrorCodes.Timeout, error.Code);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 3);
        Assert.False(TestEnvironment.IsRunning(await scope.Logs.WaitForPidAsync()));
    }

    [PythonFact]
    public async Task ProcessExitingDuringInitialize_FailsCreate()
    {
        await using var scope = new TestScope();
        var options = TestScope.Mock(ComponentKind.Composition) with { Args = ["-c", "import sys; sys.exit(4)"] };

        var error = await Assert.ThrowsAsync<ExternalPluginException>(() => ExternalPlugins.CreateAsync(options, scope.Context));
        Assert.Equal(ExternalPluginErrorCodes.ProcessExited, error.Code);
    }
}
