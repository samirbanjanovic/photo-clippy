using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Metadata;

namespace PhotoClippy.Metadata.Tests;

public sealed class ExifMappingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Canon  ", "Canon")]
    [InlineData("NIKON   Z 6_2", "NIKON Z 6_2")]
    [InlineData("OLYMPUS\tIMAGING\nCORP.", "OLYMPUS IMAGING CORP.")]
    [InlineData("SONY\0\0\0garbage", "SONY")]
    public void Clean_trims_and_collapses_whitespace(string? input, string? expected) =>
        Assert.Equal(expected, ExifMapping.Clean(input));

    [Theory]
    [InlineData("6012345", "6012345")]
    [InlineData(" NO= 3201234 ", "3201234")]
    [InlineData("0000000000", null)]
    [InlineData("----", null)]
    [InlineData("N/A", null)]
    [InlineData("none", null)]
    [InlineData("Unknown (0)", null)]
    [InlineData("0123", "0123")]
    public void CleanSerial_rejects_placeholders(string input, string? expected) =>
        Assert.Equal(expected, ExifMapping.CleanSerial(input));

    [Theory]
    [InlineData("EF24-70mm f/2.8L II USM", "EF24-70mm f/2.8L II USM")]
    [InlineData("  FE 35mm  F1.8 ", "FE 35mm F1.8")]
    [InlineData("65535", null)]
    [InlineData("0 0", null)]
    [InlineData("----", null)]
    [InlineData("Unknown (32 0 0)", null)]
    [InlineData("(255)", null)]
    [InlineData("", null)]
    public void UsefulLensName_rejects_undecoded_ids(string input, string? expected) =>
        Assert.Equal(expected, ExifMapping.UsefulLensName(input));

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 1.4)]
    [InlineData(3.0, 2.8)]
    [InlineData(4.97, 5.6)]
    [InlineData(6.0, 8.0)]
    [InlineData(10.0, 32.0)]
    public void FNumberFromApex(double av, double expected) =>
        Assert.Equal(expected, ExifMapping.FNumberFromApex(av));

    [Theory]
    [InlineData(6.97, 1.0 / 125)]
    [InlineData(12.97, 1.0 / 8000)]
    [InlineData(13.0, 1.0 / 8000)]
    [InlineData(4.0, 1.0 / 16)]
    [InlineData(10.5, 1.0 / 1448)]
    [InlineData(1.32, 0.4)]
    [InlineData(0.32, 0.8)]
    [InlineData(0.0, 1.0)]
    [InlineData(-4.907, 30.0)]
    [InlineData(-1.0, 2.0)]
    public void ExposureFromApex(double tv, double expected) =>
        Assert.Equal(expected, ExifMapping.ExposureFromApex(tv)!.Value, 12);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(99.0)]
    public void Apex_out_of_range_is_null(double value)
    {
        Assert.Null(ExifMapping.FNumberFromApex(value));
        Assert.Null(ExifMapping.ExposureFromApex(value));
    }

    [Theory]
    [InlineData(5000.0, null, 196.8503937)]
    [InlineData(5000.0, 2, 196.8503937)]
    [InlineData(2000.0, 3, 200.0)]
    [InlineData(200.0, 4, 200.0)]
    [InlineData(0.2, 5, 200.0)]
    public void FocalPlanePerMm_converts_units(double resolution, int? unit, double expected) =>
        Assert.Equal(expected, ExifMapping.FocalPlanePerMm(resolution, unit)!.Value, 6);

    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(100.0, 9)]
    public void FocalPlanePerMm_invalid_is_null(double resolution, int unit) =>
        Assert.Null(ExifMapping.FocalPlanePerMm(resolution, unit));

    [Fact]
    public void LensSpec_handles_unknown_members_and_reversed_ranges()
    {
        Assert.Equal(new LensSpecification(24, 70, 2.8, 2.8), ExifMapping.LensSpec(24, 70, 2.8, 2.8));
        Assert.Equal(new LensSpecification(18, 55, null, 5.6), ExifMapping.LensSpec(18, 55, 0, 5.6));
        Assert.Equal(new LensSpecification(18, 55, 3.5, 5.6), ExifMapping.LensSpec(55, 18, 5.6, 3.5));
        Assert.Equal(new LensSpecification(50, 50, null, null), ExifMapping.LensSpec(50, 50, double.NaN, null));
        Assert.Null(ExifMapping.LensSpec(0, 70, 2.8, 2.8));
        Assert.Null(ExifMapping.LensSpec(24, 0, 2.8, 2.8));
        Assert.Null(ExifMapping.LensSpec(null, null, null, null));
    }

    [Fact]
    public void ParseLensInfo_accepts_exiftool_and_xmp_formats()
    {
        Assert.Equal(new LensSpecification(24, 70, 2.8, 4), ExifMapping.ParseLensInfo("24 70 2.8 4"));
        Assert.Equal(new LensSpecification(24, 70, 2.8, 4), ExifMapping.ParseLensInfo("24/1 70/1 28/10 40/10"));
        Assert.Equal(new LensSpecification(35, 35, null, null), ExifMapping.ParseLensInfo("35/1 35/1 0/0 0/0"));
        Assert.Null(ExifMapping.ParseLensInfo("0 0 0 0"));
        Assert.Null(ExifMapping.ParseLensInfo("24 70 2.8"));
        Assert.Null(ExifMapping.ParseLensInfo("abc"));
        Assert.Null(ExifMapping.ParseLensInfo(null));
    }

    [Theory]
    [InlineData("1/250", 0.004)]
    [InlineData("28/10", 2.8)]
    [InlineData("-7/10", -0.7)]
    [InlineData("4.5", 4.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1/0", null)]
    [InlineData("x/2", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParseNumber(string token, double? expected) =>
        Assert.Equal(expected, ExifMapping.ParseNumber(token));

    [Theory]
    [InlineData("2024:05:01 13:45:10", "2024-05-01T13:45:10")]
    [InlineData("2024:05:01 13:45:10+02:00", "2024-05-01T13:45:10")]
    [InlineData("2024-05-01T13:45:10", "2024-05-01T13:45:10")]
    [InlineData("2024:05:01 13:45", "2024-05-01T13:45:00")]
    [InlineData("0000:00:00 00:00:00", null)]
    [InlineData("    :  :     :  :  ", null)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void ParseDateTime(string? input, string? expected)
    {
        var result = ExifMapping.ParseDateTime(input);
        Assert.Equal(expected, result?.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
        if (result is { } dt)
        {
            Assert.Equal(DateTimeKind.Unspecified, dt.Kind);
        }
    }

    [Theory]
    [InlineData("+02:00", "+02:00")]
    [InlineData("-05:30", "-05:30")]
    [InlineData("Z", "+00:00")]
    [InlineData(" +09:00 ", "+09:00")]
    [InlineData("+2:00", null)]
    [InlineData("   :  ", null)]
    [InlineData(null, null)]
    public void ParseOffset(string? input, string? expected) =>
        Assert.Equal(expected, ExifMapping.ParseOffset(input));

    [Theory]
    [InlineData("123", "123")]
    [InlineData(" 04 ", "04")]
    [InlineData("12a", null)]
    [InlineData("", null)]
    public void ParseSubSec(string input, string? expected) =>
        Assert.Equal(expected, ExifMapping.ParseSubSec(input));

    [Theory]
    [InlineData("ISO 400", 400)]
    [InlineData("1600", 1600)]
    [InlineData("Auto ISO 3200", 3200)]
    [InlineData("Hi 1.0", null)]
    [InlineData("Auto", null)]
    [InlineData(null, null)]
    public void IsoFromDescription(string? input, int? expected) =>
        Assert.Equal(expected, ExifMapping.IsoFromDescription(input));

    [Theory]
    [InlineData(48.85, 2.35, true)]
    [InlineData(-33.8, -70.6, true)]
    [InlineData(0, 0, false)]
    [InlineData(91, 0, false)]
    [InlineData(0, 181, false)]
    [InlineData(double.NaN, 1, false)]
    public void ValidGps(double lat, double lon, bool expected) =>
        Assert.Equal(expected, ExifMapping.ValidGps(lat, lon));

    [Fact]
    public void Range_validators()
    {
        Assert.Equal(6, ExifMapping.ValidOrientation(6));
        Assert.Null(ExifMapping.ValidOrientation(0));
        Assert.Null(ExifMapping.ValidOrientation(9));
        Assert.Null(ExifMapping.ValidOrientation(2.5));
        Assert.Equal(-1, ExifMapping.ValidRating(-1));
        Assert.Equal(5, ExifMapping.ValidRating(5));
        Assert.Null(ExifMapping.ValidRating(6));
        Assert.Null(ExifMapping.ValidIso(0));
        Assert.Equal(100, ExifMapping.ValidIso(100));
        Assert.Null(ExifMapping.SubjectDistance(0));
        Assert.Null(ExifMapping.SubjectDistance(4294967295));
        Assert.Equal(2.5, ExifMapping.SubjectDistance(2.5));
    }

    [Fact]
    public void ExifTool_json_maps_all_fields()
    {
        const string json = """
            {
              "SourceFile": "/x/a.nef", "FileType": "NEF",
              "Make": "NIKON CORPORATION", "Model": "NIKON Z 6_2", "Software": "Ver.01.50",
              "SerialNumber": 6012345, "LensMake": "Nikon", "LensModel": "NIKKOR Z 24-70mm f/4 S",
              "LensSerialNumber": "20012345", "LensInfo": "24 70 4 4",
              "FocalLength": 35, "FocalLengthIn35mmFormat": 35, "FNumber": 5.6, "ExposureTime": 0.004,
              "ISO": 400, "ExposureCompensation": -0.7, "ExposureProgram": 3, "MeteringMode": 5,
              "WhiteBalance": 0, "Flash": 16,
              "DateTimeOriginal": "2024:05:01 13:45:10", "OffsetTimeOriginal": "+02:00", "SubSecTimeOriginal": 123,
              "ImageUniqueID": "ABCDEF", "ExifImageWidth": 6048, "ExifImageHeight": 4024,
              "ImageWidth": 6064, "ImageHeight": 4040, "Orientation": 6,
              "FocalPlaneXResolution": 1672, "FocalPlaneResolutionUnit": 3, "SubjectDistance": 3.5, "Rating": 4,
              "GPSLatitude": 33.8568, "GPSLatitudeRef": "S", "GPSLongitude": 151.2153, "GPSLongitudeRef": "E"
            }
            """;

        var m = MapJson(json);

        Assert.Equal("NIKON CORPORATION", m.Make);
        Assert.Equal("NIKON Z 6_2", m.Model);
        Assert.Equal("Ver.01.50", m.Software);
        Assert.Equal("6012345", m.BodySerial);
        Assert.Equal("Nikon", m.LensMake);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", m.LensModel);
        Assert.Equal("20012345", m.LensSerial);
        Assert.Equal(new LensSpecification(24, 70, 4, 4), m.LensSpec);
        Assert.Equal(35, m.FocalLengthMm);
        Assert.Equal(35, m.FocalLength35mm);
        Assert.Equal(5.6, m.FNumber);
        Assert.Equal(0.004, m.ExposureTimeS);
        Assert.Equal(400, m.Iso);
        Assert.Equal(-0.7, m.ExposureBiasEv);
        Assert.Equal("aperture-priority", m.ExposureProgram);
        Assert.Equal("multi-segment", m.MeteringMode);
        Assert.Equal("auto", m.WhiteBalance);
        Assert.False(m.FlashFired);
        Assert.Equal(new DateTime(2024, 5, 1, 13, 45, 10), m.CaptureLocal);
        Assert.Equal("+02:00", m.OffsetTime);
        Assert.Equal("123", m.SubSec);
        Assert.Equal("ABCDEF", m.ImageUniqueId);
        Assert.Equal(6048, m.Width);
        Assert.Equal(4024, m.Height);
        Assert.Equal(6, m.Orientation);
        Assert.Equal(167.2, m.FocalPlaneXResolutionPerMm);
        Assert.Equal(3.5, m.SubjectDistanceM);
        Assert.Equal(4, m.Rating);
        Assert.True(m.HasGps);
        Assert.Equal(-33.8568, m.Latitude);
        Assert.Equal(151.2153, m.Longitude);
        Assert.Equal("NEF", m.Extras["fileType"]);
        Assert.Empty(m.Warnings);
    }

    [Theory]
    [InlineData("18.0-55.0mm", true)]
    [InlineData("18.0 - 55.0 mm", true)]
    [InlineData("50 mm", true)]
    [InlineData("mm", false)]
    [InlineData("18-55mm f/3.5-5.6", false)]
    [InlineData("EF50mm", false)]
    public void Detects_focal_range_only_lens_values(string value, bool expected) =>
        Assert.Equal(expected, ExifMapping.IsFocalRangeOnly(value));

    [Fact]
    public void ExifTool_lens_fallbacks_never_invent_names()
    {
        var lensId = MapJson("""{"LensID": "AF-S DX Nikkor 18-55mm f/3.5-5.6G VR", "Lens": "18-55mm f/3.5-5.6"}""");
        Assert.Equal("AF-S DX Nikkor 18-55mm f/3.5-5.6G VR", lensId.LensModel);
        Assert.Equal("lensId", lensId.Extras["lensSource"]);

        var ambiguous = MapJson("""{"LensID": "Sigma 17-70mm F2.8-4 DC Macro or Tamron 17-50mm"}""");
        Assert.Null(ambiguous.LensModel);
        Assert.Equal("Sigma 17-70mm F2.8-4 DC Macro or Tamron 17-50mm", ambiguous.Extras["lensCandidates"]);

        // Composite:Lens is excluded via --Composite:Lens, so a Lens key is the file's own (e.g. XMP aux:Lens) value.
        var recorded = MapJson("""{"LensID": "Sigma 17-70mm F2.8-4 DC Macro or Tamron 17-50mm", "Lens": "17.0-70.0 mm"}""");
        Assert.Equal("17.0-70.0 mm", recorded.LensModel);
        Assert.Equal("lens", recorded.Extras["lensSource"]);
        Assert.True(recorded.Extras.ContainsKey("lensCandidates"));

        var unknown = MapJson("""{"LensID": "Unknown 18-55mm"}""");
        Assert.Null(unknown.LensModel);
        Assert.False(unknown.Extras.ContainsKey("lensSource"));

        var synthesized = MapJson("""{"LensID": "18.0-55.0mm"}""");
        Assert.Null(synthesized.LensModel);
        Assert.False(synthesized.Extras.ContainsKey("lensSource"));

        var fromAux = MapJson("""{"LensID": "18.0-55.0mm", "Lens": "18.0-55.0 mm"}""");
        Assert.Equal("18.0-55.0 mm", fromAux.LensModel);
        Assert.Equal("lens", fromAux.Extras["lensSource"]);

        var numeric = MapJson("""{"LensModel": "65535", "Lens": "Leica Summilux-M 35mm f/1.4"}""");
        Assert.Equal("Leica Summilux-M 35mm f/1.4", numeric.LensModel);
        Assert.Equal("lens", numeric.Extras["lensSource"]);
    }

    [Fact]
    public void ExifTool_iso_and_apex_fallbacks()
    {
        var rei = MapJson("""{"ISO": 65535, "RecommendedExposureIndex": 102400}""");
        Assert.Equal(102400, rei.Iso);
        Assert.Equal("recommendedExposureIndex", rei.Extras["isoSource"]);

        var saturated = MapJson("""{"ISO": 65535}""");
        Assert.Equal(65535, saturated.Iso);
        Assert.Equal("saturated", saturated.Extras["isoSource"]);

        var arrayIso = MapJson("""{"ISO": [200, 0]}""");
        Assert.Equal(200, arrayIso.Iso);

        var apex = MapJson("""{"ApertureValue": 7.9999, "ShutterSpeedValue": 0.00797}""");
        Assert.Equal(8, apex.FNumber);
        Assert.Equal(1.0 / 125, apex.ExposureTimeS!.Value, 12);
    }

    [Fact]
    public void ExifTool_zero_values_and_errors()
    {
        var m = MapJson("""
            {"FocalLength": 0, "FocalLengthIn35mmFormat": 0, "FNumber": 0, "ExposureTime": 0, "ISO": 0,
             "LensInfo": "0 0 0 0", "SerialNumber": "0000", "GPSLatitude": 0, "GPSLongitude": 0, "Orientation": 0,
             "DateTimeOriginal": "0000:00:00 00:00:00", "ModifyDate": "2020:02:03 04:05:06", "OffsetTime": "Z",
             "Error": "File format error", "Warning": "Bad MakerNotes"}
            """);

        Assert.Null(m.FocalLengthMm);
        Assert.Null(m.FocalLength35mm);
        Assert.Null(m.FNumber);
        Assert.Null(m.ExposureTimeS);
        Assert.Null(m.Iso);
        Assert.Null(m.LensSpec);
        Assert.Null(m.BodySerial);
        Assert.Null(m.Orientation);
        Assert.False(m.HasGps);
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), m.CaptureLocal);
        Assert.Equal("+00:00", m.OffsetTime);
        Assert.Equal("dateTime", m.Extras["captureSource"]);
        Assert.Equal(["exiftool: File format error", "exiftool: Bad MakerNotes"], m.Warnings);
    }

    private static PhotoMetadata MapJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var draft = new MetadataDraft();
        ExifToolMetadataExtractor.Map(doc.RootElement, draft);
        return draft.Build("/x/a.jpg", ExifToolMetadataExtractor.PluginId);
    }
}
