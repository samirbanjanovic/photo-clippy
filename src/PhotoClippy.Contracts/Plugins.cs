using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhotoClippy.Contracts;

public static class ContractInfo
{
    /// <summary>Semantic version of the plugin contract. Minor bumps are additive; major bumps break.</summary>
    public const string Version = "1.2";
}

public enum ComponentKind
{
    Metadata,
    Composition,
    Color,
    Recommendation,
    Report,
    /// <summary>Sign-in for the web UI (<see cref="IAuthProvider"/>). In-process only.</summary>
    Auth,
    /// <summary>Searchable lens database for picking candidates (<see cref="ILensCatalog"/>).</summary>
    LensCatalog,
}

public sealed record PluginManifest
{
    /// <summary>Stable identifier, e.g. <c>builtin.color</c> or <c>ext.my-plugin</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required ComponentKind Kind { get; init; }
    public string ContractVersion { get; init; } = ContractInfo.Version;
    public string? Description { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Changes whenever output-affecting settings or models change. Combined with <see cref="Id"/> and
    /// <see cref="Version"/> it forms the cache key for stored features.
    /// </summary>
    public string? Fingerprint { get; init; }

    public string CacheKey => Fingerprint is null ? $"{Id}@{Version}" : $"{Id}@{Version}#{Fingerprint}";
}

/// <summary>A downloadable model file a plugin needs, stored at ModelsDirectory/RelativePath.</summary>
public sealed record ModelAsset
{
    public required string RelativePath { get; init; }
    public required Uri Source { get; init; }
    public long? SizeBytes { get; init; }
    /// <summary>Lower-case hex SHA-256; verified after download when present.</summary>
    public string? Sha256 { get; init; }
    public string? License { get; init; }
}

/// <summary>Implemented by plugins that need model files. The host downloads them on explicit request only.</summary>
public interface IRequiresModels
{
    IReadOnlyList<ModelAsset> Models { get; }
}

public sealed record PluginHealth(bool Healthy, string? Message = null)
{
    public static PluginHealth Ok(string? message = null) => new(true, message);
    public static PluginHealth Unhealthy(string message) => new(false, message);
}

/// <summary>Base contract for every component. Implementations must be thread-safe.</summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }
    ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Entry point used by the host to create plugins. Built-in plugins and assemblies loaded from the
/// plugins directory expose one public parameterless implementation per plugin.
/// </summary>
public interface IPluginFactory
{
    string Id { get; }
    ComponentKind Kind { get; }
    IPlugin Create(PluginContext context);
}

public sealed record PluginContext
{
    /// <summary>Plugin-specific settings (the <c>plugins:&lt;id&gt;</c> config section). May be <c>Undefined</c>.</summary>
    public JsonElement Settings { get; init; }

    /// <summary>Directory with bundled read-only data (genre taxonomy, report assets).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Directory for downloaded models.</summary>
    public required string ModelsDirectory { get; init; }

    /// <summary>Writable directory for plugin state and caches.</summary>
    public required string StateDirectory { get; init; }

    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Genre taxonomy shared by analyzers, recommender and renderers.</summary>
    public required GenreTaxonomy Taxonomy { get; init; }

    public T? GetSettings<T>() where T : class =>
        Settings.ValueKind is JsonValueKind.Object ? Settings.Deserialize<T>(ContractJson.Options) : null;
}
