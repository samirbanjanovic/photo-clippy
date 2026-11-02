using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Plugins;

namespace PhotoClippy.Core.Tests;

public sealed class PluginHostTests : IDisposable
{
    private static readonly IReadOnlySet<ComponentKind> AllKinds = new HashSet<ComponentKind>(Enum.GetValues<ComponentKind>());

    private readonly TestDir _dir = new("host");

    public void Dispose() => _dir.Dispose();

    internal static AppPaths Paths(string home) => new()
    {
        Home = home,
        Database = Path.Combine(home, "cache.db"),
        Models = Path.Combine(home, "models"),
        State = Path.Combine(home, "state"),
        Data = AppPaths.FindDataDirectory(),
        Plugins = Path.Combine(home, "plugins"),
    };

    internal static PluginOptions Options(string[]? metadata = null, string[]? composition = null, string[]? color = null) => new()
    {
        Metadata = [.. metadata ?? []],
        Composition = [.. composition ?? []],
        Color = [.. color ?? []],
        Recommender = "",
        Reports = [],
        GenreWeights = new(StringComparer.OrdinalIgnoreCase),
    };

    private PluginHost Host(PluginCatalog catalog, ExternalPluginActivator? activator = null) =>
        new(catalog, Paths(_dir.Path), TestData.Taxonomy, null, activator);

    [Fact]
    public async Task Selects_configured_plugins_and_passes_context()
    {
        var meta = new FakeFactory("fake.metadata", ComponentKind.Metadata, _ => new FakeExtractor());
        var catalog = new PluginCatalog()
            .Add(meta)
            .Add(new FakeFactory("fake.composition", ComponentKind.Composition, _ => new FakeComposition()))
            .Add(new FakeFactory("fake.color", ComponentKind.Color, _ => new FakeColor()));
        var options = Options(["fake.metadata"], ["fake.composition"], ["fake.color"]);
        options.Settings["fake.metadata"] = JsonDocument.Parse("""{ "level": 3 }""").RootElement.Clone();
        options.GenreWeights["fake.composition"] = 0.5;

        await using var set = await Host(catalog).CreateAsync(options, AllKinds);

        Assert.IsType<FakeExtractor>(Assert.Single(set.Metadata));
        Assert.IsType<FakeComposition>(Assert.Single(set.Composition));
        Assert.IsType<FakeColor>(Assert.Single(set.Color));
        Assert.Null(set.Recommender);
        Assert.Empty(set.Notes);
        Assert.Equal(new[] { "fake.metadata", "fake.composition", "fake.color" }, set.Manifests.Select(m => m.Id));
        Assert.Equal(0.5, set.GenreWeights["fake.composition"]);
        var context = meta.LastContext!;
        Assert.Equal(3, context.Settings.GetProperty("level").GetInt32());
        Assert.Equal(Path.Combine(_dir.Path, "state", "fake.metadata"), context.StateDirectory);
        Assert.Same(TestData.Taxonomy, context.Taxonomy);
    }

    [Fact]
    public async Task Only_requested_kinds_are_created()
    {
        var composition = new FakeFactory("fake.composition", ComponentKind.Composition, _ => new FakeComposition());
        var catalog = new PluginCatalog().Add(new FakeFactory("fake.metadata", ComponentKind.Metadata, _ => new FakeExtractor())).Add(composition);

        await using var set = await Host(catalog).CreateAsync(Options(["fake.metadata"], ["fake.composition"]), new HashSet<ComponentKind> { ComponentKind.Metadata });

        Assert.Single(set.Metadata);
        Assert.Empty(set.Composition);
        Assert.Null(composition.LastContext);
    }

    [Fact]
    public async Task Problem_plugins_are_skipped_with_notes()
    {
        var catalog = new PluginCatalog()
            .Add(new FakeFactory("good", ComponentKind.Metadata, _ => new FakeExtractor("good")))
            .Add(new FakeFactory("color-not-meta", ComponentKind.Color, _ => new FakeColor("color-not-meta")))
            .Add(new FakeFactory("boom", ComponentKind.Metadata, _ => throw new InvalidOperationException("no model")))
            .Add(new FakeFactory("plain", ComponentKind.Metadata, _ => new NotAnExtractor("plain")))
            .Add(new FakeFactory("future", ComponentKind.Metadata, _ => new FakeExtractor("future") { ContractVersion = "2.0" }))
            .Add(new FakeFactory("sick", ComponentKind.Metadata, _ => new FakeExtractor("sick") { Health = PluginHealth.Unhealthy("exiftool missing") }))
            .Add(new FakeFactory("flaky", ComponentKind.Metadata, _ => new FakeExtractor("flaky") { ThrowOnHealth = true }));

        await using var set = await Host(catalog).CreateAsync(
            Options(["missing", "color-not-meta", "boom", "plain", "future", "sick", "flaky", "good"]), AllKinds);

        Assert.Equal("good", Assert.Single(set.Metadata).Manifest.Id);
        Assert.Equal(
            new[]
            {
                "missing: plugin not found.",
                "color-not-meta: is a Color plugin, not Metadata; skipped.",
                "boom: failed to initialise: no model",
                "plain: does not implement IMetadataExtractor; skipped.",
                $"future: contract 2.0 is incompatible with host contract {ContractInfo.Version}; skipped.",
                "sick: unavailable — exiftool missing",
                "flaky: unavailable — health probe exploded",
            },
            set.Notes);
    }

    [Fact]
    public async Task Health_check_can_be_skipped()
    {
        var catalog = new PluginCatalog().Add(new FakeFactory("sick", ComponentKind.Metadata, _ => new FakeExtractor("sick") { Health = PluginHealth.Unhealthy("nope") }));

        await using var set = await Host(catalog).CreateAsync(Options(["sick"]), AllKinds, checkHealth: false);

        Assert.Single(set.Metadata);
        Assert.Empty(set.Notes);
    }

    [Fact]
    public async Task Duplicate_and_blank_ids_are_ignored()
    {
        var factory = new CountingFactory("fake.metadata", ComponentKind.Metadata, () => new FakeExtractor());
        var catalog = new PluginCatalog().Add(factory);

        await using var set = await Host(catalog).CreateAsync(Options(["fake.metadata", " ", "", "FAKE.METADATA"]), AllKinds);

        Assert.Single(set.Metadata);
        Assert.Equal(1, factory.Created);
        Assert.Empty(set.Notes);
    }

    [Fact]
    public async Task Dispose_disposes_every_created_plugin()
    {
        var selected = new DisposableExtractor("a");
        var unhealthy = new DisposableExtractor("b") { Health = PluginHealth.Unhealthy("down") };
        var catalog = new PluginCatalog()
            .Add(new FakeFactory("a", ComponentKind.Metadata, _ => selected))
            .Add(new FakeFactory("b", ComponentKind.Metadata, _ => unhealthy));

        var set = await Host(catalog).CreateAsync(Options(["a", "b"]), AllKinds);
        Assert.Equal(2, set.All.Count);
        await set.DisposeAsync();

        Assert.True(selected.Disposed);
        Assert.True(unhealthy.Disposed);
        Assert.Empty(set.All);
    }

    [Fact]
    public async Task External_definitions_need_an_activator()
    {
        var options = Options();
        options.External.Add(JsonDocument.Parse("""{ "id": "ext.meta", "kind": "metadata" }""").RootElement.Clone());
        options.External.Add(JsonDocument.Parse("""{ "kind": "metadata" }""").RootElement.Clone());

        await using var set = await Host(new PluginCatalog()).CreateAsync(options, AllKinds);

        Assert.Empty(set.Metadata);
        Assert.Contains("ext.meta: external plugins are not supported by this host.", set.Notes);
        Assert.Contains(set.Notes, n => n.StartsWith("External plugin definition skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task External_plugins_are_enabled_automatically_via_activator()
    {
        var options = Options();
        options.External.Add(JsonDocument.Parse("""{ "id": "ext.comp", "kind": "composition", "weight": 0.4, "settings": { "k": 1 } }""").RootElement.Clone());
        options.External.Add(JsonDocument.Parse("""{ "id": "ext.off", "kind": "composition", "enabled": false }""").RootElement.Clone());
        PluginContext? seen = null;

        await using var set = await Host(new PluginCatalog(), (definition, context, _) =>
        {
            seen = context;
            return Task.FromResult<IPlugin>(new FakeComposition(definition.GetProperty("id").GetString()!));
        }).CreateAsync(options, AllKinds);

        Assert.Equal("ext.comp", Assert.Single(set.Composition).Manifest.Id);
        Assert.Equal(0.4, set.GenreWeights["ext.comp"]);
        Assert.Equal(1, seen!.Settings.GetProperty("k").GetInt32());
        Assert.Empty(set.Notes);
    }

    private sealed class CountingFactory(string id, ComponentKind kind, Func<IPlugin> create) : IPluginFactory
    {
        public int Created { get; private set; }
        public string Id => id;
        public ComponentKind Kind => kind;

        public IPlugin Create(PluginContext context)
        {
            Created++;
            return create();
        }
    }

    private sealed class DisposableExtractor(string id) : FakePlugin(id, ComponentKind.Metadata), IMetadataExtractor, IDisposable
    {
        public bool Disposed { get; private set; }

        public bool CanHandle(SourceFile file) => true;

        public Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhotoMetadata>>([]);

        public void Dispose() => Disposed = true;
    }
}
