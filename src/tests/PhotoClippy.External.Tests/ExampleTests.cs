using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.External.Tests;

public sealed class ExampleTests
{
    [PythonFact]
    public async Task CompositionExample_OverStdio_AnalyzesFramesAndReportsHealth()
    {
        await using var scope = new TestScope();
        var options = new ExternalPluginOptions
        {
            Id = "ext.python-composition",
            Kind = ComponentKind.Composition,
            Command = TestEnvironment.Python,
            Args = [TestEnvironment.CompositionExample],
            Settings = JsonDocument.Parse("""{"nightPrior":0.3}""").RootElement,
        };
        var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(options);

        Assert.Equal("ext.python-composition", analyzer.Manifest.Id);
        Assert.Equal(ComponentKind.Composition, analyzer.Manifest.Kind);
        Assert.Equal(256, analyzer.Requirements.MaxEdge);

        var dark = await analyzer.AnalyzeAsync(Frames.Solid(60, 40, 10, 10, 10, 6000, 4000), Frames.Context("dark"));
        Assert.Equal(0.3, dark.Genres!["night"], 6);
        Assert.Equal(10 / 255.0, dark.Extras["ext.python.meanLuma"], 3);
        Assert.Equal(1.5, dark.Extras["ext.python.aspect"], 6);
        Assert.Equal(0, dark.Extras["ext.python.brightCorner"], 6);

        var bright = await analyzer.AnalyzeAsync(Frames.Solid(1024, 512, 200, 200, 200), Frames.Context("bright"));
        Assert.True(bright.Genres is null || bright.Genres.Count == 0);
        Assert.Equal(200 / 255.0, bright.Extras["ext.python.meanLuma"], 3);

        var health = await analyzer.CheckHealthAsync();
        Assert.True(health.Healthy, health.Message);
        Assert.Empty(Directory.EnumerateFiles(scope.FrameTempDirectory));
    }

    [PythonFact]
    public async Task CompositionExample_OverHttp_AnalyzesFramesAndReportsHealth()
    {
        await using var scope = new TestScope();
        var port = TestEnvironment.FreeTcpPort();
        using var server = Process.Start(new ProcessStartInfo(TestEnvironment.Python!)
        {
            ArgumentList = { TestEnvironment.CompositionExample, "--http", port.ToString(CultureInfo.InvariantCulture) },
            UseShellExecute = false,
            RedirectStandardError = true,
        })!;
        server.ErrorDataReceived += (_, _) => { };
        server.BeginErrorReadLine();
        try
        {
            await WaitForPortAsync(port);
            var options = new ExternalPluginOptions
            {
                Id = "ext.python-composition",
                Kind = ComponentKind.Composition,
                Transport = "http",
                Url = $"http://127.0.0.1:{port}/",
                TimeoutSeconds = 10,
            };
            var analyzer = await scope.CreateAsync<ICompositionAnalyzer>(options);
            Assert.Equal("ext.python-composition", analyzer.Manifest.Id);
            Assert.Null(((IExternalPlugin)analyzer).ProcessId);

            var dark = await analyzer.AnalyzeAsync(Frames.Solid(40, 30, 5, 5, 5), Frames.Context("dark"));
            Assert.Equal(0.2, dark.Genres!["night"], 6);
            Assert.Equal(5 / 255.0, dark.Extras["ext.python.meanLuma"], 3);

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
                analyzer.AnalyzeAsync(Frames.Solid(32, 32, (byte)(i * 30), 0, 0), Frames.Context("c" + i)).AsTask()));
            Assert.All(results, r => Assert.True(r.Extras.ContainsKey("ext.python.brightCorner")));

            Assert.True((await analyzer.CheckHealthAsync()).Healthy);
            Assert.False(Directory.Exists(scope.FrameTempDirectory) && Directory.EnumerateFiles(scope.FrameTempDirectory).Any());
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }

    [PythonFact]
    public async Task MarkdownReportExample_WritesMarkdownFile()
    {
        await using var scope = new TestScope();
        var options = new ExternalPluginOptions
        {
            Id = "ext.python-markdown-report",
            Kind = ComponentKind.Report,
            Command = TestEnvironment.Python,
            Args = [TestEnvironment.MarkdownExample],
        };
        var renderer = await scope.CreateAsync<IReportRenderer>(options);
        Assert.Equal("md", renderer.Format);

        var model = new ReportModel
        {
            Run = new RunInfo { RunId = "run-42", StartedUtc = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero) },
            Profile = new ShootingProfile { Library = new LibrarySummary { FilesSeen = 1234, Captures = 1000, Roots = ["/photos"] } },
            Title = "Test habits",
        };
        var result = await renderer.RenderAsync(model, new RenderTarget { OutputDirectory = Path.Combine(scope.Root, "out"), BaseName = "habits" });

        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(scope.Root, "out", "habits.md"), file);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("# Test habits", text, StringComparison.Ordinal);
        Assert.Contains("## Library", text, StringComparison.Ordinal);
        Assert.Contains("run-42", text, StringComparison.Ordinal);
        Assert.Contains("1234", text, StringComparison.Ordinal);
        Assert.Contains("|---", text, StringComparison.Ordinal);
        Assert.True((await renderer.CheckHealthAsync()).Healthy);
    }

    private static async Task WaitForPortAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }
}
