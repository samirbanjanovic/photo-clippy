using System.Diagnostics;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Metadata;
using PhotoClippy.Testing;
using Xunit.Abstractions;

namespace PhotoClippy.Metadata.Tests;

public sealed class ExifToolMetadataExtractorTests(ITestOutputHelper output) : IDisposable
{
    private readonly FixtureDirectory _dir = new("exiftool");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Missing_executable_does_not_throw_and_reports_unhealthy()
    {
        var bogus = Path.Combine(_dir.Path, "no-such-exiftool");
        var factory = new ExifToolMetadataExtractorFactory();
        using var plugin = Assert.IsType<ExifToolMetadataExtractor>(
            factory.Create(Fixtures.Context(new ExifToolSettings { ExecutablePath = bogus, TimeoutSeconds = 10 })));

        var health = await plugin.CheckHealthAsync();
        Assert.False(health.Healthy);
        Assert.StartsWith("exiftool not found", health.Message, StringComparison.Ordinal);

        var jpeg = _dir.Write("a.jpg", Fixtures.BaseJpeg);
        var results = await plugin.ExtractAsync(Fixtures.Sources(jpeg, Path.Combine(_dir.Path, "b.jpg")));
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(ExifToolMetadataExtractor.PluginId, r.Extractor);
            Assert.StartsWith("exiftool unavailable", Assert.Single(r.Warnings), StringComparison.Ordinal);
        });
        Assert.Equal(jpeg, results[0].Path);
    }

    [Fact]
    public void Factory_tolerates_default_and_invalid_settings()
    {
        var factory = new ExifToolMetadataExtractorFactory();
        Assert.Equal("builtin.metadata.exiftool", factory.Id);
        Assert.Equal(ComponentKind.Metadata, factory.Kind);

        using var defaults = Assert.IsType<ExifToolMetadataExtractor>(factory.Create(Fixtures.Context()));
        using var invalid = Assert.IsType<ExifToolMetadataExtractor>(factory.Create(Fixtures.Context(new { timeoutSeconds = "soon" })));
        Assert.Equal(TimeSpan.FromSeconds(120), defaults.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), invalid.Timeout);
        Assert.Equal(factory.Id, defaults.Manifest.Id);
        Assert.Equal(ComponentKind.Metadata, defaults.Manifest.Kind);
        Assert.True(defaults.CanHandle(Fixtures.Source("/x/a.crw")));
        Assert.True(defaults.CanHandle(Fixtures.Source("/x/a.CR3")));
        Assert.False(defaults.CanHandle(Fixtures.Source("/x/a.txt")));
    }

    [Fact]
    public async Task Disposed_extractor_is_unhealthy_and_rejects_work()
    {
        var plugin = new ExifToolMetadataExtractor(new ExifToolSettings { ExecutablePath = ExifToolLocator.ExecutablePath ?? "exiftool" });
        await plugin.DisposeAsync();
        plugin.Dispose();

        Assert.False((await plugin.CheckHealthAsync()).Healthy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.ExtractAsync(Fixtures.Sources("/x/a.jpg")));
    }

    [ExifToolFact]
    public async Task Health_reports_version()
    {
        await using var plugin = Create();

        var health = await plugin.CheckHealthAsync();

        output.WriteLine(health.Message);
        Assert.True(health.Healthy);
        Assert.Matches(@"^exiftool \d+\.\d+", health.Message);
    }

    [ExifToolFact]
    public async Task Matches_native_extractor_on_fixtures()
    {
        var paths = WriteComparisonFixtures();
        await using var exifTool = Create();
        var native = new NativeMetadataExtractor();
        var batch = Fixtures.Sources([.. paths]);

        var expected = await native.ExtractAsync(batch);
        var actual = await exifTool.ExtractAsync(batch);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            output.WriteLine($"{Path.GetFileName(paths[i])}: native[{string.Join("; ", expected[i].Warnings)}] exiftool[{string.Join("; ", actual[i].Warnings)}]");
            Assert.Equal(paths[i], actual[i].Path);
            Assert.Equal(ExifToolMetadataExtractor.PluginId, actual[i].Extractor);
            Assert.Empty(actual[i].Warnings);
            AssertEquivalent(expected[i], actual[i]);
        }

        // Embedded XMP aux:Lens is the file's own value; ExifTool's synthesized Composite:Lens must not shadow it.
        Assert.Equal("18.0-55.0 mm", actual[^1].LensModel);
        Assert.Equal(4, actual[^1].Rating);
    }

    [ExifToolFact]
    public async Task Maps_full_fixture()
    {
        var path = _dir.Write("full.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, NativeMetadataExtractorTests.FullSpec));
        await using var plugin = Create();

        var m = Assert.Single(await plugin.ExtractAsync(Fixtures.Sources(path)));

        Assert.Equal("NIKON CORPORATION", m.Make);
        Assert.Equal("NIKON Z 6_2", m.Model);
        Assert.Equal("6012345", m.BodySerial);
        Assert.Equal("NIKKOR Z 24-70mm f/4 S", m.LensModel);
        Assert.Equal(new LensSpecification(24, 70, 4, 4), m.LensSpec);
        Assert.Equal(1.0 / 250, m.ExposureTimeS!.Value, 12);
        Assert.Equal(400, m.Iso);
        Assert.Equal("aperture-priority", m.ExposureProgram);
        Assert.True(m.FlashFired);
        Assert.Equal(new DateTime(2024, 5, 1, 13, 45, 10), m.CaptureLocal);
        Assert.Equal("+02:00", m.OffsetTime);
        Assert.Equal("123", m.SubSec);
        Assert.Equal(-33.8568, m.Latitude!.Value, 4);
        Assert.Equal(151.2153, m.Longitude!.Value, 4);
        Assert.Equal(167.2, m.FocalPlaneXResolutionPerMm!.Value, 6);
        Assert.Equal("JPEG", m.Extras["fileType"]);
    }

    [ExifToolFact]
    public async Task Failures_are_warnings_and_order_is_preserved()
    {
        var random = new byte[2048];
        new Random(7).NextBytes(random);
        var paths = new[]
        {
            _dir.Write("ok-1.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = "One" })),
            Path.Combine(_dir.Path, "missing.jpg"),
            _dir.Write("garbage.jpg", random),
            _dir.Write("empty.jpg", []),
            _dir.Write("ok-2.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = "Two" })),
            _dir.Path + "/bad\nname.jpg",
            _dir.Write("ok-1-again.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = "Three" })),
        };
        await using var plugin = Create();

        var results = await plugin.ExtractAsync(Fixtures.Sources(paths));

        Assert.Equal(paths, results.Select(r => r.Path));
        foreach (var r in results)
        {
            output.WriteLine($"{Path.GetFileName(r.Path)}: {r.Model} [{string.Join("; ", r.Warnings)}]");
        }

        Assert.Equal("One", results[0].Model);
        Assert.Contains("not found", Assert.Single(results[1].Warnings), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(results[2].Warnings);
        Assert.NotEmpty(results[3].Warnings);
        Assert.Equal("Two", results[4].Model);
        Assert.Equal(["path cannot be passed to exiftool"], results[5].Warnings);
        Assert.Equal("Three", results[6].Model);
    }

    [ExifToolFact]
    public async Task Duplicate_paths_in_a_batch_each_get_a_result()
    {
        var path = _dir.Write("dup.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = "Dup" }));
        await using var plugin = Create();

        var results = await plugin.ExtractAsync(Fixtures.Sources(path, path));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("Dup", r.Model));
    }

    [ExifToolFact]
    public async Task Concurrent_batches_are_serialized_safely()
    {
        var paths = NativeMetadataExtractorTests.WriteVariedFixtures(_dir, 60);
        await using var plugin = Create();
        var batches = paths.Chunk(10).Select(c => Fixtures.Sources(c)).ToList();

        var results = await Task.WhenAll(batches.Select(b => plugin.ExtractAsync(b)));

        for (var b = 0; b < batches.Count; b++)
        {
            Assert.Equal(batches[b].Select(s => s.Path), results[b].Select(r => r.Path));
            for (var i = 0; i < batches[b].Count; i++)
            {
                var index = (b * 10) + i;
                Assert.Equal("Z " + (index % 9), results[b][i].Model);
                Assert.Equal(100 * (1 + (index % 64)), results[b][i].Iso);
            }
        }
    }

    [ExifToolFact]
    public async Task Restarts_when_the_process_dies()
    {
        var path = _dir.Write("restart.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Model = "R" }));
        await using var plugin = Create();
        Assert.Equal("R", Assert.Single(await plugin.ExtractAsync(Fixtures.Sources(path))).Model);
        var firstPid = plugin.CurrentProcessId!.Value;

        using (var process = Process.GetProcessById(firstPid))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        var m = Assert.Single(await plugin.ExtractAsync(Fixtures.Sources(path)));

        Assert.Equal("R", m.Model);
        Assert.Empty(m.Warnings);
        Assert.NotEqual(firstPid, plugin.CurrentProcessId);
    }

    [ExifToolFact]
    public async Task Dispose_kills_the_process()
    {
        var path = _dir.Write("dispose.jpg", Fixtures.BaseJpeg);
        var plugin = Create();
        await plugin.ExtractAsync(Fixtures.Sources(path));
        var pid = plugin.CurrentProcessId!.Value;

        await plugin.DisposeAsync();

        Assert.Null(plugin.CurrentProcessId);
        Assert.False(IsRunning(pid));
        Assert.False((await plugin.CheckHealthAsync()).Healthy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.ExtractAsync(Fixtures.Sources(path)));
    }

    [ExifToolFact]
    public async Task Cancellation_is_honoured()
    {
        var path = _dir.Write("cancel.jpg", Fixtures.BaseJpeg);
        await using var plugin = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExtractAsync(Fixtures.Sources(path), cts.Token));

        Assert.Single(await plugin.ExtractAsync(Fixtures.Sources(path)));
    }

    [ExifToolFact]
    public async Task Sidecar_fills_gaps()
    {
        var path = _dir.Write("IMG_0100.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Make = "Canon" }));
        _dir.WriteText("IMG_0100.jpg.xmp", Fixtures.Xmp("xmp:Rating=\"3\" aux:Lens=\"EF50mm f/1.8 STM\""));
        await using var plugin = Create();

        var m = Assert.Single(await plugin.ExtractAsync(Fixtures.Sources(path)));

        Assert.Equal(3, m.Rating);
        Assert.Equal("EF50mm f/1.8 STM", m.LensModel);
        Assert.Equal("IMG_0100.jpg.xmp", m.Extras["sidecar"]);
    }

    [ExifToolFact]
    public async Task Throughput_benchmark()
    {
        const int count = 300;
        var paths = NativeMetadataExtractorTests.WriteVariedFixtures(_dir, count);
        await using var plugin = Create();
        await plugin.ExtractAsync(Fixtures.Sources([.. paths.Take(5)]));

        var sw = Stopwatch.StartNew();
        var results = new List<PhotoMetadata>(count);
        foreach (var chunk in paths.Chunk(100))
        {
            results.AddRange(await plugin.ExtractAsync(Fixtures.Sources(chunk)));
        }

        sw.Stop();
        output.WriteLine($"exiftool: {count} files in {sw.Elapsed.TotalMilliseconds:F0} ms = {count / sw.Elapsed.TotalSeconds:F0} files/s (batches of 100)");
        Assert.Equal(count, results.Count);
        Assert.All(results, r => Assert.Empty(r.Warnings));
    }

    [ExifToolSamplesFact]
    public async Task Reports_sample_lens_names()
    {
        var files = Directory.EnumerateFiles(ExifToolLocator.SampleImages!).Order(StringComparer.Ordinal).ToList();
        await using var plugin = Create();
        var native = new NativeMetadataExtractor();
        var batch = Fixtures.Sources([.. files]);

        var results = await plugin.ExtractAsync(batch);
        var nativeResults = await native.ExtractAsync(batch);

        Assert.Equal(files, results.Select(r => r.Path));
        for (var i = 0; i < results.Count; i++)
        {
            var m = results[i];
            output.WriteLine(
                $"{Path.GetFileName(m.Path),-22} {m.Extras.GetValueOrDefault("fileType"),-6} {m.Make} | {m.Model} | lens={m.LensModel} " +
                $"({m.Extras.GetValueOrDefault("lensSource")}) native-lens={nativeResults[i].LensModel} | iso={m.Iso} | " +
                $"{m.Width}x{m.Height} | {string.Join("; ", m.Warnings)}");
        }

        var canon = results.Single(r => Path.GetFileName(r.Path) == "Canon.jpg");
        Assert.Equal("Canon", canon.Make);
        var nikon = results.Single(r => Path.GetFileName(r.Path) == "Nikon.jpg");
        Assert.Equal("NIKON", nikon.Make);
    }

    private static ExifToolMetadataExtractor Create() =>
        new(new ExifToolSettings { ExecutablePath = ExifToolLocator.ExecutablePath!, TimeoutSeconds = 60 });

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private List<string> WriteComparisonFixtures()
    {
        var spec = NativeMetadataExtractorTests.FullSpec;
        return
        [
            _dir.Write("full.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec)),
            _dir.Write("fast.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, spec with
            {
                ExposureTime = 1.0 / 8000, LensSpec = (18, 55, 3.5, 5.6), Gps = (22.9519, -43.2105), Flash = 0x10, ExposureProgram = 1,
            })),
            _dir.Write("slow.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec
            {
                Make = "Canon", Model = "Canon EOS R5", ExposureTime = 30, Iso = 70000, ExposureBias = 1.3, Orientation = 8,
            })),
            _dir.Write("zeros.jpg", ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec
            {
                Make = "Canon", BodySerial = "0000", LensSpec = (0, 0, 0, 0), FocalLength = 0, FocalLength35 = 0, FNumber = 0,
                ExposureTime = 0, Iso = 0, Orientation = 0, PixelWidth = 0, PixelHeight = 0, Gps = (0, 0),
            })),
            _dir.Write("plain.jpg", Fixtures.BaseJpeg),
            _dir.Write("apex.jpg", Fixtures.JpegWith(new TiffBuilder()
                .Rational(Ifd.Exif, 0x9202, (6, 1))
                .SRational(Ifd.Exif, 0x9201, 697, 100)
                .Short(Ifd.Exif, 0x9207, 3)
                .Short(Ifd.Exif, 0xA403, 1)
                .Build())),
            _dir.Write("iso-rei.jpg", Fixtures.JpegWith(new TiffBuilder()
                .Short(Ifd.Exif, 0x8827, 65535)
                .Short(Ifd.Exif, 0x8830, 2)
                .Long(Ifd.Exif, 0x8832, 102400)
                .Build())),
            _dir.Write("createdate.jpg", Fixtures.JpegWith(new TiffBuilder()
                .Ascii(Ifd.Exif, 0x9003, "0000:00:00 00:00:00")
                .Ascii(Ifd.Exif, 0x9004, "2023:12:31 23:59:58")
                .Ascii(Ifd.Exif, 0x9012, "-05:00")
                .Ascii(Ifd.Exif, 0x9292, "42")
                .Rational(Ifd.Exif, 0x9206, (350, 100))
                .Rational(Ifd.Exif, 0xA20E, (5000, 1))
                .Short(Ifd.Exif, 0xA210, 2)
                .Build())),
            _dir.Write("raw.dng", ExifWriter.BuildTiff(spec with { PixelWidth = 4000, PixelHeight = 3000 }, Fixtures.EncodeJpeg(32, 24))),
            _dir.Write("scan.tif", ExifWriter.BuildTiff(spec)),
            _dir.Write("xmp-lens.jpg", Fixtures.JpegWith(
                xmp: Fixtures.Xmp("""xmp:Rating="4" aux:Lens="18.0-55.0 mm" """),
                jpeg: ExifWriter.InjectIntoJpeg(Fixtures.BaseJpeg, new ExifSpec { Make = "Canon", FocalLength = 18 }))),
        ];
    }

    private static void AssertEquivalent(PhotoMetadata expected, PhotoMetadata actual)
    {
        var name = Path.GetFileName(expected.Path);
        Assert.True(expected.Make == actual.Make, $"{name}: Make {expected.Make} vs {actual.Make}");
        Assert.True(expected.Model == actual.Model, $"{name}: Model {expected.Model} vs {actual.Model}");
        Assert.True(expected.Software == actual.Software, $"{name}: Software {expected.Software} vs {actual.Software}");
        Assert.True(expected.BodySerial == actual.BodySerial, $"{name}: BodySerial {expected.BodySerial} vs {actual.BodySerial}");
        Assert.True(expected.LensMake == actual.LensMake, $"{name}: LensMake");
        Assert.True(expected.LensModel == actual.LensModel, $"{name}: LensModel {expected.LensModel} vs {actual.LensModel}");
        Assert.True(expected.LensSpec == actual.LensSpec, $"{name}: LensSpec {expected.LensSpec} vs {actual.LensSpec}");
        Close(name, "FocalLengthMm", expected.FocalLengthMm, actual.FocalLengthMm);
        Close(name, "FocalLength35mm", expected.FocalLength35mm, actual.FocalLength35mm);
        Close(name, "FNumber", expected.FNumber, actual.FNumber);
        Close(name, "ExposureTimeS", expected.ExposureTimeS, actual.ExposureTimeS);
        Assert.True(expected.Iso == actual.Iso, $"{name}: Iso {expected.Iso} vs {actual.Iso}");
        Close(name, "ExposureBiasEv", expected.ExposureBiasEv, actual.ExposureBiasEv);
        Assert.True(expected.ExposureProgram == actual.ExposureProgram, $"{name}: ExposureProgram");
        Assert.True(expected.MeteringMode == actual.MeteringMode, $"{name}: MeteringMode");
        Assert.True(expected.WhiteBalance == actual.WhiteBalance, $"{name}: WhiteBalance");
        Assert.True(expected.FlashFired == actual.FlashFired, $"{name}: FlashFired");
        Assert.True(expected.CaptureLocal == actual.CaptureLocal, $"{name}: CaptureLocal {expected.CaptureLocal} vs {actual.CaptureLocal}");
        Assert.True(expected.OffsetTime == actual.OffsetTime, $"{name}: OffsetTime");
        Assert.True(expected.SubSec == actual.SubSec, $"{name}: SubSec");
        Assert.True(expected.ImageUniqueId == actual.ImageUniqueId, $"{name}: ImageUniqueId");
        Assert.True(expected.Width == actual.Width && expected.Height == actual.Height,
            $"{name}: size {expected.Width}x{expected.Height} vs {actual.Width}x{actual.Height}");
        Assert.True(expected.Orientation == actual.Orientation, $"{name}: Orientation");
        Close(name, "FocalPlaneXResolutionPerMm", expected.FocalPlaneXResolutionPerMm, actual.FocalPlaneXResolutionPerMm);
        Close(name, "SubjectDistanceM", expected.SubjectDistanceM, actual.SubjectDistanceM);
        Assert.True(expected.Rating == actual.Rating, $"{name}: Rating {expected.Rating} vs {actual.Rating}");
        Assert.True(expected.HasGps == actual.HasGps, $"{name}: HasGps");
        Close(name, "Latitude", expected.Latitude, actual.Latitude, 1e-6);
        Close(name, "Longitude", expected.Longitude, actual.Longitude, 1e-6);
        Assert.True(expected.Extras.GetValueOrDefault("isoSource") == actual.Extras.GetValueOrDefault("isoSource"), $"{name}: isoSource");
        Assert.True(expected.Extras.GetValueOrDefault("captureSource") == actual.Extras.GetValueOrDefault("captureSource"), $"{name}: captureSource");
    }

    private static void Close(string name, string field, double? expected, double? actual, double tolerance = 1e-9)
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"{name}: {field} {expected} vs {actual}");
            return;
        }

        var scale = Math.Max(1, Math.Abs(expected.Value));
        Assert.True(Math.Abs(expected.Value - actual.Value) <= tolerance * scale, $"{name}: {field} {expected} vs {actual}");
    }
}
