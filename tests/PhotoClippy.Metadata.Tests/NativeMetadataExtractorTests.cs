using System.Diagnostics;
using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Metadata;
using PhotoClippy.Testing;
using Xunit.Abstractions;

namespace PhotoClippy.Metadata.Tests;

public sealed class NativeMetadataExtractorTests(ITestOutputHelper output) : IDisposable
{
    internal static readonly ExifSpec FullSpec = new()
    {
        Make = "  NIKON CORPORATION ",
        Model = "NIKON  Z 6_2",
        Software = "Ver.01.50 ",
        BodySerial = "6012345",
        LensMake = "Nikon",
        LensModel = "NIKKOR Z 24-70mm f/4 S",
        LensSpec = (24, 70, 4, 4),
        FocalLength = 35,
        FocalLength35 = 35,
        FNumber = 5.6,
        ExposureTime = 1.0 / 250,
        Iso = 400,
        ExposureBias = -0.7,
        ExposureProgram = 3,
        Flash = 0x19,
        DateTimeOriginal = new DateTime(2024, 5, 1, 13, 45, 10),
        SubSec = "123",
        OffsetTime = "+02:00",
        ImageUniqueId = "ABCDEF0123456789",
        Orientation = 6,
        PixelWidth = 6048,
        PixelHeight = 4024,
        FocalPlaneXResolutionPerMm = 167.2,
        Gps = (-33.8568, 151.2153),
        Rating = 4,
    };

    private readonly FixtureDirectory _dir = new("native");
    private readonly NativeMetadataExtractor _extractor = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Maps_every_field_written_by_ExifWriter()
    {
        var path = _dir.Write("full.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, FullSpec));

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Equal(path, m.Path);
        Assert.Equal(NativeMetadataExtractor.PluginId, m.Extractor);
        Assert.Equal("NIKON CORPORATION", m.Make);
        Assert.Equal("NIKON Z 6_2", m.Model);
        Assert.Equal("Ver.01.50", m.Software);
        Assert.Equal("6012345", m.BodySerial);
        Assert.Equal("Nikon", m.LensMake);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", m.LensModel);
        Assert.Equal(new LensSpecification(24, 70, 4, 4), m.LensSpec);
        Assert.Equal(35, m.FocalLengthMm);
        Assert.Equal(35, m.FocalLength35mm);
        Assert.Equal(5.6, m.FNumber);
        Assert.Equal(1.0 / 250, m.ExposureTimeS!.Value, 12);
        Assert.Equal(400, m.Iso);
        Assert.Equal(-0.7, m.ExposureBiasEv!.Value, 6);
        Assert.Equal("aperture-priority", m.ExposureProgram);
        Assert.True(m.FlashFired);
        Assert.Equal(new DateTime(2024, 5, 1, 13, 45, 10), m.CaptureLocal);
        Assert.Equal(DateTimeKind.Unspecified, m.CaptureLocal!.Value.Kind);
        Assert.Equal("+02:00", m.OffsetTime);
        Assert.Equal("123", m.SubSec);
        Assert.Equal("ABCDEF0123456789", m.ImageUniqueId);
        Assert.Equal(6, m.Orientation);
        Assert.Equal(6048, m.Width);
        Assert.Equal(4024, m.Height);
        Assert.Equal(167.2, m.FocalPlaneXResolutionPerMm!.Value, 6);
        Assert.Equal(4, m.Rating);
        Assert.True(m.HasGps);
        Assert.Equal(-33.8568, m.Latitude!.Value, 4);
        Assert.Equal(151.2153, m.Longitude!.Value, 4);
        Assert.Null(m.MeteringMode);
        Assert.Null(m.WhiteBalance);
        Assert.Null(m.SubjectDistanceM);
        Assert.Equal("JPEG", m.Extras["fileType"]);
        Assert.False(m.Extras.ContainsKey("isoSource"));
        Assert.False(m.Extras.ContainsKey("captureSource"));
    }

    [Theory]
    [InlineData(1.0 / 8000)]
    [InlineData(1.0 / 250)]
    [InlineData(0.5)]
    [InlineData(30.0)]
    public async Task Exposure_times_are_exact_seconds(double seconds)
    {
        var path = _dir.Write("exp.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { ExposureTime = seconds }));

        var m = await ExtractOne(path);

        Assert.Equal(seconds, m.ExposureTimeS!.Value, 12);
    }

    [Fact]
    public async Task Zoom_lens_specification_keeps_variable_aperture()
    {
        var spec = new ExifSpec { LensModel = "18-55mm F3.5-5.6", LensSpec = (18, 55, 3.5, 5.6) };
        var path = _dir.Write("zoom.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec));

        var m = await ExtractOne(path);

        Assert.Equal(new LensSpecification(18, 55, 3.5, 5.6), m.LensSpec);
        Assert.Equal("18-55mm F3.5-5.6", m.LensModel);
    }

    [Fact]
    public async Task Lens_specification_with_unknown_apertures_has_null_members()
    {
        var tiff = new TiffBuilder()
            .Rational(Ifd.Exif, 0xA432, (24, 1), (105, 1), (0, 0), (0, 1))
            .Build();
        var path = _dir.Write("spec00.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(new LensSpecification(24, 105, null, null), m.LensSpec);
    }

    [Fact]
    public async Task Lens_specification_without_focal_range_is_null()
    {
        var tiff = new TiffBuilder()
            .Rational(Ifd.Exif, 0xA432, (0, 0), (0, 0), (0, 0), (0, 0))
            .Build();
        var path = _dir.Write("spec-zero.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Null(m.LensSpec);
    }

    [Fact]
    public async Task Zero_and_placeholder_values_are_unknown()
    {
        var spec = new ExifSpec
        {
            Make = "Canon",
            BodySerial = "0000000000",
            LensModel = "----",
            LensSpec = (0, 0, 0, 0),
            FocalLength = 0,
            FocalLength35 = 0,
            FNumber = 0,
            ExposureTime = 0,
            Iso = 0,
            Orientation = 0,
            PixelWidth = 0,
            PixelHeight = 0,
            ImageUniqueId = "00000000000000000000000000000000",
            FocalPlaneXResolutionPerMm = 0,
            Gps = (0, 0),
        };
        var path = _dir.Write("zeros.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec));

        var m = await ExtractOne(path);

        Assert.Equal("Canon", m.Make);
        Assert.Null(m.BodySerial);
        Assert.Null(m.LensModel);
        Assert.Null(m.LensSpec);
        Assert.Null(m.FocalLengthMm);
        Assert.Null(m.FocalLength35mm);
        Assert.Null(m.FNumber);
        Assert.Null(m.ExposureTimeS);
        Assert.Null(m.Iso);
        Assert.Null(m.Orientation);
        Assert.Null(m.ImageUniqueId);
        Assert.Null(m.FocalPlaneXResolutionPerMm);
        Assert.False(m.HasGps);
        Assert.Null(m.Latitude);
        Assert.Null(m.Longitude);

        // Zero EXIF pixel dimensions fall back to the JPEG frame.
        Assert.Equal(64, m.Width);
        Assert.Equal(48, m.Height);
    }

    [Fact]
    public async Task Jpeg_without_metadata_yields_nulls_and_frame_dimensions()
    {
        var path = _dir.Write("plain.jpg", Fixtures.BaseJpeg);

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Null(m.Make);
        Assert.Null(m.Model);
        Assert.Null(m.LensModel);
        Assert.Null(m.CaptureLocal);
        Assert.Null(m.Iso);
        Assert.Null(m.Orientation);
        Assert.False(m.HasGps);
        Assert.Equal(64, m.Width);
        Assert.Equal(48, m.Height);
    }

    [Fact]
    public async Task Png_dimensions_come_from_the_image_header()
    {
        using var bitmap = new SkiaSharp.SKBitmap(40, 30);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var path = _dir.Write("plain.png", data.ToArray());

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Equal(40, m.Width);
        Assert.Equal(30, m.Height);
        Assert.Equal("PNG", m.Extras["fileType"]);
    }

    [Theory]
    [InlineData(0x8832u, "recommendedExposureIndex")]
    [InlineData(0x8833u, "isoSpeed")]
    [InlineData(0x8831u, "standardOutputSensitivity")]
    public async Task Iso_falls_back_when_ISOSpeedRatings_is_saturated(uint tag, string source)
    {
        var tiff = new TiffBuilder()
            .Short(Ifd.Exif, 0x8827, 65535)
            .Short(Ifd.Exif, 0x8830, 2)
            .Long(Ifd.Exif, (ushort)tag, 102400)
            .Build();
        var path = _dir.Write("iso-" + source + ".jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(102400, m.Iso);
        Assert.Equal(source, m.Extras["isoSource"]);
    }

    [Fact]
    public async Task Iso_falls_back_when_ISOSpeedRatings_is_missing()
    {
        var tiff = new TiffBuilder().Long(Ifd.Exif, 0x8832, 800).Build();
        var path = _dir.Write("iso-rei.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(800, m.Iso);
        Assert.Equal("recommendedExposureIndex", m.Extras["isoSource"]);
    }

    [Fact]
    public async Task Saturated_iso_without_alternatives_is_kept_as_lower_bound()
    {
        var path = _dir.Write("iso-sat.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Iso = 204800 }));

        var m = await ExtractOne(path);

        Assert.Equal(65535, m.Iso);
        Assert.Equal("saturated", m.Extras["isoSource"]);
    }

    [Fact]
    public async Task Apex_values_are_used_when_FNumber_and_ExposureTime_are_missing()
    {
        var tiff = new TiffBuilder()
            .Rational(Ifd.Exif, 0x9202, (6, 1))
            .SRational(Ifd.Exif, 0x9201, 697, 100)
            .Build();
        var path = _dir.Write("apex.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(8, m.FNumber);
        Assert.Equal(1.0 / 125, m.ExposureTimeS!.Value, 12);
    }

    [Fact]
    public async Task Apex_long_exposure_is_rounded_to_a_tenth_of_a_second()
    {
        var tiff = new TiffBuilder()
            .Rational(Ifd.Exif, 0x9202, (497, 100))
            .SRational(Ifd.Exif, 0x9201, -4907, 1000)
            .Build();
        var path = _dir.Write("apex-long.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(5.6, m.FNumber);
        Assert.Equal(30.0, m.ExposureTimeS);
    }

    [Fact]
    public async Task FNumber_and_ExposureTime_take_precedence_over_apex()
    {
        var tiff = new TiffBuilder()
            .Rational(Ifd.Exif, 0x829A, (1, 60))
            .Rational(Ifd.Exif, 0x829D, (28, 10))
            .Rational(Ifd.Exif, 0x9202, (6, 1))
            .SRational(Ifd.Exif, 0x9201, 10, 1)
            .Build();
        var path = _dir.Write("apex-both.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(2.8, m.FNumber);
        Assert.Equal(1.0 / 60, m.ExposureTimeS!.Value, 12);
    }

    [Theory]
    [InlineData(1, "manual")]
    [InlineData(2, "program")]
    [InlineData(3, "aperture-priority")]
    [InlineData(4, "shutter-priority")]
    [InlineData(5, "creative")]
    [InlineData(6, "action")]
    [InlineData(7, "portrait")]
    [InlineData(8, "landscape")]
    [InlineData(9, "bulb")]
    [InlineData(0, null)]
    public async Task Exposure_program_is_a_lower_case_string(int value, string? expected)
    {
        var path = _dir.Write("ep.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { ExposureProgram = value }));

        var m = await ExtractOne(path);

        Assert.Equal(expected, m.ExposureProgram);
    }

    [Theory]
    [InlineData(1, "average")]
    [InlineData(2, "center-weighted")]
    [InlineData(3, "spot")]
    [InlineData(4, "multi-spot")]
    [InlineData(5, "multi-segment")]
    [InlineData(6, "partial")]
    [InlineData(255, "other")]
    [InlineData(0, null)]
    public async Task Metering_mode_is_a_lower_case_string(int value, string? expected)
    {
        var path = _dir.Write("mm.jpg", Fixtures.JpegWith(new TiffBuilder().Short(Ifd.Exif, 0x9207, (ushort)value).Build()));

        var m = await ExtractOne(path);

        Assert.Equal(expected, m.MeteringMode);
    }

    [Theory]
    [InlineData(0, "auto")]
    [InlineData(1, "manual")]
    [InlineData(7, null)]
    public async Task White_balance_is_auto_or_manual(int value, string? expected)
    {
        var path = _dir.Write("wb.jpg", Fixtures.JpegWith(new TiffBuilder().Short(Ifd.Exif, 0xA403, (ushort)value).Build()));

        var m = await ExtractOne(path);

        Assert.Equal(expected, m.WhiteBalance);
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x01, true)]
    [InlineData(0x10, false)]
    [InlineData(0x18, false)]
    [InlineData(0x19, true)]
    [InlineData(0x41, true)]
    public async Task Flash_fired_is_bit_zero(int flash, bool fired)
    {
        var path = _dir.Write("flash.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Flash = flash }));

        var m = await ExtractOne(path);

        Assert.Equal(fired, m.FlashFired);
    }

    [Fact]
    public async Task Capture_time_falls_back_to_CreateDate()
    {
        var tiff = new TiffBuilder()
            .Ascii(Ifd.Exif, 0x9003, "0000:00:00 00:00:00")
            .Ascii(Ifd.Exif, 0x9004, "2023:12:31 23:59:58")
            .Ascii(Ifd.Exif, 0x9012, "-05:00")
            .Ascii(Ifd.Exif, 0x9292, "42")
            .Ascii(Ifd.Zero, 0x0132, "2024:01:02 03:04:05")
            .Build();
        var path = _dir.Write("createdate.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(new DateTime(2023, 12, 31, 23, 59, 58), m.CaptureLocal);
        Assert.Equal("-05:00", m.OffsetTime);
        Assert.Equal("42", m.SubSec);
        Assert.Equal("createDate", m.Extras["captureSource"]);
    }

    [Fact]
    public async Task Capture_time_falls_back_to_ifd0_DateTime()
    {
        var tiff = new TiffBuilder()
            .Ascii(Ifd.Zero, 0x0132, "2024:01:02 03:04:05")
            .Ascii(Ifd.Exif, 0x9010, "+09:00")
            .Build();
        var path = _dir.Write("datetime.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), m.CaptureLocal);
        Assert.Equal("+09:00", m.OffsetTime);
        Assert.Equal("dateTime", m.Extras["captureSource"]);
    }

    [Fact]
    public async Task Zero_dates_are_ignored()
    {
        var tiff = new TiffBuilder()
            .Ascii(Ifd.Exif, 0x9003, "0000:00:00 00:00:00")
            .Ascii(Ifd.Exif, 0x9004, "    :  :     :  :  ")
            .Build();
        var path = _dir.Write("nodate.jpg", Fixtures.JpegWith(tiff));

        var m = await ExtractOne(path);

        Assert.Null(m.CaptureLocal);
        Assert.Null(m.OffsetTime);
    }

    [Theory]
    [InlineData(5000u, (ushort)2, 196.850394)]
    [InlineData(2000u, (ushort)3, 200.0)]
    [InlineData(200u, (ushort)4, 200.0)]
    [InlineData(5000u, (ushort)0, 196.850394)]
    public async Task Focal_plane_resolution_is_converted_to_pixels_per_mm(uint resolution, ushort unit, double expected)
    {
        var builder = new TiffBuilder().Rational(Ifd.Exif, 0xA20E, (resolution, 1));
        if (unit != 0)
        {
            builder.Short(Ifd.Exif, 0xA210, unit);
        }

        var path = _dir.Write("fpr.jpg", Fixtures.JpegWith(builder.Build()));

        var m = await ExtractOne(path);

        Assert.Equal(expected, m.FocalPlaneXResolutionPerMm!.Value, 5);
    }

    [Theory]
    [InlineData(350u, 100u, 3.5)]
    [InlineData(0u, 1u, null)]
    [InlineData(0xFFFFFFFFu, 1u, null)]
    public async Task Subject_distance_is_meters(uint num, uint den, double? expected)
    {
        var path = _dir.Write("sd.jpg", Fixtures.JpegWith(new TiffBuilder().Rational(Ifd.Exif, 0x9206, (num, den)).Build()));

        var m = await ExtractOne(path);

        Assert.Equal(expected, m.SubjectDistanceM);
    }

    [Fact]
    public async Task Gps_south_and_west_are_negative()
    {
        var path = _dir.Write("gps.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Gps = (-22.9519, -43.2105) }));

        var m = await ExtractOne(path);

        Assert.True(m.HasGps);
        Assert.Equal(-22.9519, m.Latitude!.Value, 4);
        Assert.Equal(-43.2105, m.Longitude!.Value, 4);
    }

    [Fact]
    public async Task Rating_and_lens_come_from_embedded_xmp_when_exif_lacks_them()
    {
        var xmp = Fixtures.Xmp("xmp:Rating=\"3\" aux:Lens=\"Samyang 12mm f/2.0 NCS CS\" aux:SerialNumber=\"XMP123\"");
        var tiff = ExifWriter.BuildTiff(new ExifSpec { Make = "FUJIFILM", Model = "X-T4" });
        var path = _dir.Write("xmp.jpg", Fixtures.JpegWith(tiff, xmp));

        var m = await ExtractOne(path);

        Assert.Equal(3, m.Rating);
        Assert.Equal("Samyang 12mm f/2.0 NCS CS", m.LensModel);
        Assert.Equal("xmp", m.Extras["lensSource"]);
        Assert.Equal("XMP123", m.BodySerial);
    }

    [Fact]
    public async Task Tiff_raw_fixture_saved_as_dng_is_read()
    {
        var preview = Fixtures.EncodeJpeg(32, 24);
        var spec = FullSpec with { PixelWidth = 4000, PixelHeight = 3000 };
        var path = _dir.Write("raw.dng", ExifWriter.BuildTiff(spec, preview));

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Equal("NIKON CORPORATION", m.Make);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", m.LensModel);
        Assert.Equal(new LensSpecification(24, 70, 4, 4), m.LensSpec);
        Assert.Equal(400, m.Iso);
        Assert.Equal(1.0 / 250, m.ExposureTimeS!.Value, 12);
        Assert.Equal(new DateTime(2024, 5, 1, 13, 45, 10), m.CaptureLocal);
        Assert.Equal(4000, m.Width);
        Assert.Equal(3000, m.Height);
        Assert.Equal(6, m.Orientation);
        Assert.Equal(4, m.Rating);
        Assert.True(m.HasGps);
    }

    [Fact]
    public async Task Tiff_fixture_saved_as_tif_is_read()
    {
        var path = _dir.Write("scan.tif", ExifWriter.BuildTiff(FullSpec));

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Equal("NIKON Z 6_2", m.Model);
        Assert.Equal(5.6, m.FNumber);
    }

    [Fact]
    public async Task Darktable_style_sidecar_fills_gaps()
    {
        var path = _dir.Write("IMG_0001.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Make = "SONY", Model = "ILCE-7M3" }));
        _dir.WriteText("IMG_0001.jpg.xmp", Fixtures.Xmp("xmp:Rating=\"5\" aux:Lens=\"FE 35mm F1.8\" aux:LensInfo=\"35/1 35/1 18/10 18/10\""));

        var m = await ExtractOne(path);

        Assert.Empty(m.Warnings);
        Assert.Equal(5, m.Rating);
        Assert.Equal("FE 35mm F1.8", m.LensModel);
        Assert.Equal(new LensSpecification(35, 35, 1.8, 1.8), m.LensSpec);
        Assert.Equal("sidecar", m.Extras["lensSource"]);
        Assert.Equal("IMG_0001.jpg.xmp", m.Extras["sidecar"]);
    }

    [Fact]
    public async Task Lightroom_style_sidecar_fills_gaps_for_raw_files()
    {
        var path = _dir.Write("DSC_0002.dng", ExifWriter.BuildTiff(new ExifSpec { Make = "NIKON", Model = "D850" }, Fixtures.EncodeJpeg(16, 16)));
        _dir.WriteText("DSC_0002.xmp", Fixtures.Xmp("xmp:Rating=\"2\" exifEX:LensModel=\"AF-S NIKKOR 70-200mm f/2.8E FL ED VR\""));

        var m = await ExtractOne(path);

        Assert.Equal(2, m.Rating);
        Assert.Equal("AF-S NIKKOR 70-200mm f/2.8E FL ED VR", m.LensModel);
        Assert.Equal("DSC_0002.xmp", m.Extras["sidecar"]);
    }

    [Fact]
    public async Task Sidecar_never_overrides_exif_values()
    {
        var spec = new ExifSpec { LensModel = "EF24-105mm f/4L IS USM", Rating = 1 };
        var path = _dir.Write("IMG_0003.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec));
        _dir.WriteText("IMG_0003.xmp", Fixtures.Xmp("xmp:Rating=\"5\" aux:Lens=\"Something else\""));

        var m = await ExtractOne(path);

        Assert.Equal(1, m.Rating);
        Assert.Equal("EF24-105mm f/4L IS USM", m.LensModel);
        Assert.False(m.Extras.ContainsKey("lensSource"));
        Assert.False(m.Extras.ContainsKey("sidecar"));
    }

    [Fact]
    public async Task File_name_sidecar_wins_over_stem_sidecar()
    {
        var path = _dir.Write("IMG_0004.jpg", Fixtures.BaseJpeg);
        _dir.WriteText("IMG_0004.xmp", Fixtures.Xmp("xmp:Rating=\"1\""));
        _dir.WriteText("IMG_0004.jpg.xmp", Fixtures.Xmp("xmp:Rating=\"4\""));

        var m = await ExtractOne(path);

        Assert.Equal(4, m.Rating);
    }

    [Fact]
    public async Task Corrupt_sidecar_is_a_warning()
    {
        var path = _dir.Write("IMG_0005.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Make = "Canon" }));
        _dir.WriteText("IMG_0005.xmp", "<x:xmpmeta this is not xml");

        var m = await ExtractOne(path);

        Assert.Equal("Canon", m.Make);
        Assert.Contains(m.Warnings, w => w.Contains("IMG_0005.xmp", StringComparison.Ordinal));
    }

    private const string CommentedXmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <!-- leading comment -->
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <!-- comment inside rdf:RDF -->
          <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="3"/>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    [Fact]
    public async Task Xmp_that_xmpcore_rejects_never_loses_exif()
    {
        var exifJpeg = ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Make = "Canon", Iso = 400 });
        var embedded = _dir.Write("embedded-comment.jpg", Fixtures.JpegWith(xmp: CommentedXmp, jpeg: exifJpeg));
        var withSidecar = _dir.Write("sidecar-comment.jpg", exifJpeg);
        _dir.WriteText("sidecar-comment.xmp", CommentedXmp);

        var results = await new NativeMetadataExtractor().ExtractAsync(Fixtures.Sources(embedded, withSidecar), CancellationToken.None);

        foreach (var m in results)
        {
            output.WriteLine($"{Path.GetFileName(m.Path)}: rating={m.Rating} warnings=[{string.Join("; ", m.Warnings)}]");
            Assert.Equal("Canon", m.Make);
            Assert.Equal(400, m.Iso);
        }

        // Embedded packet: XmpCore's failure is reported, EXIF survives.
        Assert.Contains(results[0].Warnings, w => w.Contains("XMP unreadable", StringComparison.Ordinal));

        // Sidecar: comments are stripped before parsing, so the rating is recovered.
        Assert.Equal(3, results[1].Rating);
        Assert.Empty(results[1].Warnings);
    }

    [Theory]
    [InlineData("a<!-- x -->b", "ab")]
    [InlineData("<!--x--><!-- y -->c<!---->", "c")]
    [InlineData("no comments", "no comments")]
    [InlineData("keep<!-- unterminated", "keep")]
    public void Strips_xml_comments(string input, string expected) =>
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(XmpValues.StripXmlComments(System.Text.Encoding.UTF8.GetBytes(input))));

    [Fact]
    public async Task Unreadable_files_produce_a_warning_not_an_exception()
    {
        var random = new byte[4096];
        new Random(42).NextBytes(random);
        var garbage = _dir.Write("garbage.jpg", random);
        var text = _dir.WriteText("text.jpg", "this is not an image");
        var empty = _dir.Write("empty.jpg", []);
        var full = ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, FullSpec);
        var truncated = _dir.Write("truncated.jpg", full[..40]);
        var missing = Path.Combine(_dir.Path, "missing.jpg");
        var directory = Directory.CreateDirectory(Path.Combine(_dir.Path, "folder.jpg")).FullName;

        var results = await _extractor.ExtractAsync(Fixtures.Sources(garbage, text, empty, truncated, missing, directory));

        Assert.Equal(6, results.Count);
        foreach (var m in results)
        {
            output.WriteLine($"{Path.GetFileName(m.Path)}: {string.Join(" | ", m.Warnings)}");
            Assert.NotEmpty(m.Warnings);
            Assert.Equal(NativeMetadataExtractor.PluginId, m.Extractor);
            Assert.Null(m.Make);
            Assert.Null(m.CaptureLocal);
        }

        Assert.Equal(["file not found"], results[4].Warnings);
        Assert.StartsWith("unreadable or unsupported file", results[1].Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_order_is_preserved_with_failures_in_between()
    {
        var paths = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            paths.Add(i % 5 == 3
                ? Path.Combine(_dir.Path, $"missing-{i}.jpg")
                : _dir.Write($"f{i:D2}.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = $"Cam-{i}", Iso = 100 + i })));
        }

        var results = await _extractor.ExtractAsync(Fixtures.Sources([.. paths]));

        Assert.Equal(paths.Count, results.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            Assert.Equal(paths[i], results[i].Path);
            if (i % 5 == 3)
            {
                Assert.Equal(["file not found"], results[i].Warnings);
            }
            else
            {
                Assert.Equal($"Cam-{i}", results[i].Model);
                Assert.Equal(100 + i, results[i].Iso);
            }
        }
    }

    [Fact]
    public async Task Parallel_batches_on_one_instance_match_sequential_results()
    {
        var paths = WriteVariedFixtures(_dir, 40);
        var batch = Fixtures.Sources([.. paths]);
        var expected = (await _extractor.ExtractAsync(batch)).Select(Serialize).ToList();

        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _extractor.ExtractAsync(batch)));

        foreach (var run in runs)
        {
            Assert.Equal(expected, run.Select(Serialize));
        }
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var path = _dir.Write("c.jpg", Fixtures.BaseJpeg);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _extractor.ExtractAsync(Fixtures.Sources(path), cts.Token));
    }

    [Fact]
    public async Task Empty_batch_returns_empty_list()
    {
        Assert.Empty(await _extractor.ExtractAsync([]));
    }

    [Fact]
    public async Task Factory_manifest_and_health()
    {
        var factory = new NativeMetadataExtractorFactory();
        var plugin = Assert.IsType<NativeMetadataExtractor>(factory.Create(Fixtures.Context()));

        Assert.Equal("builtin.metadata.native", factory.Id);
        Assert.Equal(ComponentKind.Metadata, factory.Kind);
        Assert.Equal(factory.Id, plugin.Manifest.Id);
        Assert.Equal(ComponentKind.Metadata, plugin.Manifest.Kind);
        Assert.Equal(ContractInfo.Version, plugin.Manifest.ContractVersion);
        Assert.False(string.IsNullOrEmpty(plugin.Manifest.Fingerprint));
        var health = await plugin.CheckHealthAsync();
        Assert.True(health.Healthy);
        Assert.Contains("MetadataExtractor", health.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.jpe", true)]
    [InlineData("a.heic", true)]
    [InlineData("a.HIF", true)]
    [InlineData("a.avif", true)]
    [InlineData("a.tiff", true)]
    [InlineData("a.png", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.dng", true)]
    [InlineData("a.nef", true)]
    [InlineData("a.cr3", true)]
    [InlineData("a.raf", true)]
    [InlineData("a.arw", true)]
    [InlineData("a.orf", true)]
    [InlineData("a.rw2", true)]
    [InlineData("a.iiq", true)]
    [InlineData("a.crw", false)]
    [InlineData("a.x3f", false)]
    [InlineData("a.mp4", false)]
    [InlineData("a.xmp", false)]
    [InlineData("noextension", false)]
    public void CanHandle_by_extension(string fileName, bool expected)
    {
        Assert.Equal(expected, _extractor.CanHandle(Fixtures.Source(Path.Combine(_dir.Path, fileName))));
    }

    [Fact]
    public async Task Throughput_benchmark()
    {
        const int count = 500;
        var paths = WriteVariedFixtures(_dir, count);
        var batch = Fixtures.Sources([.. paths]);
        await _extractor.ExtractAsync(batch.Take(20).ToList());

        var sw = Stopwatch.StartNew();
        var results = await _extractor.ExtractAsync(batch);
        sw.Stop();

        var filesPerSecond = count / sw.Elapsed.TotalSeconds;
        output.WriteLine($"native: {count} files in {sw.Elapsed.TotalMilliseconds:F0} ms = {filesPerSecond:F0} files/s (single thread)");
        Assert.All(results, r => Assert.Empty(r.Warnings));
        Assert.True(filesPerSecond > 100, $"native extractor too slow: {filesPerSecond:F0} files/s");

        sw.Restart();
        var batches = batch.Chunk(50).Select(c => _extractor.ExtractAsync(c)).ToList();
        await Task.WhenAll(batches);
        sw.Stop();
        output.WriteLine($"native: {count} files in {sw.Elapsed.TotalMilliseconds:F0} ms = {count / sw.Elapsed.TotalSeconds:F0} files/s (10 parallel batches)");
    }

    [ExifToolSamplesFact]
    public async Task Reports_sample_format_support()
    {
        var dir = ExifToolLocator.SampleImages!;
        var files = Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal).ToList();
        var results = await _extractor.ExtractAsync(Fixtures.Sources([.. files]));
        foreach (var m in results)
        {
            output.WriteLine(
                $"{Path.GetFileName(m.Path),-22} {m.Extras.GetValueOrDefault("fileType"),-6} {m.Make} | {m.Model} | lens={m.LensModel} " +
                $"({m.Extras.GetValueOrDefault("lensSource")}) | f={m.FocalLengthMm} N={m.FNumber} t={m.ExposureTimeS} iso={m.Iso} " +
                $"| {m.Width}x{m.Height} | {m.CaptureLocal:s} | {string.Join("; ", m.Warnings)}");
        }

        string[] mustRead = ["CanonRaw.cr2", "CanonRaw.cr3", "FujiFilm.raf", "Nikon.nef", "DNG.dng", "Panasonic.rw2"];
        foreach (var name in mustRead)
        {
            var m = results.SingleOrDefault(r => Path.GetFileName(r.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (m is not null)
            {
                Assert.NotNull(m.Make);
                Assert.DoesNotContain(m.Warnings, w => w.StartsWith("unreadable", StringComparison.Ordinal));
            }
        }
    }

    internal static List<string> WriteVariedFixtures(FixtureDirectory dir, int count)
    {
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var spec = FullSpec with
            {
                Model = "Z " + (i % 9),
                ExposureTime = i % 3 == 0 ? 1.0 / (125 << (i % 6)) : 1 + (i % 30),
                Iso = 100 * (1 + (i % 64)),
                FNumber = 1.4 + (i % 10),
                DateTimeOriginal = new DateTime(2020, 1, 1).AddMinutes(i * 37),
                Gps = i % 4 == 0 ? null : (45 + (i / 1000.0), -(7 + (i / 1000.0))),
                Rating = i % 6,
            };
            paths.Add(dir.Write($"bench-{i:D4}.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec)));
        }

        return paths;
    }

    internal static string Serialize(PhotoMetadata m) => JsonSerializer.Serialize(m with { Path = Path.GetFileName(m.Path) });

    private async Task<PhotoMetadata> ExtractOne(string path)
    {
        var results = await _extractor.ExtractAsync(Fixtures.Sources(path));
        var m = Assert.Single(results);
        if (m.Warnings.Count > 0)
        {
            output.WriteLine(string.Join(Environment.NewLine, m.Warnings));
        }

        return m;
    }
}
