using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// Configuration of one out-of-process plugin (an entry of <c>plugins:external</c>). Bindable from JSON
/// with <see cref="ContractJson.Options"/>.
/// </summary>
public sealed record ExternalPluginOptions
{
    public const string StdioTransport = "stdio";
    public const string HttpTransport = "http";

    /// <summary>Configured plugin id, e.g. <c>ext.python-composition</c>.</summary>
    public required string Id { get; init; }

    public required ComponentKind Kind { get; init; }

    /// <summary><c>stdio</c> (spawn <see cref="Command"/>) or <c>http</c> (POST to <see cref="Url"/>).</summary>
    public string Transport { get; init; } = StdioTransport;

    /// <summary>Executable for the stdio transport; resolved via PATH or relative to <see cref="WorkingDirectory"/>.</summary>
    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Working directory of the child process; defaults to the host's current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables for the child process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>JSON-RPC endpoint for the http transport.</summary>
    public string? Url { get; init; }

    /// <summary>Per-request timeout (also applies to <c>initialize</c>).</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Maximum number of requests in flight at once.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Lifetime budget of automatic restarts after the child process dies (stdio only).</summary>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>Plugin-specific settings passed verbatim in <c>initialize</c>. Falls back to <see cref="PluginContext.Settings"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Settings { get; init; }

    /// <summary>Informational; used by the host for genre fusion.</summary>
    public double Weight { get; init; } = 1.0;

    internal TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan RestartBackoff { get; init; } = TimeSpan.FromMilliseconds(250);
}
