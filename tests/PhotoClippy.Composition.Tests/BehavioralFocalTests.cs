using PhotoClippy.Contracts;
using PhotoClippy.Plugins.BehavioralFocal;

namespace PhotoClippy.Composition.Tests;

public sealed class BehavioralFocalTests
{
    [Fact]
    public void Same_aspect_crop_reports_focal_equivalence_and_pixel_retention()
    {
        var result = CropGeometryCalculator.Calculate(24, VerifiedCrop());

        var geometry = Assert.IsType<CropGeometry>(result.Geometry);
        Assert.Equal(50, geometry.FinalEquivalentFocal35Mm!.Value, 8);
        Assert.Equal(0.48 * 0.48, geometry.PixelsRetained, 8);
        Assert.Equal(1 - 0.48 * 0.48, geometry.CropDependence, 8);
        Assert.Equal(0.26, geometry.Left, 8);
        Assert.Equal(0.74, geometry.Right, 8);
    }

    [Fact]
    public void Sensor_normalization_requires_finite_positive_inputs()
    {
        Assert.Equal(24, CropGeometryCalculator.NormalizeFocalEquivalent35Mm(16, 1.5));
        Assert.Null(CropGeometryCalculator.NormalizeFocalEquivalent35Mm(0, 1.5));
        Assert.Null(CropGeometryCalculator.NormalizeFocalEquivalent35Mm(double.NaN, 1.5));
        Assert.Null(CropGeometryCalculator.NormalizeFocalEquivalent35Mm(16, double.PositiveInfinity));
    }

    [Fact]
    public void Rotated_orientations_are_normalized_before_aspect_validation()
    {
        var evidence = VerifiedCrop() with
        {
            OriginalWidth = 4000,
            OriginalHeight = 6000,
            OriginalOrientation = 6,
            FinalWidth = 2000,
            FinalHeight = 3000,
            FinalOrientation = 6,
        };

        Assert.True(CropGeometryCalculator.Calculate(24, evidence).IsEligible);
        Assert.Equal("crop-aspect-orientation-mismatch",
            CropGeometryCalculator.Calculate(24, evidence with { FinalOrientation = 1 }).ExclusionReason);
    }

    [Fact]
    public void Unequal_axis_crops_do_not_report_a_single_equivalent_focal_length()
    {
        var evidence = VerifiedCrop() with
        {
            OriginalWidth = 1500,
            OriginalHeight = 1000,
            Right = 0.76,
            Bottom = 0.51,
            FinalWidth = 3000,
            FinalHeight = 1000,
        };

        var geometry = Assert.IsType<CropGeometry>(CropGeometryCalculator.Calculate(24, evidence).Geometry);
        Assert.Null(geometry.FinalEquivalentFocal35Mm);
        Assert.Equal(48, geometry.HorizontalEquivalentFocal35Mm, 8);
        Assert.Equal(96, geometry.VerticalEquivalentFocal35Mm, 8);
        Assert.Equal(0.125, geometry.PixelsRetained, 8);
    }

    [Fact]
    public void Already_included_digital_crop_is_not_counted_twice()
    {
        var evidence = VerifiedCrop() with { FocalEquivalentIncludesCrop = true };

        var geometry = Assert.IsType<CropGeometry>(CropGeometryCalculator.Calculate(24, evidence).Geometry);
        Assert.Equal(24, geometry.FinalEquivalentFocal35Mm);
        Assert.Equal(0.48 * 0.48, geometry.PixelsRetained, 8);
    }

    [Theory]
    [InlineData(CropEvidenceStatus.Unsupported, "crop-evidence-unsupported")]
    [InlineData(CropEvidenceStatus.Ambiguous, "crop-evidence-ambiguous")]
    [InlineData(CropEvidenceStatus.Invalid, "crop-evidence-invalid")]
    public void Unsupported_or_ambiguous_evidence_is_excluded(CropEvidenceStatus status, string reason)
    {
        var result = CropGeometryCalculator.Calculate(24, VerifiedCrop() with { Status = status });

        Assert.False(result.IsEligible);
        Assert.Equal(reason, result.ExclusionReason);
    }

    [Fact]
    public void Missing_evidence_and_invalid_geometry_are_not_zero_measurements()
    {
        Assert.Equal("crop-provenance-absent", CropGeometryCalculator.Calculate(24, null).ExclusionReason);
        Assert.Equal("crop-bounds-invalid", CropGeometryCalculator.Calculate(24, VerifiedCrop() with { Left = double.NaN }).ExclusionReason);
        Assert.Equal("crop-dimensions-or-orientation-invalid",
            CropGeometryCalculator.Calculate(24, VerifiedCrop() with { OriginalWidth = 0 }).ExclusionReason);
        Assert.Equal("crop-link-or-session-identity-missing",
            CropGeometryCalculator.Calculate(24, VerifiedCrop() with { IndependentSessionId = null }).ExclusionReason);
        Assert.Equal("focal-normalization-missing-or-invalid", CropGeometryCalculator.Calculate(0, VerifiedCrop()).ExclusionReason);
        Assert.Equal("crop-link-or-session-identity-missing",
            CropGeometryCalculator.Calculate(24, VerifiedCrop() with { OriginalCaptureId = "/photos/original.jpg" }).ExclusionReason);
    }

    [Fact]
    public async Task Plugin_is_opt_in_deterministic_cancellable_and_namespaced()
    {
        var factory = new BehavioralFocalFactory();
        var plugin = Assert.IsType<BehavioralFocalAnalyzer>(factory.Create(new PluginContext
        {
            DataDirectory = string.Empty,
            ModelsDirectory = string.Empty,
            StateDirectory = string.Empty,
            LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Taxonomy = new GenreTaxonomy(),
        }));
        var frame = new Frame(1, 1, new byte[3], 1, 1);
        var facts = new CaptureFacts
        {
            CaptureId = "capture-1",
            PrimaryPath = "capture.jpg",
            Focal35Mm = 24,
            CropProvenanceRecords = [VerifiedCrop()],
        };
        var context = new AnalysisContext { CaptureId = facts.CaptureId, SourcePath = facts.PrimaryPath, Facts = facts };

        var first = await plugin.AnalyzeAsync(frame, context);
        var second = await plugin.AnalyzeAsync(frame, context);

        Assert.Equal(first.Extras, second.Extras);
        Assert.All(first.Extras.Keys, key => Assert.StartsWith("composition.behavioralFocal.", key, StringComparison.Ordinal));
        Assert.DoesNotContain("subjectArea", first.Extras.Keys);
        Assert.Empty((await plugin.AnalyzeAsync(frame, context with
        {
            Facts = facts with { CropProvenanceRecords = [] },
        })).Extras);
        Assert.Equal(plugin.GetContextFingerprint(context), plugin.GetContextFingerprint(context));
        Assert.NotEqual(plugin.GetContextFingerprint(context),
            plugin.GetContextFingerprint(context with { Facts = facts with { CropProvenanceRecords = [VerifiedCrop() with { Right = 0.75 }] } }));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => plugin.AnalyzeAsync(frame, context).AsTask()));
        Assert.All(concurrent, result => Assert.Equal(first.Extras, result.Extras));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.AnalyzeAsync(frame, context, cancelled.Token).AsTask());
    }

    private static CropProvenance VerifiedCrop() => new()
    {
        Status = CropEvidenceStatus.Verified,
        Source = CropEvidenceSource.ExternalMetadataPlugin,
        OriginalCaptureId = "original-1",
        FinalCaptureId = "final-1",
        IndependentSessionId = "session-1",
        OriginalWidth = 1000,
        OriginalHeight = 1000,
        OriginalOrientation = 1,
        FinalWidth = 480,
        FinalHeight = 480,
        FinalOrientation = 1,
        Left = 0.26,
        Top = 0.26,
        Right = 0.74,
        Bottom = 0.74,
        FocalEquivalentIncludesCrop = false,
    };
}
