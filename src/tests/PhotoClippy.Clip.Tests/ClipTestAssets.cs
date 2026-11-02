using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Clip;
using SkiaSharp;

namespace PhotoClippy.Clip.Tests;

/// <summary>
/// Locates optional test assets. Models: <c>PHOTO_CLIPPY_MODELS</c>, then <c>PHOTO_CLIPPY_HOME/models</c>,
/// the default per-user models directory and <c>/tmp/photo-clippy-clip/models</c>. Images:
/// <c>PHOTO_CLIPPY_CLIP_IMAGES</c> or an <c>images</c> directory next to the models directory.
/// Fetch them with <c>fetch-test-assets.sh</c>.
/// </summary>
internal static class ClipTestAssets
{
    public const string ModelDirectoryName = "clip-vit-base-patch32";

    private static readonly Lazy<GenreTaxonomy> LazyTaxonomy = new(LoadTaxonomy);
    private static readonly Lazy<string?> LazyModels = new(FindModelsDirectory);

    public static GenreTaxonomy Taxonomy => LazyTaxonomy.Value;

    /// <summary>Directory containing <c>clip-vit-base-patch32/</c>, or null when no candidate has the vocabulary.</summary>
    public static string? ModelsDirectory => LazyModels.Value;

    public static string? ImagesDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PHOTO_CLIPPY_CLIP_IMAGES");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Directory.Exists(configured) ? configured : null;
            }

            if (ModelsDirectory is null)
            {
                return null;
            }

            var sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ModelsDirectory).TrimEnd(Path.DirectorySeparatorChar))!, "images");
            return Directory.Exists(sibling) ? sibling : null;
        }
    }

    public static bool TokenizerAvailable => ModelsDirectory is { } dir
        && File.Exists(Path.Combine(dir, ModelDirectoryName, "vocab.json"))
        && File.Exists(Path.Combine(dir, ModelDirectoryName, "merges.txt"));

    public static bool ModelsAvailable(ClipSettings? settings = null)
    {
        if (ModelsDirectory is null)
        {
            return false;
        }

        using var analyzer = new ClipCompositionAnalyzer(CreateContext(ModelsDirectory, Path.Combine(AppContext.BaseDirectory, "clip-test-state", "probe")), settings);
        return analyzer.ModelsPresent;
    }

    public static string? ImagePath(string name) =>
        ImagesDirectory is { } dir && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;

    public static ClipTokenizer LoadTokenizer() => ClipTokenizer.Load(
        Path.Combine(ModelsDirectory!, ModelDirectoryName, "vocab.json"),
        Path.Combine(ModelsDirectory!, ModelDirectoryName, "merges.txt"));

    private static readonly Lazy<string> StateRoot = new(() =>
    {
        // Each run starts from a clean root so per-test state directories do not pile up in bin/.
        var root = Path.Combine(AppContext.BaseDirectory, "clip-test-state");
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return root;
    });

    public static string NewStateDirectory(string name)
    {
        var path = Path.Combine(StateRoot.Value, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static PluginContext CreateContext(string modelsDirectory, string stateDirectory, JsonElement settings = default, GenreTaxonomy? taxonomy = null) => new()
    {
        Settings = settings,
        DataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(TaxonomyPath())!)!,
        ModelsDirectory = modelsDirectory,
        StateDirectory = stateDirectory,
        LoggerFactory = NullLoggerFactory.Instance,
        Taxonomy = taxonomy ?? Taxonomy,
    };

    public static AnalysisContext AnalysisContext(string name = "test") => new() { CaptureId = name, SourcePath = name };

    /// <summary>Decodes an image and downscales it to <paramref name="maxEdge"/> like the host does.</summary>
    public static Frame LoadFrame(string path, int maxEdge = ClipCompositionAnalyzer.WorkingEdge)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Cannot decode {path}.");
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(decoded.Width, decoded.Height));
        var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!decoded.ScalePixels(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            throw new InvalidOperationException("ScalePixels failed.");
        }

        return ToFrame(target, decoded.Width, decoded.Height);
    }

    /// <summary>Renders a synthetic desktop-application screenshot (window chrome, sidebar, text, buttons).</summary>
    public static Frame RenderScreenshot(int width = 448, int height = 280)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        using (var font = new SKFont(SKTypeface.Default, 11))
        using (var titleFont = new SKFont(SKTypeface.Default, 14))
        {
            canvas.Clear(new SKColor(245, 246, 248));
            paint.Color = new SKColor(222, 225, 230);
            canvas.DrawRect(0, 0, width, 26, paint);
            SKColor[] lights = [new(255, 95, 87), new(254, 188, 46), new(40, 200, 64)];
            for (var i = 0; i < lights.Length; i++)
            {
                paint.Color = lights[i];
                canvas.DrawCircle(14 + (i * 16), 13, 5, paint);
            }

            paint.Color = new SKColor(60, 60, 67);
            canvas.DrawText("Settings — Preferences", new SKPoint((width / 2f) - 70, 18), SKTextAlign.Left, font, paint);

            paint.Color = new SKColor(232, 234, 238);
            canvas.DrawRect(0, 26, 110, height - 26, paint);
            string[] menu = ["General", "Accounts", "Notifications", "Privacy", "Network", "Storage", "Display", "Updates", "About"];
            for (var i = 0; i < menu.Length; i++)
            {
                if (i == 2)
                {
                    paint.Color = new SKColor(0, 122, 255);
                    canvas.DrawRoundRect(6, 34 + (i * 24), 98, 20, 4, 4, paint);
                    paint.Color = SKColors.White;
                }
                else
                {
                    paint.Color = new SKColor(40, 40, 45);
                }

                canvas.DrawText(menu[i], new SKPoint(14, 48 + (i * 24)), SKTextAlign.Left, font, paint);
            }

            paint.Color = new SKColor(20, 20, 25);
            canvas.DrawText("Notifications", new SKPoint(126, 52), SKTextAlign.Left, titleFont, paint);
            string[] lines =
            [
                "Show previews: When unlocked",
                "Allow notifications when the display is sleeping",
                "Allow notifications when mirroring the screen",
                "Play sound for notifications",
                "Badge application icon",
                "Notification grouping: Automatic",
            ];
            for (var i = 0; i < lines.Length; i++)
            {
                paint.Color = SKColors.White;
                canvas.DrawRoundRect(122, 64 + (i * 30), width - 134, 26, 5, 5, paint);
                paint.Color = new SKColor(30, 30, 35);
                canvas.DrawText(lines[i], new SKPoint(130, 81 + (i * 30)), SKTextAlign.Left, font, paint);
                paint.Color = i % 2 == 0 ? new SKColor(52, 199, 89) : new SKColor(200, 200, 205);
                canvas.DrawRoundRect(width - 48, 70 + (i * 30), 30, 14, 7, 7, paint);
            }

            paint.Color = new SKColor(0, 122, 255);
            canvas.DrawRoundRect(width - 90, height - 34, 76, 24, 5, 5, paint);
            paint.Color = SKColors.White;
            canvas.DrawText("Apply", new SKPoint(width - 68, height - 18), SKTextAlign.Left, font, paint);
        }

        return ToFrame(bitmap, width, height);
    }

    public static Frame Solid(int width, int height, byte r, byte g, byte b)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }

        return new Frame(width, height, rgb, width, height);
    }

    private static Frame ToFrame(SKBitmap bitmap, int sourceWidth, int sourceHeight)
    {
        var rgba = bitmap.GetPixelSpan();
        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }

        return new Frame(bitmap.Width, bitmap.Height, rgb, sourceWidth, sourceHeight);
    }

    private static string TaxonomyPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "taxonomy", "genres.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("data/taxonomy/genres.json not found above the test output directory.");
    }

    private static GenreTaxonomy LoadTaxonomy()
    {
        using var stream = File.OpenRead(TaxonomyPath());
        return JsonSerializer.Deserialize<GenreTaxonomy>(stream, ContractJson.Options)
            ?? throw new InvalidDataException("Empty taxonomy.");
    }

    private static string? FindModelsDirectory()
    {
        var home = Environment.GetEnvironmentVariable("PHOTO_CLIPPY_HOME");
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("PHOTO_CLIPPY_MODELS"),
            string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "photo-clippy", "models"),
            "/tmp/photo-clippy-clip/models",
        ];
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, ModelDirectoryName, "vocab.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Skips unless vocab.json and merges.txt are available.</summary>
public sealed class TokenizerFactAttribute : FactAttribute
{
    public TokenizerFactAttribute()
    {
        if (!ClipTestAssets.TokenizerAvailable)
        {
            Skip = "CLIP vocabulary not found (set PHOTO_CLIPPY_MODELS).";
        }
    }
}

public sealed class TokenizerTheoryAttribute : TheoryAttribute
{
    public TokenizerTheoryAttribute()
    {
        if (!ClipTestAssets.TokenizerAvailable)
        {
            Skip = "CLIP vocabulary not found (set PHOTO_CLIPPY_MODELS).";
        }
    }
}

/// <summary>Skips unless the default model files are available.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (!ClipTestAssets.ModelsAvailable())
        {
            Skip = "CLIP models not found (set PHOTO_CLIPPY_MODELS, run fetch-test-assets.sh).";
        }
    }
}

/// <summary>Skips unless the default model files and the test images are available.</summary>
public sealed class ModelImagesTheoryAttribute : TheoryAttribute
{
    public ModelImagesTheoryAttribute()
    {
        if (!ClipTestAssets.ModelsAvailable() || ClipTestAssets.ImagesDirectory is null)
        {
            Skip = "CLIP models or test images not found (set PHOTO_CLIPPY_MODELS, run fetch-test-assets.sh).";
        }
    }
}

/// <summary>Opt-in benchmark (PHOTO_CLIPPY_CLIP_BENCH=1) that needs all model variants and the test images.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PHOTO_CLIPPY_CLIP_BENCH") != "1")
        {
            Skip = "Benchmark disabled (set PHOTO_CLIPPY_CLIP_BENCH=1).";
        }
        else if (!ClipTestAssets.ModelsAvailable(new ClipSettings { Quantized = true }) || !ClipTestAssets.ModelsAvailable(new ClipSettings { Quantized = false }) || ClipTestAssets.ImagesDirectory is null)
        {
            Skip = "Benchmark needs both vision models and the test images.";
        }
    }
}
