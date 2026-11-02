using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Core.Plugins;

/// <summary>Creates an out-of-process plugin from one <c>plugins.external[]</c> element.</summary>
public delegate Task<IPlugin> ExternalPluginActivator(JsonElement definition, PluginContext context, CancellationToken cancellationToken);

/// <summary>Instantiated, health-checked plugins for one run, in configured priority order.</summary>
public sealed class PluginSet : IAsyncDisposable
{
    private readonly List<IPlugin> _all = [];
    private readonly List<string> _notes = [];

    public List<IMetadataExtractor> Metadata { get; } = [];
    public List<ICompositionAnalyzer> Composition { get; } = [];
    public List<IColorAnalyzer> Color { get; } = [];
    public IRecommender? Recommender { get; internal set; }
    public List<IReportRenderer> Reports { get; } = [];

    /// <summary>Composition analyzer id → weight in genre fusion.</summary>
    public Dictionary<string, double> GenreWeights { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Every instantiated plugin, including externals that were created but not selected.</summary>
    public IReadOnlyList<IPlugin> All => _all;

    /// <summary>Manifests of the active (selected and healthy) plugins.</summary>
    public IReadOnlyList<PluginManifest> Manifests =>
    [
        .. Metadata.Select(p => p.Manifest),
        .. Composition.Select(p => p.Manifest),
        .. Color.Select(p => p.Manifest),
        .. (Recommender is null ? [] : new[] { Recommender.Manifest }),
        .. Reports.Select(p => p.Manifest),
    ];

    internal void Add(IPlugin plugin) => _all.Add(plugin);
    internal void Note(string note) => _notes.Add(note);

    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _all)
        {
            switch (plugin)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        _all.Clear();
    }
}

public sealed class PluginHost(PluginCatalog catalog, AppPaths paths, GenreTaxonomy taxonomy, ILoggerFactory? loggerFactory = null, ExternalPluginActivator? externalActivator = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("PhotoClippy.Plugins");

    public PluginCatalog Catalog => catalog;

    public PluginContext CreateContext(PluginOptions options, string pluginId) => new()
    {
        Settings = options.Settings.TryGetValue(pluginId, out var settings) ? settings : default,
        DataDirectory = paths.Data,
        ModelsDirectory = paths.Models,
        StateDirectory = Path.Combine(paths.State, pluginId),
        LoggerFactory = _loggerFactory,
        Taxonomy = taxonomy,
    };

    /// <summary>
    /// Instantiates the configured plugins of the requested kinds. Missing or unhealthy plugins are skipped with a note;
    /// declared external plugins are enabled automatically (recommenders only when selected).
    /// </summary>
    public async Task<PluginSet> CreateAsync(PluginOptions options, IReadOnlySet<ComponentKind> kinds, bool checkHealth = true, CancellationToken cancellationToken = default)
    {
        var set = new PluginSet();
        foreach (var (id, weight) in options.GenreWeights)
        {
            set.GenreWeights[id] = weight;
        }

        var externals = await CreateExternalsAsync(options, kinds, set, cancellationToken).ConfigureAwait(false);

        try
        {
            if (kinds.Contains(ComponentKind.Metadata))
            {
                foreach (var plugin in await ResolveAsync<IMetadataExtractor>(options, options.Metadata, ComponentKind.Metadata, externals, set, checkHealth, cancellationToken).ConfigureAwait(false))
                {
                    set.Metadata.Add(plugin);
                }
            }

            if (kinds.Contains(ComponentKind.Composition))
            {
                foreach (var plugin in await ResolveAsync<ICompositionAnalyzer>(options, options.Composition, ComponentKind.Composition, externals, set, checkHealth, cancellationToken).ConfigureAwait(false))
                {
                    set.Composition.Add(plugin);
                }
            }

            if (kinds.Contains(ComponentKind.Color))
            {
                foreach (var plugin in await ResolveAsync<IColorAnalyzer>(options, options.Color, ComponentKind.Color, externals, set, checkHealth, cancellationToken).ConfigureAwait(false))
                {
                    set.Color.Add(plugin);
                }
            }

            if (kinds.Contains(ComponentKind.Recommendation) && !string.IsNullOrWhiteSpace(options.Recommender))
            {
                var resolved = await ResolveAsync<IRecommender>(options, [options.Recommender], ComponentKind.Recommendation, externals, set, checkHealth, cancellationToken).ConfigureAwait(false);
                set.Recommender = resolved.FirstOrDefault();
            }

            if (kinds.Contains(ComponentKind.Report))
            {
                foreach (var plugin in await ResolveAsync<IReportRenderer>(options, options.Reports, ComponentKind.Report, externals, set, checkHealth, cancellationToken).ConfigureAwait(false))
                {
                    set.Reports.Add(plugin);
                }
            }

            // Externals that were created but never selected must still be disposed (they may own a child process).
            foreach (var unused in externals.Values)
            {
                set.Add(unused);
            }
        }
        catch
        {
            await set.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return set;
    }

    private async Task<Dictionary<string, IPlugin>> CreateExternalsAsync(PluginOptions options, IReadOnlySet<ComponentKind> kinds, PluginSet set, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in options.External)
        {
            if (definition.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = definition.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var kindText = definition.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !Enum.TryParse<ComponentKind>(kindText, ignoreCase: true, out var kind))
            {
                set.Note($"External plugin definition skipped: 'id' and a valid 'kind' are required ({definition.GetRawText()}).");
                continue;
            }

            if (definition.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            if (!kinds.Contains(kind) || (kind == ComponentKind.Recommendation && !string.Equals(options.Recommender, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (kind == ComponentKind.Auth)
            {
                set.Note($"{id}: auth plugins must be in-process assemblies (plugins directory), not external processes; skipped.");
                continue;
            }

            if (externalActivator is null)
            {
                set.Note($"{id}: external plugins are not supported by this host.");
                continue;
            }

            if (kind == ComponentKind.Composition && !set.GenreWeights.ContainsKey(id)
                && definition.TryGetProperty("weight", out var weight) && weight.TryGetDouble(out var w))
            {
                set.GenreWeights[id] = w;
            }

            try
            {
                var context = CreateContext(options, id) with
                {
                    Settings = definition.TryGetProperty("settings", out var s) ? s.Clone() : default,
                };
                var plugin = await externalActivator(definition, context, cancellationToken).ConfigureAwait(false);
                result[id] = plugin;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                set.Note($"{id}: external plugin failed to start: {ex.Message}");
                _logger.LogWarning(ex, "External plugin {Id} failed to start", id);
            }
        }

        return result;
    }

    private async Task<List<T>> ResolveAsync<T>(
        PluginOptions options,
        IReadOnlyList<string> ids,
        ComponentKind kind,
        Dictionary<string, IPlugin> externals,
        PluginSet set,
        bool checkHealth,
        CancellationToken cancellationToken)
        where T : class, IPlugin
    {
        var resolved = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = ids.Concat(externals.Where(e => e.Value.Manifest.Kind == kind && kind != ComponentKind.Recommendation).Select(e => e.Key)).ToList();

        foreach (var id in ordered)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            IPlugin? plugin = null;
            if (externals.Remove(id, out var external))
            {
                plugin = external;
            }
            else if (catalog.TryGet(id, out var descriptor))
            {
                if (descriptor.Factory.Kind != kind)
                {
                    set.Note($"{id}: is a {descriptor.Factory.Kind} plugin, not {kind}; skipped.");
                    continue;
                }

                try
                {
                    plugin = descriptor.Factory.Create(CreateContext(options, id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    set.Note($"{id}: failed to initialise: {ex.Message}");
                    _logger.LogWarning(ex, "Plugin {Id} failed to initialise", id);
                    continue;
                }
            }
            else
            {
                set.Note($"{id}: plugin not found.");
                continue;
            }

            set.Add(plugin);
            if (plugin is not T typed)
            {
                set.Note($"{id}: does not implement {typeof(T).Name}; skipped.");
                continue;
            }

            if (!string.Equals(plugin.Manifest.ContractVersion.Split('.')[0], ContractInfo.Version.Split('.')[0], StringComparison.Ordinal))
            {
                set.Note($"{id}: contract {plugin.Manifest.ContractVersion} is incompatible with host contract {ContractInfo.Version}; skipped.");
                continue;
            }

            if (checkHealth)
            {
                PluginHealth health;
                try
                {
                    health = await plugin.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    health = PluginHealth.Unhealthy(ex.Message);
                }

                if (!health.Healthy)
                {
                    set.Note($"{id}: unavailable — {health.Message}");
                    continue;
                }
            }

            resolved.Add(typed);
        }

        return resolved;
    }
}
