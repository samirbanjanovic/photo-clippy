using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.External.Tests;

public sealed class ManifestAndOptionsTests
{
    [PythonTheory]
    [InlineData("MOCK_KIND", "color", "kind")]
    [InlineData("MOCK_CONTRACT", "2.0", "contract")]
    [InlineData("MOCK_ID", "builtin.thirds", "must equal")]
    public async Task InvalidManifest_IsRejected_AndProcessStopped(string variable, string value, string expected)
    {
        await using var scope = new TestScope();
        var env = new Dictionary<string, string> { [variable] = value };

        var error = await Assert.ThrowsAsync<ExternalPluginException>(() =>
            ExternalPlugins.CreateAsync(TestScope.Mock(ComponentKind.Composition, env), scope.Context));

        Assert.Equal(ExternalPluginErrorCodes.ManifestRejected, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal("ext.mock", error.PluginId);

        var pid = await scope.Logs.WaitForPidAsync();
        Assert.False(TestEnvironment.IsRunning(pid), "a rejected plugin process must be stopped");
    }

    [PythonFact]
    public async Task ManifestId_MayDifferWhenPrefixedExt()
    {
        await using var scope = new TestScope();
        var env = new Dictionary<string, string> { ["MOCK_ID"] = "ext.other", ["MOCK_CONTRACT"] = "1.7" };

        var plugin = await scope.CreateAsync<ICompositionAnalyzer>(TestScope.Mock(ComponentKind.Composition, env));

        Assert.Equal("ext.other", plugin.Manifest.Id);
        Assert.Equal(ComponentKind.Composition, plugin.Manifest.Kind);
        Assert.Equal("1.7", plugin.Manifest.ContractVersion);
    }

    [PythonFact]
    public async Task CreateAsync_ReturnsAdapterForEachKind()
    {
        await using var scope = new TestScope();

        Assert.IsAssignableFrom<IMetadataExtractor>(await scope.CreateAsync<IPlugin>(TestScope.Mock(ComponentKind.Metadata)));
        Assert.IsAssignableFrom<ICompositionAnalyzer>(await scope.CreateAsync<IPlugin>(TestScope.Mock(ComponentKind.Composition)));
        Assert.IsAssignableFrom<IColorAnalyzer>(await scope.CreateAsync<IPlugin>(TestScope.Mock(ComponentKind.Color)));
        Assert.IsAssignableFrom<IRecommender>(await scope.CreateAsync<IPlugin>(TestScope.Mock(ComponentKind.Recommendation)));
        var report = await scope.CreateAsync<IPlugin>(TestScope.Mock(ComponentKind.Report));
        Assert.IsAssignableFrom<IReportRenderer>(report);
        Assert.IsAssignableFrom<IAsyncDisposable>(report);
        Assert.IsAssignableFrom<IDisposable>(report);
    }

    [Fact]
    public void Options_BindFromConfigJson()
    {
        const string json = """
            {
              "id": "ext.python-composition",
              "kind": "composition",
              "transport": "stdio",
              "command": "python3",
              "args": ["examples/python-composition/plugin.py", "--verbose"],
              "workingDirectory": "..",
              "environment": { "PYTHONUNBUFFERED": "1" },
              "timeoutSeconds": 15,
              "maxConcurrency": 2,
              "maxRestarts": 5,
              "settings": { "nightPrior": 0.25 },
              "weight": 0.3
            }
            """;

        var options = JsonSerializer.Deserialize<ExternalPluginOptions>(json, ContractJson.Options)!;

        Assert.Equal("ext.python-composition", options.Id);
        Assert.Equal(ComponentKind.Composition, options.Kind);
        Assert.Equal(ExternalPluginOptions.StdioTransport, options.Transport);
        Assert.Equal("python3", options.Command);
        Assert.Equal(["examples/python-composition/plugin.py", "--verbose"], options.Args);
        Assert.Equal("..", options.WorkingDirectory);
        Assert.Equal("1", options.Environment["PYTHONUNBUFFERED"]);
        Assert.Equal(15, options.TimeoutSeconds);
        Assert.Equal(2, options.MaxConcurrency);
        Assert.Equal(5, options.MaxRestarts);
        Assert.Equal(0.25, options.Settings.GetProperty("nightPrior").GetDouble());
        Assert.Equal(0.3, options.Weight);
    }

    [Fact]
    public void Options_HaveDocumentedDefaults()
    {
        var options = JsonSerializer.Deserialize<ExternalPluginOptions>("""{"id":"ext.http","kind":"color","transport":"http","url":"http://127.0.0.1:7070/"}""", ContractJson.Options)!;

        Assert.Equal(ComponentKind.Color, options.Kind);
        Assert.Equal("http://127.0.0.1:7070/", options.Url);
        Assert.Null(options.Command);
        Assert.Empty(options.Args);
        Assert.Empty(options.Environment);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(3, options.MaxRestarts);
        Assert.Equal(1.0, options.Weight);
        Assert.Equal(JsonValueKind.Undefined, options.Settings.ValueKind);
    }

    public static TheoryData<string> InvalidOptions() => new()
    {
        """{"id":"ext.a","kind":"composition"}""",
        """{"id":"","kind":"composition","command":"python3"}""",
        """{"id":"ext.a","kind":"composition","transport":"http"}""",
        """{"id":"ext.a","kind":"composition","transport":"http","url":"ftp://host/x"}""",
        """{"id":"ext.a","kind":"composition","transport":"pipe","command":"python3"}""",
        """{"id":"ext.a","kind":"composition","command":"python3","timeoutSeconds":0}""",
        """{"id":"ext.a","kind":"composition","command":"python3","maxConcurrency":0}""",
        """{"id":"ext.a","kind":"composition","command":"python3","maxRestarts":-1}""",
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task InvalidOptions_AreRejectedBeforeStarting(string json)
    {
        await using var scope = new TestScope();
        var options = JsonSerializer.Deserialize<ExternalPluginOptions>(json, ContractJson.Options)!;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => ExternalPlugins.CreateAsync(options, scope.Context));
    }

    [Fact]
    public async Task UndefinedKind_IsRejected()
    {
        await using var scope = new TestScope();
        var options = new ExternalPluginOptions { Id = "ext.a", Kind = (ComponentKind)99, Command = "python3" };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => ExternalPlugins.CreateAsync(options, scope.Context));
    }
}
