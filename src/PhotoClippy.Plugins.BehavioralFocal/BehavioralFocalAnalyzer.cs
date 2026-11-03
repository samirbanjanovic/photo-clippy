using System.Security.Cryptography;
using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.BehavioralFocal;

public sealed class BehavioralFocalAnalyzer : ICompositionAnalyzer, IContextFingerprintProvider
{
    public const string PluginId = "builtin.composition.behavioral-focal";
    public const int AlgorithmRevision = 1;

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Behavioral focal framing",
        Version = "1.0.0",
        Kind = ComponentKind.Composition,
        Description = "Describes verified crop geometry and same-viewpoint framing; does not infer a preferred lens.",
        Capabilities = ["behavioral-focal.verified-crop-geometry"],
        Fingerprint = $"crop-geometry-{AlgorithmRevision}",
    };

    public FrameRequirements Requirements { get; } = new(32);

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginHealth.Ok());
    }

    public ValueTask<CompositionFeatures> AnalyzeAsync(Frame frame, AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = context.Facts?.CropProvenanceRecords;
        if (evidence is not { Count: 1 })
        {
            return ValueTask.FromResult(new CompositionFeatures());
        }

        var result = CropGeometryCalculator.Calculate(context.Facts?.Focal35Mm, evidence[0]);
        if (result.Geometry is not { } geometry)
        {
            return ValueTask.FromResult(new CompositionFeatures());
        }

        var extras = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["composition.behavioralFocal.pixelsRetained"] = geometry.PixelsRetained,
            ["composition.behavioralFocal.cropDependence"] = geometry.CropDependence,
            ["composition.behavioralFocal.retainedWidth"] = geometry.RetainedWidth,
            ["composition.behavioralFocal.retainedHeight"] = geometry.RetainedHeight,
            ["composition.behavioralFocal.horizontalEquivalent35"] = geometry.HorizontalEquivalentFocal35Mm,
            ["composition.behavioralFocal.verticalEquivalent35"] = geometry.VerticalEquivalentFocal35Mm,
            ["composition.behavioralFocal.cropLeft"] = geometry.Left,
            ["composition.behavioralFocal.cropTop"] = geometry.Top,
            ["composition.behavioralFocal.cropRight"] = geometry.Right,
            ["composition.behavioralFocal.cropBottom"] = geometry.Bottom,
        };
        if (geometry.FinalEquivalentFocal35Mm is { } finalEquivalent)
        {
            extras["composition.behavioralFocal.finalEquivalent35"] = finalEquivalent;
        }

        return ValueTask.FromResult(new CompositionFeatures { Extras = extras });
    }

    public string GetContextFingerprint(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = JsonSerializer.SerializeToUtf8Bytes(
            new { context.Facts?.Focal35Mm, context.Facts?.CropProvenanceRecords },
            ContractJson.Options);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
