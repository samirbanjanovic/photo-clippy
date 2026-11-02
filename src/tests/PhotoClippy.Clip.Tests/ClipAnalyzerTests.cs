using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Clip;

namespace PhotoClippy.Clip.Tests;

/// <summary>Tests that need no model files.</summary>
public sealed class ClipAnalyzerTests
{
    private static readonly Dictionary<string, (long Size, string Sha)> Pinned = new()
    {
        ["onnx/vision_model.onnx"] = (351685709, "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40"),
        ["onnx/vision_model_quantized.onnx"] = (89117001, "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299"),
        ["onnx/text_model.onnx"] = (254058553, "3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc"),
        ["onnx/text_model_quantized.onnx"] = (64504507, "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a"),
        ["vocab.json"] = (862328, "5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363"),
        ["merges.txt"] = (524619, "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a"),
    };

    [Fact]
    public async Task WithoutModels_CreateSucceeds_HealthIsUnhealthy_AndAnalyzeReturnsEmptyFeatures()
    {
        var context = ClipTestAssets.CreateContext(ClipTestAssets.NewStateDirectory("no-models"), ClipTestAssets.NewStateDirectory("state"));
        var plugin = new ClipCompositionFactory().Create(context);
        using var analyzer = Assert.IsType<ClipCompositionAnalyzer>(plugin);

        var health = await analyzer.CheckHealthAsync();
        var features = await analyzer.AnalyzeAsync(ClipTestAssets.Solid(64, 48, 10, 20, 30), ClipTestAssets.AnalysisContext());

        Assert.False(health.Healthy);
        Assert.Equal("CLIP model not found – run `photo-clippy models fetch`", health.Message);
        Assert.Null(features.Genres);
        Assert.Empty(features.Extras);
        Assert.Null(analyzer.LoadedRuntime);
        Assert.False(File.Exists(analyzer.TextCachePath));
    }

    [Fact]
    public async Task WithCorruptModels_HealthReportsLoadFailure_AndAnalyzeReturnsEmptyFeatures()
    {
        var models = ClipTestAssets.NewStateDirectory("corrupt-models");
        var context = ClipTestAssets.CreateContext(models, ClipTestAssets.NewStateDirectory("state"));
        using var analyzer = new ClipCompositionAnalyzer(context);
        foreach (var path in analyzer.ModelPaths.All)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a model");
        }

        var health = await analyzer.CheckHealthAsync();
        var features = await analyzer.AnalyzeAsync(ClipTestAssets.Solid(64, 48, 10, 20, 30), ClipTestAssets.AnalysisContext());

        Assert.False(health.Healthy);
        Assert.StartsWith("CLIP model failed to load", health.Message, StringComparison.Ordinal);
        Assert.Null(features.Genres);
    }

    [Fact]
    public async Task EmptyTaxonomy_IsUnhealthy()
    {
        var context = ClipTestAssets.CreateContext(ClipTestAssets.NewStateDirectory("models"), ClipTestAssets.NewStateDirectory("state"), taxonomy: new GenreTaxonomy());
        using var analyzer = new ClipCompositionAnalyzer(context);

        var health = await analyzer.CheckHealthAsync();

        Assert.False(health.Healthy);
        Assert.Null((await analyzer.AnalyzeAsync(ClipTestAssets.Solid(8, 8, 0, 0, 0), ClipTestAssets.AnalysisContext())).Genres);
    }

    [Theory]
    [InlineData(true, "vision_model_quantized.onnx")]
    [InlineData(false, "vision_model.onnx")]
    public void Models_ArePinnedHuggingFaceAssets(bool quantized, string visionFile)
    {
        using var analyzer = new ClipCompositionAnalyzer(NoModelContext(), new ClipSettings { Quantized = quantized });

        Assert.Equal(4, analyzer.Models.Count);
        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/" + visionFile);
        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/text_model.onnx");
        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/vocab.json");
        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/merges.txt");
        foreach (var asset in analyzer.Models)
        {
            const string prefix = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/";
            Assert.StartsWith(prefix, asset.Source.AbsoluteUri, StringComparison.Ordinal);
            var repoPath = asset.Source.AbsoluteUri[prefix.Length..];
            Assert.Equal(Path.GetFileName(repoPath), Path.GetFileName(asset.RelativePath));
            Assert.Equal(Pinned[repoPath].Size, asset.SizeBytes);
            Assert.Equal(Pinned[repoPath].Sha, asset.Sha256);
            Assert.Equal("MIT", asset.License);
        }
    }

    [Fact]
    public void QuantizedText_SelectsQuantizedTextModel()
    {
        using var analyzer = new ClipCompositionAnalyzer(NoModelContext(), new ClipSettings { QuantizedText = true });

        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/text_model_quantized.onnx");
    }

    [Fact]
    public void Manifest_DescribesPlugin_AndRequestsWorkingEdge()
    {
        using var analyzer = new ClipCompositionAnalyzer(NoModelContext());

        Assert.Equal("builtin.composition.clip", analyzer.Manifest.Id);
        Assert.Equal(ComponentKind.Composition, analyzer.Manifest.Kind);
        Assert.Equal(448, analyzer.Requirements.MaxEdge);
        Assert.Matches("^[0-9a-f]{12}$", analyzer.Manifest.Fingerprint);
        Assert.EndsWith($"text-{analyzer.Manifest.Fingerprint}.bin", analyzer.TextCachePath, StringComparison.Ordinal);
        Assert.Equal("clip", Path.GetFileName(Path.GetDirectoryName(analyzer.TextCachePath)));
        Assert.Equal(["composition.genres", "composition.extras"], analyzer.Manifest.Capabilities);
    }

    [Fact]
    public void Fingerprint_IsStable_AndChangesWithModelSettingsAndTaxonomy()
    {
        string Fingerprint(ClipSettings? settings = null, GenreTaxonomy? taxonomy = null)
        {
            using var analyzer = new ClipCompositionAnalyzer(NoModelContext(taxonomy), settings);
            return analyzer.Manifest.Fingerprint!;
        }

        var baseline = Fingerprint();
        var changedTaxonomy = ClipTestAssets.Taxonomy with
        {
            Genres = [.. ClipTestAssets.Taxonomy.Genres.Select((g, i) => i == 0 ? g with { Prompts = [.. g.Prompts, "a photo of hills"] } : g)],
        };

        Assert.Equal(baseline, Fingerprint());
        Assert.Equal(baseline, Fingerprint(new ClipSettings { IntraOpThreads = 1 }));
        Assert.NotEqual(baseline, Fingerprint(new ClipSettings { Quantized = !new ClipSettings().Quantized }));
        Assert.NotEqual(baseline, Fingerprint(new ClipSettings { QuantizedText = true }));
        Assert.NotEqual(baseline, Fingerprint(new ClipSettings { LogitScale = 50 }));
        Assert.NotEqual(baseline, Fingerprint(taxonomy: changedTaxonomy));
    }

    [Fact]
    public void Factory_ParsesSettingsSection_AndNormalizesInvalidValues()
    {
        var factory = new ClipCompositionFactory();
        var json = JsonDocument.Parse("""{"quantized": false, "intraOpThreads": 3, "logitScale": -5, "model": "../evil"}""").RootElement;

        using var analyzer = (ClipCompositionAnalyzer)factory.Create(ClipTestAssets.CreateContext(ClipTestAssets.NewStateDirectory("models"), ClipTestAssets.NewStateDirectory("state"), json));

        Assert.Equal("builtin.composition.clip", factory.Id);
        Assert.Equal(ComponentKind.Composition, factory.Kind);
        Assert.False(analyzer.Settings.Quantized);
        Assert.Equal(Math.Min(3, Environment.ProcessorCount), analyzer.Settings.IntraOpThreads);
        Assert.Equal(100, analyzer.Settings.LogitScale);
        Assert.Equal("Xenova/clip-vit-base-patch32", analyzer.Settings.Model);
        Assert.Contains(analyzer.Models, m => m.RelativePath == "clip-vit-base-patch32/vision_model.onnx");
    }

    [Fact]
    public void Score_OneHotMatch_IsConfident()
    {
        var genres = Embeddings(4, 8);
        var image = new float[8];
        image[2] = 3f;

        var features = ClipCompositionAnalyzer.Score(image, genres, 100);

        Assert.NotNull(features.Genres);
        Assert.Equal(1.0, features.Genres.Values.Sum(), 1e-9);
        Assert.Equal("g2", features.Genres.MaxBy(kv => kv.Value).Key);
        Assert.Equal(1.0, features.Extras["clip.top1Prob"], 1e-9);
        Assert.Equal(1.0, features.Extras["clip.margin"], 1e-9);
        Assert.Equal(0.0, features.Extras["clip.entropy"], 1e-9);
        Assert.Null(features.SubjectX);
        Assert.Null(features.ThirdsScore);
        Assert.Null(features.HorizonTiltDeg);
        Assert.Null(features.HorizontalEdges);
    }

    [Fact]
    public void Score_EquidistantImage_IsUniform()
    {
        var genres = Embeddings(4, 8);
        var image = new float[8];
        image[6] = 1f;

        var features = ClipCompositionAnalyzer.Score(image, genres, 100);

        Assert.All(features.Genres!.Values, p => Assert.Equal(0.25, p, 1e-9));
        Assert.Equal(0.25, features.Extras["clip.top1Prob"], 1e-9);
        Assert.Equal(0.0, features.Extras["clip.margin"], 1e-9);
        Assert.Equal(1.0, features.Extras["clip.entropy"], 1e-9);
    }

    [Fact]
    public void Score_UsesLogitScaleOverCosineSimilarity()
    {
        var genres = Embeddings(2, 2);
        float[] image = [MathF.Cos(0.3f), MathF.Sin(0.3f)];

        var features = ClipCompositionAnalyzer.Score(image, genres, 10);

        var l0 = 10 * Math.Cos(0.3);
        var l1 = 10 * Math.Sin(0.3);
        var expected = Math.Exp(l0) / (Math.Exp(l0) + Math.Exp(l1));
        Assert.Equal(expected, features.Genres!["g0"], 1e-6);
        Assert.Equal(1 - expected, features.Genres["g1"], 1e-6);
        Assert.Equal((2 * expected) - 1, features.Extras["clip.margin"], 1e-6);
    }

    [Fact]
    public void EmbeddingCache_RoundTrips_AndRejectsMismatches()
    {
        var dir = ClipTestAssets.NewStateDirectory("cache");
        var path = Path.Combine(dir, "clip", "text-test.bin");
        var embeddings = Embeddings(3, 5) with { Matrix = [.. Enumerable.Range(0, 15).Select(i => i * 0.25f)] };
        GenreDefinition[] genres = [Genre("a"), Genre("b"), Genre("c")];

        ClipGenreEmbeddingStore.Save(path, embeddings);
        var loaded = ClipGenreEmbeddingStore.TryLoad(path, genres);

        Assert.NotNull(loaded);
        Assert.True(loaded.FromCache);
        Assert.Equal(5, loaded.Dimension);
        Assert.Equal(["a", "b", "c"], loaded.Ids);
        Assert.Equal(embeddings.Matrix, loaded.Matrix);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Null(ClipGenreEmbeddingStore.TryLoad(path, genres[..2]));
        Assert.Null(ClipGenreEmbeddingStore.TryLoad(Path.Combine(dir, "missing.bin"), genres));

        File.WriteAllBytes(path, File.ReadAllBytes(path)[..40]);
        Assert.Null(ClipGenreEmbeddingStore.TryLoad(path, genres));
        File.WriteAllText(path, "garbage garbage garbage");
        Assert.Null(ClipGenreEmbeddingStore.TryLoad(path, genres));
    }

    [Fact]
    public void PromptsFor_FallsBackToGenreName()
    {
        Assert.Equal(["a photo of Night sky"], ClipGenreEmbeddingStore.PromptsFor(new GenreDefinition { Id = "x", Name = "Night sky", Prompts = [" ", ""] }));
        Assert.Equal(["p1", "p2"], ClipGenreEmbeddingStore.PromptsFor(new GenreDefinition { Id = "x", Name = "X", Prompts = ["p1", "", "p2"] }));
    }

    [Fact]
    public void Taxonomy_HasPromptsForEveryGenre()
    {
        Assert.NotEmpty(ClipTestAssets.Taxonomy.Genres);
        Assert.All(ClipTestAssets.Taxonomy.Genres, g => Assert.NotEmpty(g.Prompts));
        Assert.Contains(ClipTestAssets.Taxonomy.Genres, g => g.Junk);
    }

    private static PluginContext NoModelContext(GenreTaxonomy? taxonomy = null) =>
        ClipTestAssets.CreateContext(Path.Combine(AppContext.BaseDirectory, "clip-test-state", "no-models"), Path.Combine(AppContext.BaseDirectory, "clip-test-state", "shared-state"), taxonomy: taxonomy);

    private static GenreDefinition Genre(string id) => new() { Id = id, Name = id };

    /// <summary>Unit-vector genre embeddings along the first <paramref name="count"/> axes.</summary>
    private static ClipGenreEmbeddings Embeddings(int count, int dimension)
    {
        var matrix = new float[count * dimension];
        for (var g = 0; g < count; g++)
        {
            matrix[(g * dimension) + g] = 1f;
        }

        return new ClipGenreEmbeddings([.. Enumerable.Range(0, count).Select(i => $"g{i}")], matrix, dimension, FromCache: false);
    }
}
