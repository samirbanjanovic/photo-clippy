using PhotoClippy.Contracts;
using PhotoClippy.Core.Analysis;

namespace PhotoClippy.Core.Tests;

internal sealed class FakeFactory(string id, ComponentKind kind, Func<PluginContext, IPlugin> create) : IPluginFactory
{
    public string Id => id;
    public ComponentKind Kind => kind;
    public PluginContext? LastContext { get; private set; }

    public IPlugin Create(PluginContext context)
    {
        LastContext = context;
        return create(context);
    }
}

internal abstract class FakePlugin(string id, ComponentKind kind) : IPlugin
{
    public string ContractVersion { get; init; } = ContractInfo.Version;
    public PluginHealth Health { get; init; } = PluginHealth.Ok();
    public bool ThrowOnHealth { get; init; }

    public PluginManifest Manifest => new() { Id = id, Name = id, Version = "1.0.0", Kind = kind, ContractVersion = ContractVersion };

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ThrowOnHealth ? throw new InvalidOperationException("health probe exploded") : ValueTask.FromResult(Health);
}

internal sealed class FakeExtractor(string id = "fake.metadata") : FakePlugin(id, ComponentKind.Metadata), IMetadataExtractor
{
    private int _calls;
    private int _files;

    public int Calls => _calls;
    public int FilesExtracted => _files;
    public Func<SourceFile, PhotoMetadata>? Map { get; init; }

    public bool CanHandle(SourceFile file) => true;

    public Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _files, batch.Count);
        var map = Map ?? (f => TestData.NikonMeta(f.Path, new DateTime(2023, 5, 1, 10, 0, 0).AddMinutes(Math.Abs(f.Path.GetHashCode(StringComparison.Ordinal) % 1000))));
        return Task.FromResult<IReadOnlyList<PhotoMetadata>>([.. batch.Select(map)]);
    }
}

internal sealed class ThrowingExtractor(string id = "fake.metadata.throwing") : FakePlugin(id, ComponentKind.Metadata), IMetadataExtractor
{
    public bool CanHandle(SourceFile file) => true;

    public Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("extractor exploded");
}

internal sealed class FakeComposition(string id = "fake.composition") : FakePlugin(id, ComponentKind.Composition), ICompositionAnalyzer
{
    private int _calls;

    public int Calls => _calls;
    public FrameRequirements Requirements { get; } = new(64);

    public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return ValueTask.FromResult(new CompositionFeatures
        {
            Genres = new Dictionary<string, double> { ["landscape"] = 0.9, ["portrait"] = 0.1 },
            ThirdsScore = 0.6,
            Extras = new Dictionary<string, double> { ["clip.top1Prob"] = 0.7, ["nan"] = double.NaN },
        });
    }
}

internal sealed class ThrowingComposition(string id = "fake.composition.throwing") : FakePlugin(id, ComponentKind.Composition), ICompositionAnalyzer
{
    public FrameRequirements Requirements { get; } = new(64);

    public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("analyzer exploded");
}

internal sealed class FakeColor(string id = "fake.color") : FakePlugin(id, ComponentKind.Color), IColorAnalyzer
{
    public FrameRequirements Requirements { get; } = new(32);

    public ValueTask<ColorFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ColorFeatures { MeanLuma = 0.5, Extras = new Dictionary<string, double> { ["meanHue"] = 0.3 } });
}

internal sealed class FakeLensCatalog(string id = "fake.lenses") : FakePlugin(id, ComponentKind.LensCatalog), ILensCatalog
{
    public Task<LensCatalogInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LensCatalogInfo { Available = true, LensCount = 1 });

    public Task<IReadOnlyList<CatalogLens>> SearchAsync(LensCatalogQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogLens>>([new CatalogLens { Id = "x/23", Maker = "X", Model = "23mm f/1.4", Mounts = ["X"], FocalMinMm = 23, FocalMaxMm = 23, MaxApertureWide = 1.4, MaxApertureTele = 1.4 }]);

    public Task<LensCatalogInfo> RefreshAsync(CancellationToken cancellationToken = default) => GetInfoAsync(cancellationToken);

    public Task<string?> MountForBodyAsync(string? maker, string? model, CancellationToken cancellationToken = default) => Task.FromResult<string?>("X");
}

internal sealed class NotAnExtractor(string id = "fake.wrong") : FakePlugin(id, ComponentKind.Metadata);

internal sealed class FakeFrameSource : IFrameSource
{
    private int _decodes;

    public int Decodes => _decodes;
    public string Id => "fake-frames";

    public bool CanDecode(SourceFile file) => true;

    public FrameDecodeResult Decode(SourceFile file, int maxEdge, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _decodes);
        return FrameDecodeResult.Ok(new FrameLease(new Frame(4, 4, new byte[4 * 4 * 3], 4, 4)));
    }
}
