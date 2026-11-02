using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Clip;

/// <summary>
/// <c>builtin.composition.clip</c>: zero-shot genre recognition with CLIP (ONNX Runtime, CPU).
/// Genres are scored as softmax(LogitScale × cosine(image, genre)) over every taxonomy genre, where a genre
/// embedding is the re-normalized mean of its normalized prompt embeddings. Only <see cref="CompositionFeatures.Genres"/>
/// and the <c>clip.*</c> extras are produced. Models are loaded lazily on first use and shared across threads.
/// </summary>
public sealed class ClipCompositionAnalyzer : ICompositionAnalyzer, IRequiresModels, IDisposable
{
    public const string PluginId = "builtin.composition.clip";
    public const string PluginVersion = "1.0.0";
    public const string ModelNotFoundMessage = "CLIP model not found – run `photo-clippy models fetch`";

    /// <summary>Short side ≥ 224 for aspect ratios up to 2:1.</summary>
    public const int WorkingEdge = 448;

    /// <summary>Bump when preprocessing or scoring changes without a settings/model change.</summary>
    private const string AlgorithmRevision = "clip-algo-1";

    private readonly ClipSettings _settings;
    private readonly PluginContext _context;
    private readonly ILogger _logger;
    private readonly ClipModelPaths _paths;
    private readonly Lazy<ClipRuntime> _runtime;
    private int _missingLogged;
    private int _failureLogged;
    private volatile bool _disposed;

    public ClipCompositionAnalyzer(PluginContext context, ClipSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = context.LoggerFactory?.CreateLogger<ClipCompositionAnalyzer>() ?? NullLogger<ClipCompositionAnalyzer>.Instance;
        _settings = Normalize(settings ?? new ClipSettings(), _logger);

        var files = ClipModelCatalog.Files(_settings.Model, _settings.Quantized, _settings.QuantizedText);
        var assets = new List<ModelAsset>(files.Count);
        var paths = new Dictionary<ClipModelRole, string>();
        foreach (var file in files)
        {
            assets.Add(ClipModelCatalog.ToAsset(_settings.Model, file));
            paths[file.Role] = Path.Combine(context.ModelsDirectory, file.RelativePath);
        }

        Models = assets;
        _paths = new ClipModelPaths(paths[ClipModelRole.Vision], paths[ClipModelRole.Text], paths[ClipModelRole.Vocab], paths[ClipModelRole.Merges]);

        var fingerprint = ComputeFingerprint(_settings, files, context.Taxonomy);
        TextCachePath = Path.Combine(context.StateDirectory, "clip", $"text-{fingerprint}.bin");
        Requirements = new FrameRequirements(WorkingEdge);
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "CLIP genre recognition",
            Version = PluginVersion,
            Kind = ComponentKind.Composition,
            Description = $"Zero-shot photo genre recognition with CLIP ({_settings.Model}{(_settings.Quantized ? ", int8 vision" : string.Empty)}) on ONNX Runtime.",
            Capabilities = ["composition.genres", "composition.extras"],
            Fingerprint = fingerprint,
        };

        _runtime = new Lazy<ClipRuntime>(LoadRuntime, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Effective (normalized) settings.</summary>
    public ClipSettings Settings => _settings;

    public PluginManifest Manifest { get; }

    public FrameRequirements Requirements { get; }

    public IReadOnlyList<ModelAsset> Models { get; }

    internal string TextCachePath { get; }

    internal ClipModelPaths ModelPaths => _paths;

    /// <summary>The loaded runtime, or null when not loaded (yet).</summary>
    internal ClipRuntime? LoadedRuntime => _runtime.IsValueCreated ? _runtime.Value : null;

    internal bool ModelsPresent
    {
        get
        {
            foreach (var path in _paths.All)
            {
                if (!File.Exists(path))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.Taxonomy.Genres.Count == 0)
        {
            return ValueTask.FromResult(PluginHealth.Unhealthy("Genre taxonomy is empty."));
        }

        if (!ModelsPresent)
        {
            return ValueTask.FromResult(PluginHealth.Unhealthy(ModelNotFoundMessage));
        }

        var runtime = TryGetRuntime(out var error);
        return ValueTask.FromResult(runtime is null
            ? PluginHealth.Unhealthy($"CLIP model failed to load: {error}")
            : PluginHealth.Ok(string.Create(CultureInfo.InvariantCulture, $"{_settings.Model} ({(_settings.Quantized ? "int8" : "fp32")} vision), {runtime.Genres.Ids.Length} genres")));
    }

    public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_context.Taxonomy.Genres.Count == 0)
        {
            return ValueTask.FromResult(new CompositionFeatures());
        }

        if (!_runtime.IsValueCreated && !ModelsPresent)
        {
            if (Interlocked.Exchange(ref _missingLogged, 1) == 0)
            {
                _logger.LogWarning("{Message} (looked in {Directory}); genre recognition is disabled.", ModelNotFoundMessage, Path.GetDirectoryName(_paths.Vision));
            }

            return ValueTask.FromResult(new CompositionFeatures());
        }

        var runtime = TryGetRuntime(out _);
        return ValueTask.FromResult(runtime is null ? new CompositionFeatures() : Analyze(runtime, frame, cancellationToken));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_runtime.IsValueCreated)
        {
            _runtime.Value.Dispose();
        }
    }

    internal CompositionFeatures Analyze(ClipRuntime runtime, Frame frame, CancellationToken cancellationToken)
    {
        var size = runtime.ImageSize;
        var length = 3 * size * size;
        var pixels = ArrayPool<float>.Shared.Rent(length);
        var embedding = ArrayPool<float>.Shared.Rent(runtime.Genres.Dimension);
        try
        {
            ClipImagePreprocessor.Preprocess(frame.Rgb.Span, frame.Width, frame.Height, size, pixels.AsSpan(0, length));
            using (var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, pixels.AsMemory(0, length), [1, 3, size, size]))
            using (var runOptions = new RunOptions())
            using (cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions))
            {
                try
                {
                    using var outputs = runtime.Vision.Run(runOptions, [runtime.VisionInput], [input], [runtime.VisionOutput]);
                    var output = outputs[0].GetTensorDataAsSpan<float>();
                    if (output.Length != runtime.Genres.Dimension)
                    {
                        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"CLIP image embedding has {output.Length} values, expected {runtime.Genres.Dimension}."));
                    }

                    output.CopyTo(embedding);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return Score(embedding.AsSpan(0, runtime.Genres.Dimension), runtime.Genres, _settings.LogitScale);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pixels);
            ArrayPool<float>.Shared.Return(embedding);
        }
    }

    internal static CompositionFeatures Score(ReadOnlySpan<float> imageEmbedding, ClipGenreEmbeddings genres, double logitScale)
    {
        var count = genres.Ids.Length;
        var dimension = genres.Dimension;
        var norm = (double)ClipGenreEmbeddingStore.Norm(imageEmbedding);
        var logits = new double[count];
        var max = double.NegativeInfinity;
        for (var g = 0; g < count; g++)
        {
            var row = genres.Matrix.AsSpan(g * dimension, dimension);
            var dot = 0.0;
            for (var d = 0; d < dimension; d++)
            {
                dot += (double)imageEmbedding[d] * row[d];
            }

            logits[g] = logitScale * dot / norm;
            max = Math.Max(max, logits[g]);
        }

        var sum = 0.0;
        for (var g = 0; g < count; g++)
        {
            logits[g] = Math.Exp(logits[g] - max);
            sum += logits[g];
        }

        var result = new Dictionary<string, double>(count, StringComparer.Ordinal);
        double top1 = 0, top2 = 0, entropy = 0;
        for (var g = 0; g < count; g++)
        {
            var p = logits[g] / sum;
            result[genres.Ids[g]] = p;
            if (p > top1)
            {
                top2 = top1;
                top1 = p;
            }
            else if (p > top2)
            {
                top2 = p;
            }

            if (p > 0)
            {
                entropy -= p * Math.Log(p);
            }
        }

        return new CompositionFeatures
        {
            Genres = result,
            Extras = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["clip.top1Prob"] = top1,
                ["clip.margin"] = top1 - top2,
                ["clip.entropy"] = count > 1 ? Math.Clamp(entropy / Math.Log(count), 0, 1) : 0,
            },
        };
    }

    private ClipRuntime? TryGetRuntime(out string? error)
    {
        try
        {
            error = null;
            return _runtime.Value;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            error = ex.Message;
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                _logger.LogError(ex, "CLIP model failed to load from {Directory}; genre recognition is disabled.", Path.GetDirectoryName(_paths.Vision));
            }

            return null;
        }
    }

    private ClipRuntime LoadRuntime()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var runtime = ClipRuntime.Load(_paths, _settings, _context.Taxonomy, TextCachePath, _logger);
        _logger.LogInformation(
            "CLIP loaded {Model} ({Vision}) with {Genres} genre embeddings ({Source}) in {Elapsed} ms",
            _settings.Model,
            Path.GetFileName(_paths.Vision),
            runtime.Genres.Ids.Length,
            runtime.Genres.FromCache ? "cached" : "computed",
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return runtime;
    }

    private static ClipSettings Normalize(ClipSettings settings, ILogger logger)
    {
        var model = settings.Model;
        if (!ClipModelCatalog.IsValidModelId(model))
        {
            logger.LogWarning("Invalid CLIP model id '{Model}', using the default.", model);
            model = new ClipSettings().Model;
        }

        return settings with
        {
            Model = model,
            IntraOpThreads = Math.Clamp(settings.IntraOpThreads, 0, Environment.ProcessorCount),
            LogitScale = double.IsFinite(settings.LogitScale) && settings.LogitScale > 0 ? settings.LogitScale : 100,
        };
    }

    private static string ComputeFingerprint(ClipSettings settings, IReadOnlyList<ClipModelFile> files, GenreTaxonomy taxonomy)
    {
        var shas = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (file.Role is ClipModelRole.Vision or ClipModelRole.Text)
            {
                shas.Add(file.Sha256 ?? file.RepoPath);
            }
        }

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{settings.Model}|q={settings.Quantized}|qt={settings.QuantizedText}|{string.Join('|', shas)}|tax={ClipGenreEmbeddingStore.TaxonomyHash(taxonomy)}|scale={settings.LogitScale:R}|{AlgorithmRevision}");
        return ClipHash.Hex(text, 12);
    }
}
