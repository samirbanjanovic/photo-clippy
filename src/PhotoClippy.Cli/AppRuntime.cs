using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using PhotoClippy.Contracts;
using PhotoClippy.Core;
using PhotoClippy.Core.Analysis;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Plugins;
using PhotoClippy.Core.Storage;
using PhotoClippy.Plugins.External;

namespace PhotoClippy.Cli;

internal sealed record GlobalSettings(string? ConfigFile, string? Home, bool Json, bool Verbose, bool Quiet);

internal sealed record ConfigLayers(AppOptions FileOnly, AppOptions WithEnvironment, AppOptions Effective);

/// <summary>Composition root: configuration, paths, logging, taxonomy, plugin catalog and cache for one CLI invocation.</summary>
internal sealed class AppRuntime : IDisposable
{
    /// <summary>Assemblies shipped with the app whose <see cref="IPluginFactory"/> types become built-in plugins.</summary>
    private static readonly string[] BuiltInAssemblies =
    [
        "PhotoClippy.Plugins.Metadata",
        "PhotoClippy.Plugins.Composition",
        "PhotoClippy.Plugins.Clip",
        "PhotoClippy.Plugins.Color",
        "PhotoClippy.Plugins.Recommendation",
        "PhotoClippy.Plugins.Reporting",
        "PhotoClippy.Plugins.Auth",
    ];

    private GenreTaxonomy? _taxonomy;
    private PluginCatalog? _catalog;
    private AnalysisStore? _store;
    private readonly GlobalSettings _settings;
    private readonly Action<AppOptions>? _configure;

    private AppRuntime(AppOptions options, AppPaths paths, ILoggerFactory loggerFactory, string? configFile, GlobalSettings settings, Action<AppOptions>? configure)
    {
        Options = options;
        Paths = paths;
        LoggerFactory = loggerFactory;
        ConfigFile = configFile;
        _settings = settings;
        _configure = configure;
    }

    public AppOptions Options { get; }
    public AppPaths Paths { get; }
    public ILoggerFactory LoggerFactory { get; }
    public string? ConfigFile { get; private set; }

    /// <summary>Where configuration edits are saved: the loaded config file, else &lt;home&gt;/photo-clippy.json.</summary>
    public string WritableConfigFile => ConfigFile ?? Path.Combine(Paths.Home, ConfigLoader.DefaultFileName);

    public static string ToolVersion { get; } =
        typeof(AppRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public GenreTaxonomy Taxonomy => _taxonomy ??= TaxonomyLoader.Load(Paths.Data);

    public AnalysisStore Store
    {
        get
        {
            if (_store is null)
            {
                Paths.EnsureCreated();
                _store = AnalysisStore.Open(Paths.Database);
            }

            return _store;
        }
    }

    public PluginCatalog Catalog => _catalog ??= BuildCatalog();

    public static AppRuntime Create(GlobalSettings settings, Action<AppOptions>? configure = null, LogLevel? minimumLevel = null)
    {
        var configFile = ConfigLoader.FindConfigFile(settings.ConfigFile, homeOverride: settings.Home);
        var options = ConfigLoader.Load(configFile);
        if (settings.Home is not null)
        {
            options.Paths.Home = settings.Home;
        }

        configure?.Invoke(options);
        var level = minimumLevel ?? (settings.Verbose ? LogLevel.Debug : settings.Quiet ? LogLevel.Error : LogLevel.Warning);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddFilter("Microsoft.AspNetCore", level < LogLevel.Information ? level : LogLevel.Warning);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
                o.ColorBehavior = Console.IsErrorRedirected ? LoggerColorBehavior.Disabled : LoggerColorBehavior.Default;
            });
            builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        return new AppRuntime(options, AppPaths.Resolve(options), loggerFactory, configFile, settings, configure);
    }

    /// <summary>
    /// Re-reads the configuration as three layers: the file alone, file + environment, and the effective
    /// options (file + environment + command-line flags), so callers can tell which values the file controls.
    /// </summary>
    public ConfigLayers LoadLayers()
    {
        var file = ConfigFile is not null && File.Exists(ConfigFile) ? ConfigFile : null;
        var fileOnly = ConfigLoader.Load(file, new Dictionary<string, string>());
        var withEnvironment = ConfigLoader.Load(file);
        var effective = ConfigLoader.Load(file);
        if (_settings.Home is not null)
        {
            withEnvironment.Paths.Home = _settings.Home;
            effective.Paths.Home = _settings.Home;
        }

        _configure?.Invoke(effective);
        return new ConfigLayers(fileOnly, withEnvironment, effective);
    }

    /// <summary>
    /// Swaps in reloaded settings. Paths and the plugin directory are fixed for the process lifetime; callers
    /// must not apply while a scan is reading <see cref="Options"/>.
    /// </summary>
    public void ApplyOptions(AppOptions reloaded)
    {
        Options.Library = reloaded.Library;
        Options.Analysis = reloaded.Analysis;
        reloaded.Plugins.Directory = Options.Plugins.Directory;
        Options.Plugins = reloaded.Plugins;
        Options.Recommendation = reloaded.Recommendation;
        Options.Report = reloaded.Report;
        Options.Serve = reloaded.Serve;
    }

    /// <summary>Makes <paramref name="path"/> the config file for later reloads (after the UI first saves one).</summary>
    public void AdoptConfigFile(string path) => ConfigFile = Path.GetFullPath(path);

    public PluginHost CreateHost() => new(Catalog, Paths, Taxonomy, LoggerFactory, ActivateExternalAsync);

    public async Task<PluginSet> CreatePluginsAsync(IReadOnlySet<ComponentKind> kinds, bool checkHealth = true, CancellationToken cancellationToken = default)
    {
        Paths.EnsureCreated();
        return await CreateHost().CreateAsync(Options.Plugins, kinds, checkHealth, cancellationToken).ConfigureAwait(false);
    }

    public IFrameSource? CreateFrameSource() =>
        Options.Analysis.Frames ? new ImagingFrameSource(LoggerFactory.CreateLogger<ImagingFrameSource>()) : null;

    public ILogger CreateLogger(string category) => LoggerFactory.CreateLogger(category);

    private PluginCatalog BuildCatalog()
    {
        var catalog = new PluginCatalog();
        foreach (var name in BuiltInAssemblies)
        {
            try
            {
                catalog.AddRange(PluginCatalog.DiscoverFactories(Assembly.Load(new AssemblyName(name))));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                LoggerFactory.CreateLogger<AppRuntime>().LogWarning("Built-in plugin assembly {Assembly} could not be loaded: {Error}", name, ex.Message);
            }
        }

        if (Directory.Exists(Paths.Plugins))
        {
            catalog.LoadDirectory(Paths.Plugins, LoggerFactory.CreateLogger<PluginCatalog>());
        }

        return catalog;
    }

    private static async Task<IPlugin> ActivateExternalAsync(JsonElement definition, PluginContext context, CancellationToken cancellationToken)
    {
        var options = definition.Deserialize<ExternalPluginOptions>(ContractJson.Options)
            ?? throw new InvalidDataException("Empty external plugin definition.");
        if (options.Settings.ValueKind == JsonValueKind.Undefined && context.Settings.ValueKind != JsonValueKind.Undefined)
        {
            options = options with { Settings = context.Settings };
        }

        return await ExternalPlugins.CreateAsync(options, context, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _store?.Dispose();
        LoggerFactory.Dispose();
    }
}
