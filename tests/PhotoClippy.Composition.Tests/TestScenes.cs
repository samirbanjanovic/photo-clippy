using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Composition;

namespace PhotoClippy.Composition.Tests;

/// <summary>Deterministic synthetic scenes rendered from shaders (u, v in 0..1, origin top-left; colours sRGB 0..1).</summary>
internal static class TestScenes
{
    public delegate (double R, double G, double B) Shader(double u, double v);

    private static readonly Lazy<GenreTaxonomy> LazyTaxonomy = new(LoadTaxonomy);

    public static GenreTaxonomy Taxonomy => LazyTaxonomy.Value;

    public static AnalysisContext Context(CaptureFacts? facts = null) => new()
    {
        CaptureId = facts?.CaptureId ?? "test",
        SourcePath = facts?.PrimaryPath ?? "test.jpg",
        Facts = facts,
    };

    public static CaptureFacts Facts(
        DeviceClass device = DeviceClass.InterchangeableLens,
        double? focal35 = null,
        double? fNumber = null,
        double? exposureS = null,
        int? iso = null,
        double? ev100 = null,
        bool? flash = null,
        string? lens = null) => new()
        {
            CaptureId = "test",
            PrimaryPath = "test.jpg",
            Device = device,
            Make = device is DeviceClass.InterchangeableLens or DeviceClass.FixedLens or DeviceClass.Phone ? "TestCam" : null,
            Focal35Mm = focal35,
            FNumber = fNumber,
            ExposureS = exposureS,
            Iso = iso,
            Ev100 = ev100,
            Flash = flash,
            LensDisplay = lens,
        };

    public static HeuristicCompositionAnalyzer Analyzer(HeuristicCompositionSettings? settings = null, GenreTaxonomy? taxonomy = null) =>
        new(settings, taxonomy ?? Taxonomy);

    public static CompositionFeatures Analyze(Frame frame, CaptureFacts? facts = null, HeuristicCompositionAnalyzer? analyzer = null) =>
        (analyzer ?? Analyzer()).AnalyzeAsync(frame, Context(facts)).AsTask().GetAwaiter().GetResult();

    /// <summary>Renders a shader with ss×ss supersampling (anti-aliased edges, like a downsampled photo).</summary>
    public static Frame Render(int width, int height, Shader shader, int supersample = 3)
    {
        var rgb = new byte[width * height * 3];
        double inv = 1.0 / supersample;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int sy = 0; sy < supersample; sy++)
                {
                    for (int sx = 0; sx < supersample; sx++)
                    {
                        double u = (x + ((sx + 0.5) * inv)) / width;
                        double v = (y + ((sy + 0.5) * inv)) / height;
                        (double cr, double cg, double cb) = shader(u, v);
                        r += cr;
                        g += cg;
                        b += cb;
                    }
                }

                double n = supersample * supersample;
                int i = ((y * width) + x) * 3;
                rgb[i] = ToByte(r / n);
                rgb[i + 1] = ToByte(g / n);
                rgb[i + 2] = ToByte(b / n);
            }
        }

        return new Frame(width, height, rgb, width * 8, height * 8);
    }

    /// <summary>Distance from (u, v) to (cu, cv) in units of the frame height (circles stay round).</summary>
    public static double Dist(double u, double v, double cu, double cv, double aspect)
    {
        double dx = (u - cu) * aspect;
        double dy = v - cv;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Blocky (nearest-cell) value noise in 0..1; cells of <paramref name="cells"/> per frame height.</summary>
    public static double Blocky(double u, double v, double aspect, double cells, int seed) =>
        Hash((int)Math.Floor(u * aspect * cells), (int)Math.Floor(v * cells), seed);

    /// <summary>Smooth (bilinear + smoothstep) value noise in 0..1.</summary>
    public static double Smooth(double u, double v, double aspect, double cells, int seed)
    {
        double fx = u * aspect * cells, fy = v * cells;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = SmoothStep(fx - x0), ty = SmoothStep(fy - y0);
        double a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed);
        double c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
    }

    public static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)((x * 374761393) + (y * 668265263) + (seed * 1442695041));
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    public static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static double SmoothStep(double t) => t * t * (3 - (2 * t));

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);

    // ---- Scenes -------------------------------------------------------------------------------------------

    public static Shader DiscScene(double cu, double cv, double radius, double aspect) => (u, v) =>
    {
        double t = Smooth(u, v, aspect, 3, 7) * 0.06;
        return Dist(u, v, cu, cv, aspect) <= radius ? (0.95, 0.25, 0.1) : (0.2 + t, 0.32 + t, 0.42 + t);
    };

    /// <summary>Sky above, textured ground below; split at <paramref name="splitY"/> at the centre, tilted clockwise by <paramref name="tiltDeg"/>.</summary>
    public static Shader HorizonScene(double splitY, double tiltDeg, double aspect) => (u, v) =>
    {
        double edge = splitY + (Math.Tan(tiltDeg * Math.PI / 180.0) * (u - 0.5) * aspect);
        if (v < edge)
        {
            double k = v / Math.Max(edge, 1e-6);
            return (0.45 + (0.25 * k), 0.65 + (0.2 * k), 0.95);
        }

        double n = (Smooth(u, v, aspect, 12, 3) * 0.12) + (Blocky(u, v, aspect, 60, 4) * 0.08);
        return (0.18 + n, 0.3 + n, 0.12 + (n * 0.5));
    };

    public static Shader MirrorScene(double aspect) => (u, v) =>
    {
        double mu = 0.5 + Math.Abs(u - 0.5);
        double n = Smooth(mu, v, aspect, 6, 11);
        if (Dist(mu, v, 0.78, 0.45, aspect) < 0.12)
        {
            return (0.9, 0.8, 0.2);
        }

        if (v > 0.7 && mu < 0.6)
        {
            return (0.15, 0.15, 0.5 + (0.3 * n));
        }

        return (0.3 + (0.4 * n), 0.45 + (0.2 * n), 0.5);
    };

    public static Shader NoiseScene(double aspect, int seed = 21) => (u, v) =>
    {
        double a = Blocky(u, v, aspect, 24, seed), b = Blocky(u, v, aspect, 24, seed + 1), c = Smooth(u, v, aspect, 5, seed + 2);
        return ((0.6 * a) + (0.3 * c), (0.5 * b) + (0.3 * c), (0.4 * a * b) + (0.4 * c));
    };

    /// <summary>Warm disc filled with fine sharp texture; background either blurred (smooth noise) or equally sharp.</summary>
    public static Shader SeparationScene(double aspect, bool sharpBackground) => (u, v) =>
    {
        if (Dist(u, v, 0.5, 0.5, aspect) <= 0.22)
        {
            double t = Blocky(u, v, aspect, 80, 5);
            return (0.6 + (0.35 * t), 0.3 + (0.3 * t), 0.15 + (0.15 * t));
        }

        double n = sharpBackground ? Blocky(u, v, aspect, 80, 6) : Smooth(u, v, aspect, 5, 6);
        return (0.12 + (0.25 * n), 0.25 + (0.3 * n), 0.4 + (0.35 * n));
    };

    public static Shader EmptyScene(double aspect) => (u, v) =>
        Dist(u, v, 2.0 / 3.0, 2.0 / 3.0, aspect) <= 0.035 ? (0.1, 0.1, 0.1) : (0.86, 0.87, 0.9);

    public static Shader VerticalStripes() => (u, _) =>
        Math.Sin(2 * Math.PI * u * 10) > 0 ? (0.95, 0.95, 0.95) : (0.05, 0.05, 0.05);

    public static Shader HorizontalStripes() => (_, v) =>
        Math.Sin(2 * Math.PI * v * 8) > 0 ? (0.95, 0.95, 0.95) : (0.05, 0.05, 0.05);

    public static Shader Flat(double r, double g, double b) => (_, _) => (r, g, b);

    /// <summary>White page with UI panels and grey "text" lines.</summary>
    public static Shader ScreenshotScene() => (u, v) =>
    {
        if (v < 0.08)
        {
            return (0.93, 0.93, 0.95);
        }

        if (u < 0.2)
        {
            return (0.96, 0.96, 0.97);
        }

        double line = (v * 40) - Math.Floor(v * 40);
        bool text = v > 0.15 && v < 0.85 && u > 0.25 && u < 0.9 && line > 0.35 && line < 0.6
            && Blocky(u, v, 1, 120, 9) > 0.25;
        return text ? (0.25, 0.25, 0.28) : (1.0, 1.0, 1.0);
    };

    /// <summary>Dark sky with sparse stars.</summary>
    public static Shader StarScene(double aspect) => (u, v) =>
    {
        double s = Hash((int)(u * aspect * 300), (int)(v * 300), 77);
        return s > 0.995 ? (0.9, 0.9, 0.95) : (0.02, 0.03, 0.06);
    };

    private static GenreTaxonomy LoadTaxonomy()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "data", "taxonomy", "genres.json");
            if (File.Exists(candidate))
            {
                return JsonSerializer.Deserialize<GenreTaxonomy>(File.ReadAllText(candidate), ContractJson.Options)
                    ?? throw new InvalidDataException("Empty taxonomy.");
            }
        }

        throw new FileNotFoundException("data/taxonomy/genres.json not found above the test output directory.");
    }
}
