using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.External.Tests;

/// <summary>A fact that is skipped when no python3 interpreter is available.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (TestEnvironment.Python is null)
        {
            Skip = "python3 is not available";
        }
    }
}

/// <summary>A theory that is skipped when no python3 interpreter is available.</summary>
public sealed class PythonTheoryAttribute : TheoryAttribute
{
    public PythonTheoryAttribute()
    {
        if (TestEnvironment.Python is null)
        {
            Skip = "python3 is not available";
        }
    }
}

internal static class TestEnvironment
{
    private static readonly Lazy<string?> PythonPath = new(FindPython);
    private static readonly Lazy<string> ExamplesPath = new(FindExamples);

    public static string? Python => PythonPath.Value;

    public static string Examples => ExamplesPath.Value;

    public static string MockScript => Path.Combine(AppContext.BaseDirectory, "Scripts", "mock_plugin.py");

    public static string CompositionExample => Path.Combine(Examples, "python-composition", "plugin.py");

    public static string MarkdownExample => Path.Combine(Examples, "python-markdown-report", "plugin.py");

    public static GenreTaxonomy Taxonomy { get; } = new()
    {
        Genres =
        [
            new GenreDefinition { Id = "night", Name = "Night" },
            new GenreDefinition { Id = "portrait", Name = "Portrait" },
            new GenreDefinition { Id = "landscape", Name = "Landscape" },
        ],
    };

    public static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindPython()
    {
        if (File.Exists("/usr/bin/python3"))
        {
            return "/usr/bin/python3";
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "python3", "python3.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string FindExamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "examples");
            if (File.Exists(Path.Combine(candidate, "python-composition", "plugin.py")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate the examples/ directory above " + AppContext.BaseDirectory);
    }
}

/// <summary>Per-test scratch directories under the test output folder (never the system temp dir).</summary>
internal sealed class TestScope : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    public TestScope()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "test-state", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Context = new PluginContext
        {
            DataDirectory = Directory.CreateDirectory(Path.Combine(Root, "data")).FullName,
            ModelsDirectory = Directory.CreateDirectory(Path.Combine(Root, "models")).FullName,
            StateDirectory = Directory.CreateDirectory(Path.Combine(Root, "state")).FullName,
            LoggerFactory = Logs,
            Taxonomy = TestEnvironment.Taxonomy,
        };
    }

    public string Root { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public PluginContext Context { get; }

    public string FrameTempDirectory => Path.Combine(Context.StateDirectory, "external-tmp");

    public static ExternalPluginOptions Mock(ComponentKind kind, IReadOnlyDictionary<string, string>? environment = null, string id = "ext.mock") => new()
    {
        Id = id,
        Kind = kind,
        Command = TestEnvironment.Python,
        Args = ["-u", TestEnvironment.MockScript],
        Environment = environment ?? new Dictionary<string, string>(),
        TimeoutSeconds = 10,
        ShutdownGrace = TimeSpan.FromMilliseconds(500),
        RestartBackoff = TimeSpan.FromMilliseconds(20),
    };

    public async Task<T> CreateAsync<T>(ExternalPluginOptions options)
        where T : class
    {
        var plugin = await ExternalPlugins.CreateAsync(options, Context);
        _owned.Add((IAsyncDisposable)plugin);
        return Assert.IsAssignableFrom<T>(plugin);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _owned)
        {
            await plugin.DisposeAsync();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Frames
{
    public static Frame Solid(int width, int height, byte r, byte g, byte b, int? sourceWidth = null, int? sourceHeight = null)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }

        return new Frame(width, height, rgb, sourceWidth ?? width, sourceHeight ?? height);
    }

    public static AnalysisContext Context(string captureId) => new() { CaptureId = captureId, SourcePath = "/photos/" + captureId + ".jpg" };
}

internal sealed record LogEntry(LogLevel Level, string Category, string Message);

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    /// <summary>Waits for the mock's "mock initialized pid=N" stderr line and returns N.</summary>
    public async Task<int> WaitForPidAsync()
    {
        const string marker = "mock initialized pid=";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var line = Entries.Select(e => e.Message).FirstOrDefault(m => m.Contains(marker, StringComparison.Ordinal));
            if (line is not null)
            {
                return int.Parse(line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..], System.Globalization.CultureInfo.InvariantCulture);
            }

            Assert.True(DateTime.UtcNow < deadline, "mock never reported its pid");
            await Task.Delay(20);
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
    }
}
