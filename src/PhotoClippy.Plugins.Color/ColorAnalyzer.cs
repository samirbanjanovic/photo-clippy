using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Color;

/// <summary>
/// <c>builtin.color</c>: tonal and colour analytics (OKLab lightness distribution, clipping, key, colourfulness,
/// chroma, cast, sharpness, hue histogram and dominant palette). Stateless and thread-safe; each call uses its
/// own pooled buffers. See <see cref="ColorComputation"/> for the exact metric definitions.
/// </summary>
public sealed class ColorAnalyzer : IColorAnalyzer
{
    public const string PluginId = "builtin.color";
    public const string PluginVersion = "1.0.0";

    /// <summary>Bump when the metric definitions change without a settings change.</summary>
    private const string AlgorithmRevision = "color-algo-1";

    private readonly ColorSettings _settings;

    public ColorAnalyzer(ColorSettings? settings = null)
    {
        _settings = (settings ?? new ColorSettings()).Normalize();
        Requirements = new FrameRequirements(_settings.WorkingEdge);
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Color & tone",
            Version = PluginVersion,
            Kind = ComponentKind.Color,
            Description = "OKLab tonal and colour analytics: lightness percentiles, clipping, key, colourfulness, cast, sharpness, hue histogram and dominant palette.",
            Capabilities = ["color.tone", "color.clipping", "color.colorfulness", "color.cast", "color.sharpness", "color.hue-histogram", "color.palette", "color.extras"],
            Fingerprint = ComputeFingerprint(_settings),
        };
    }

    /// <summary>Effective (normalized) settings.</summary>
    public ColorSettings Settings => _settings;

    public PluginManifest Manifest { get; }

    public FrameRequirements Requirements { get; }

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PluginHealth.Ok());

    public ValueTask<ColorFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(frame));
    }

    private ColorFeatures Analyze(Frame frame)
    {
        var (w, h) = AreaResampler.TargetSize(frame.Width, frame.Height, _settings.WorkingEdge);
        if (w == frame.Width && h == frame.Height)
        {
            return ColorComputation.Compute(frame.Rgb.Span, w, h, _settings);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(w * h * 3);
        try
        {
            var working = buffer.AsSpan(0, w * h * 3);
            AreaResampler.Resize(frame.Rgb.Span, frame.Width, frame.Height, working, w, h);
            return ColorComputation.Compute(working, w, h, _settings);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ComputeFingerprint(ColorSettings s)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{AlgorithmRevision};edge={s.WorkingEdge};k={s.PaletteSize};samples={s.MaxPaletteSamples}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }
}
