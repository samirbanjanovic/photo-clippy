using PhotoClippy.Contracts;
using PhotoClippy.Core.Normalization;

namespace PhotoClippy.Core.Tests;

public sealed class CameraNamesTests
{
    [Theory]
    [InlineData("NIKON CORPORATION", "Nikon")]
    [InlineData("SONY", "Sony")]
    [InlineData("FUJIFILM", "Fujifilm")]
    [InlineData("OM Digital Solutions", "OM System")]
    [InlineData("  Canon  ", "Canon")]
    [InlineData("ACME IMAGING CORPORATION", "Acme")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Normalizes_makes(string? raw, string? expected) => Assert.Equal(expected, CameraNames.NormalizeMake(raw));

    [Theory]
    [InlineData("Canon", "Canon EOS R5", "EOS R5")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6_2", "Z 6 II")]
    [InlineData("NIKON CORPORATION", "NIKON Z 7_3", "Z 7 III")]
    [InlineData("FUJIFILM", "X-T3", "X-T3")]
    [InlineData("Apple", "iPhone 13", "iPhone 13")]
    public void Normalizes_models(string make, string model, string expected) => Assert.Equal(expected, CameraNames.NormalizeModel(make, model));

    [Fact]
    public void Body_display_combines_make_and_model()
    {
        Assert.Equal("Nikon Z 6 II", CameraNames.BodyDisplay("NIKON CORPORATION", "NIKON Z 6_2"));
        Assert.Equal("X-T3", CameraNames.BodyDisplay(null, "X-T3"));
        Assert.Equal("Sony", CameraNames.BodyDisplay("SONY", null));
        Assert.Null(CameraNames.BodyDisplay(null, " "));
    }

    [Theory]
    [InlineData("0.0 mm f/0.0")]
    [InlineData("Unknown")]
    [InlineData("----")]
    [InlineData("n/a")]
    [InlineData("none")]
    [InlineData("")]
    public void Placeholder_lenses_are_null(string raw) => Assert.Null(CameraNames.LensDisplay(raw));

    [Fact]
    public void Real_lens_names_are_cleaned_but_kept()
    {
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", CameraNames.LensDisplay("  NIKKOR Z  24-70mm f/4 S\0"));
    }

    [Theory]
    [InlineData("Nikon Z 6 II", "nikon-z-6-ii")]
    [InlineData("NIKKOR Z 24-70mm f/4 S", "nikkor-z-24-70mm-f/4-s")]
    [InlineData("  EF 70–200 (II) ", "ef-70-200-ii")]
    [InlineData("XF35mm F1.4 R", "xf35mm-f1.4-r")]
    public void Slugs_are_stable_keys(string text, string expected) => Assert.Equal(expected, CameraNames.Slug(text));
}

public sealed class DeviceClassifierTests
{
    [Theory]
    [InlineData("Apple", "iPhone 13", DeviceClass.Phone)]
    [InlineData("Google", "Pixel 7", DeviceClass.Phone)]
    [InlineData("Samsung", "SM-G991B", DeviceClass.Phone)]
    [InlineData("Samsung", "NX500", DeviceClass.InterchangeableLens)]
    [InlineData("Sony", "XQ-BC72", DeviceClass.Phone)]
    [InlineData("DJI", "FC3582", DeviceClass.Drone)]
    [InlineData("DJI", "Osmo Pocket 3", DeviceClass.FixedLens)]
    [InlineData("Fujifilm", "X100V", DeviceClass.FixedLens)]
    [InlineData("Sony", "DSC-RX100M7", DeviceClass.FixedLens)]
    [InlineData("Canon", "PowerShot G7 X Mark III", DeviceClass.FixedLens)]
    [InlineData("Nikon", "COOLPIX P1000", DeviceClass.FixedLens)]
    [InlineData("Ricoh", "GR III", DeviceClass.FixedLens)]
    [InlineData("Nikon", "Nikon Z 6 II", DeviceClass.InterchangeableLens)]
    [InlineData("Fujifilm", "X-T3", DeviceClass.InterchangeableLens)]
    [InlineData("Nikon", "Nikon SUPER COOLSCAN 5000 ED", DeviceClass.Scanner)]
    public void Classifies_by_make_and_model(string make, string model, DeviceClass expected)
    {
        Assert.Equal(expected, DeviceClassifier.Classify(make, model, null, hasExposure: true, cropFactor: 1.0, hasLensInfo: true));
    }

    [Fact]
    public void Small_sensors_are_fixed_lens()
    {
        Assert.Equal(DeviceClass.FixedLens, DeviceClassifier.Classify("Acme", "Cam 1", null, true, 5.6, false));
        Assert.Equal(DeviceClass.InterchangeableLens, DeviceClassifier.Classify("Acme", "Cam 1", null, true, 1.5, false));
    }

    [Fact]
    public void Missing_make_and_model_uses_software_and_exposure()
    {
        Assert.Equal(DeviceClass.Scanner, DeviceClassifier.Classify(null, null, "VueScan 9.7", false, null, false));
        Assert.Equal(DeviceClass.Unknown, DeviceClassifier.Classify(null, null, null, true, null, false));
        Assert.Equal(DeviceClass.Software, DeviceClassifier.Classify(null, null, "Photoshop", false, null, false));
    }

    [Theory]
    [InlineData(0.79, "medium-format", "Medium format")]
    [InlineData(1.0, "full-frame", "Full frame")]
    [InlineData(1.29, "aps-h", "APS-H")]
    [InlineData(1.53, "aps-c", "APS-C")]
    [InlineData(2.0, "mft", "Micro Four Thirds")]
    [InlineData(2.7, "1-inch", "1-inch")]
    [InlineData(5.6, "small-sensor", "Small sensor")]
    public void Sensor_format_from_crop(double crop, string format, string display)
    {
        Assert.Equal(format, SensorFormats.FromCrop(crop));
        Assert.Equal(display, SensorFormats.Display(format));
    }
}

public sealed class CaptureBuilderTests
{
    private static readonly DateTime T0 = new(2023, 5, 1, 10, 0, 0);

    private static FileFacts Facts(PhotoMetadata meta, MediaKind kind = MediaKind.Jpeg) => CaptureBuilder.ToFileFacts(TestData.Src(meta.Path, kind), meta);

    [Fact]
    public void Missing_metadata_is_an_error()
    {
        var facts = CaptureBuilder.ToFileFacts(TestData.Src("/lib/a.jpg"), null);
        Assert.True(facts.MetadataError);
        Assert.False(facts.HasExposure);
    }

    [Fact]
    public void Unreadable_file_with_only_warnings_is_an_error_but_bare_metadata_is_not()
    {
        Assert.True(Facts(new PhotoMetadata { Path = "/lib/a.jpg", Warnings = ["corrupt"] }).MetadataError);
        Assert.False(Facts(new PhotoMetadata { Path = "/lib/b.jpg", Warnings = ["corrupt"], Width = 100 }).MetadataError);
        Assert.False(Facts(new PhotoMetadata { Path = "/lib/c.jpg" }).MetadataError);
    }

    [Fact]
    public void Maps_names_exposure_and_lens()
    {
        var f = Facts(TestData.NikonMeta("/lib/a.jpg", T0, focal: 50, fNumber: 4, iso: 800));

        Assert.Equal("Nikon", f.Make);
        Assert.Equal("Nikon Z 6 II", f.BodyDisplay);
        Assert.Equal("nikon-z-6-ii", f.BodyKey);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", f.LensDisplay);
        Assert.Equal(50, f.FocalMm);
        Assert.Equal(50, f.Focal35Exif);
        Assert.Equal(4, f.FNumber);
        Assert.Equal(800, f.Iso);
        Assert.Equal(T0, f.CaptureLocal);
        Assert.True(f.HasExposure);
        Assert.False(f.IsEdited);
    }

    [Fact]
    public void Out_of_range_values_are_dropped()
    {
        var f = Facts(new PhotoMetadata
        {
            Path = "/lib/a.jpg",
            Make = "Canon",
            FNumber = 0.2,
            ExposureTimeS = -1,
            Iso = 0,
            FocalLengthMm = 0,
            CaptureLocal = new DateTime(1800, 1, 1),
            LensSpec = new LensSpecification(70, 24, 4, 4),
        });

        Assert.Null(f.FNumber);
        Assert.Null(f.ExposureS);
        Assert.Null(f.Iso);
        Assert.Null(f.FocalMm);
        Assert.Null(f.CaptureLocal);
        Assert.Null(f.LensSpec);
        Assert.False(f.HasExposure);
    }

    [Theory]
    [InlineData(1, 6000, 4000)]
    [InlineData(6, 4000, 6000)]
    [InlineData(8, 4000, 6000)]
    public void Rotated_orientations_swap_dimensions(int orientation, int width, int height)
    {
        var f = Facts(new PhotoMetadata { Path = "/lib/a.jpg", Width = 6000, Height = 4000, Orientation = orientation });
        Assert.Equal(width, f.Width);
        Assert.Equal(height, f.Height);
    }

    [Fact]
    public void Editor_software_marks_exports()
    {
        Assert.True(Facts(TestData.NikonMeta("/lib/a.jpg") with { Software = "Adobe Photoshop Lightroom Classic 13.0" }).IsEdited);
        Assert.False(Facts(TestData.NikonMeta("/lib/b.jpg") with { Software = "Ver.1.40" }).IsEdited);
    }

    [Fact]
    public void Builds_captures_sorted_by_date_with_resolved_body_and_lens()
    {
        var files = new[]
        {
            Facts(TestData.NikonMeta("/lib/c.jpg", T0.AddHours(2), focal: 70)),
            Facts(TestData.NikonMeta("/lib/a.jpg", T0, focal: 24)),
            Facts(TestData.NikonMeta("/lib/b.jpg", T0.AddHours(1), focal: 35)),
        };

        var set = CaptureBuilder.Build(files);

        Assert.Equal(new[] { "/lib/a.jpg", "/lib/b.jpg", "/lib/c.jpg" }, set.Captures.Select(c => c.Facts.PrimaryPath));
        var body = Assert.Single(set.Bodies).Value;
        Assert.Equal(DeviceClass.InterchangeableLens, body.Device);
        Assert.Equal(1.0, body.CropFactor);
        Assert.Equal("exif-35mm", body.CropSource);
        Assert.Equal("full-frame", body.Format);

        var lens = Assert.Single(set.LensSpecs);
        Assert.Equal("nikkor-z-24-70mm-f/4-s", lens.Key);
        Assert.Equal(LensSpecSource.Exif, lens.Value.Source);
        Assert.Equal(24, lens.Value.FocalMinMm);
        Assert.Equal(70, lens.Value.FocalMaxMm);
        Assert.True(lens.Value.IsZoom);

        var first = set.Captures[0].Facts;
        Assert.Equal(CaptureBuilder.StableId("/lib/a.jpg"), first.CaptureId);
        Assert.Equal(DeviceClass.InterchangeableLens, first.Device);
        Assert.Equal("nikkor-z-24-70mm-f/4-s", first.LensKey);
        Assert.True(first.LensIsZoom);
        Assert.Equal(3, set.FilesWithMetadata);
        Assert.Equal(0, set.MetadataErrors);
        Assert.Equal(0, set.RawPlusJpeg);
    }

    [Fact]
    public void Crop_factor_comes_from_35mm_equivalent_and_fills_missing_focal35()
    {
        var fuji = new PhotoMetadata
        {
            Path = "/lib/f1.jpg",
            Make = "FUJIFILM",
            Model = "X-T3",
            LensModel = "XF23mmF2 R WR",
            FocalLengthMm = 23,
            FocalLength35mm = 35,
            FNumber = 2,
            CaptureLocal = T0,
        };
        var noEquivalent = fuji with { Path = "/lib/f2.jpg", FocalLength35mm = null, CaptureLocal = T0.AddMinutes(1) };

        var set = CaptureBuilder.Build([Facts(fuji), Facts(noEquivalent)]);

        var body = set.Bodies["fujifilm-x-t3"];
        Assert.Equal(1.52, body.CropFactor);
        Assert.Equal("aps-c", body.Format);
        Assert.Equal(35, set.Captures[0].Facts.Focal35Mm);
        Assert.Equal(Math.Round(23 * 1.52, 1), set.Captures[1].Facts.Focal35Mm);
    }

    [Fact]
    public void Interchangeable_body_without_crop_evidence_defaults_to_full_frame()
    {
        var meta = TestData.NikonMeta("/lib/a.jpg") with { FocalLength35mm = null };
        var set = CaptureBuilder.Build([Facts(meta)]);
        var body = Assert.Single(set.Bodies).Value;
        Assert.Equal(1.0, body.CropFactor);
        Assert.Null(body.CropSource);
        Assert.Equal(35, set.Captures[0].Facts.Focal35Mm);
    }

    [Fact]
    public void Raw_and_jpeg_pairs_merge_into_one_capture()
    {
        var raw = Facts(TestData.NikonMeta("/lib/DSC_0001.NEF", T0), MediaKind.Raw);
        var jpg = Facts(TestData.NikonMeta("/lib/DSC_0001.JPG", T0.AddSeconds(1)));
        var other = Facts(TestData.NikonMeta("/lib/DSC_0002.JPG", T0.AddMinutes(5)));

        var set = CaptureBuilder.Build([jpg, raw, other]);

        Assert.Equal(2, set.Captures.Count);
        Assert.Equal(1, set.RawPlusJpeg);
        var merged = set.Captures[0];
        Assert.Equal("/lib/DSC_0001.NEF", merged.Facts.PrimaryPath);
        Assert.Equal(2, merged.Facts.Paths.Count);
        Assert.Equal(MediaKind.Jpeg, merged.FrameCandidates[0].Kind);
    }

    [Fact]
    public void Same_stem_far_apart_in_time_is_not_merged()
    {
        var a = Facts(TestData.NikonMeta("/lib/x/IMG_1.jpg", T0));
        var b = Facts(TestData.NikonMeta("/lib/x/IMG_1.NEF", T0.AddHours(1)), MediaKind.Raw);

        var set = CaptureBuilder.Build([a, b]);

        Assert.Equal(2, set.Captures.Count);
        Assert.Equal(0, set.RawPlusJpeg);
    }

    [Fact]
    public void Edited_exports_of_the_same_instant_merge()
    {
        var original = Facts(TestData.NikonMeta("/lib/DSC_0001.jpg", T0));
        var export = Facts(TestData.NikonMeta("/exports/final.jpg", T0) with { Software = "Adobe Photoshop Lightroom Classic" });

        var set = CaptureBuilder.Build([original, export]);

        var capture = Assert.Single(set.Captures);
        Assert.Equal(1, set.MergedExports);
        Assert.Equal("/lib/DSC_0001.jpg", capture.Facts.PrimaryPath);
    }

    [Fact]
    public void Phone_captures_get_a_built_in_lens_key()
    {
        var set = CaptureBuilder.Build([Facts(TestData.PhoneMeta("/lib/IMG_0001.jpg"))]);
        var facts = Assert.Single(set.Captures).Facts;

        Assert.Equal(DeviceClass.Phone, facts.Device);
        Assert.StartsWith(CaptureBuilder.FixedLensPrefix, facts.LensKey, StringComparison.Ordinal);
        Assert.Equal(26, facts.Focal35Mm);
        Assert.Equal("small-sensor", set.Bodies["apple-iphone-13"].Format);
    }

    [Fact]
    public void Unnamed_lens_with_spec_gets_a_synthetic_key()
    {
        var meta = TestData.NikonMeta("/lib/a.jpg") with { LensModel = null };
        var facts = Assert.Single(CaptureBuilder.Build([Facts(meta)]).Captures).Facts;

        Assert.StartsWith(CaptureBuilder.UnknownLensPrefix + ":", facts.LensKey, StringComparison.Ordinal);
        Assert.Equal("Unnamed lens 24-70mm f/4", facts.LensDisplay);
    }

    [Fact]
    public void Metadata_errors_are_counted_but_still_form_captures()
    {
        var bad = CaptureBuilder.ToFileFacts(TestData.Src("/lib/bad.jpg"), null);
        var good = Facts(TestData.NikonMeta("/lib/good.jpg", T0));

        var set = CaptureBuilder.Build([bad, good]);

        Assert.Equal(2, set.Captures.Count);
        Assert.Equal(1, set.MetadataErrors);
        Assert.Equal(1, set.FilesWithMetadata);
        Assert.Contains(set.Captures, c => c.Facts.Device == DeviceClass.Software);
    }

    [Fact]
    public void Stable_ids_are_deterministic_hex()
    {
        var id = CaptureBuilder.StableId("/lib/a.jpg");
        Assert.Equal(16, id.Length);
        Assert.Equal(id, CaptureBuilder.StableId("/lib/a.jpg"));
        Assert.NotEqual(id, CaptureBuilder.StableId("/lib/b.jpg"));
        Assert.Matches("^[0-9a-f]+$", id);
    }
}
