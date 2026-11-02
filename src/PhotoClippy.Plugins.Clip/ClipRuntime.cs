using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Clip;

/// <summary>Loaded CLIP state: the shared vision session plus the genre text embeddings.</summary>
internal sealed class ClipRuntime : IDisposable
{
    private ClipRuntime(InferenceSession vision, string inputName, string outputName, int imageSize, ClipGenreEmbeddings genres)
    {
        Vision = vision;
        VisionInput = inputName;
        VisionOutput = outputName;
        ImageSize = imageSize;
        Genres = genres;
    }

    public InferenceSession Vision { get; }
    public string VisionInput { get; }
    public string VisionOutput { get; }
    public int ImageSize { get; }
    public ClipGenreEmbeddings Genres { get; }

    private static readonly Lazy<OrtEnv> SharedEnvironment = new(CreateEnvironment);

    public static ClipRuntime Load(ClipModelPaths paths, ClipSettings settings, GenreTaxonomy taxonomy, string cachePath, ILogger logger)
    {
        _ = SharedEnvironment.Value;
        var genres = ClipGenreEmbeddingStore.TryLoad(cachePath, taxonomy.Genres);
        if (genres is null)
        {
            var tokenizer = ClipTokenizer.Load(paths.Vocab, paths.Merges);
            using (var textOptions = CreateOptions(settings))
            using (var text = new InferenceSession(paths.Text, textOptions))
            {
                genres = ClipGenreEmbeddingStore.Compute(text, tokenizer, taxonomy);
            }

            try
            {
                ClipGenreEmbeddingStore.Save(cachePath, genres);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not write CLIP genre embedding cache {Path}", cachePath);
            }
        }

        using var options = CreateOptions(settings);
        var vision = new InferenceSession(paths.Vision, options);
        try
        {
            var input = ClipGenreEmbeddingStore.PickName(vision.InputMetadata.Keys, "pixel_values");
            var output = ClipGenreEmbeddingStore.PickName(vision.OutputMetadata.Keys, "image_embeds");
            var dims = vision.InputMetadata[input].Dimensions;
            var size = dims.Length == 4 && dims[2] > 0 && dims[2] == dims[3] ? dims[2] : ClipImagePreprocessor.DefaultSize;
            return new ClipRuntime(vision, input, output, size, genres);
        }
        catch
        {
            vision.Dispose();
            throw;
        }
    }

    private static OrtEnv CreateEnvironment()
    {
        // Quiet (errors only) and no telemetry; falls back to the existing instance if a host created one first.
        var options = new EnvironmentCreationOptions { logId = "photo-clippy", logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        OrtEnv env;
        try
        {
            env = OrtEnv.CreateInstanceWithOptions(ref options);
        }
        catch (OnnxRuntimeException)
        {
            env = OrtEnv.Instance();
        }

        env.DisableTelemetryEvents();
        return env;
    }

    private static SessionOptions CreateOptions(ClipSettings settings) => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        IntraOpNumThreads = settings.IntraOpThreads,
        InterOpNumThreads = 1,
    };

    public void Dispose() => Vision.Dispose();
}

internal sealed record ClipModelPaths(string Vision, string Text, string Vocab, string Merges)
{
    public IEnumerable<string> All => [Vision, Text, Vocab, Merges];
}
