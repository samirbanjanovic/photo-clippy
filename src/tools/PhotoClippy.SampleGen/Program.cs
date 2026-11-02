using System.Globalization;
using PhotoClippy.Testing;
using SkiaSharp;

// Generates a deterministic synthetic photo library (JPEGs with realistic EXIF, a few RAW+JPEG pairs, phone shots,
// screenshots and one corrupt file) for demos, integration tests and benchmarking the pipeline end to end.

var options = SampleOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: samplegen <output-folder> [--count 600] [--seed 42] [--size 1500]");
    return 2;
}

var generator = new LibraryGenerator(options);
var stats = generator.Generate();
Console.WriteLine($"Wrote {stats.Files} files ({stats.Captures} captures, {stats.RawPairs} RAW+JPEG pairs, {stats.Junk} junk) to {Path.GetFullPath(options.Output)} in {stats.Elapsed.TotalSeconds:0.0}s");
return 0;

internal sealed record SampleOptions(string Output, int Count, int Seed, int LongEdge)
{
    public static SampleOptions? Parse(string[] args)
    {
        string? output = null;
        int count = 600, seed = 42, size = 1500;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count" when i + 1 < args.Length:
                    count = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--seed" when i + 1 < args.Length:
                    seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--size" when i + 1 < args.Length:
                    size = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-h" or "--help":
                    return null;
                default:
                    if (args[i].StartsWith('-') || output is not null)
                    {
                        return null;
                    }

                    output = args[i];
                    break;
            }
        }

        return output is null || count < 1 || size < 64 ? null : new SampleOptions(output, count, seed, size);
    }
}

internal sealed record Lens(string Make, string Model, double MinFocal, double MaxFocal, double MaxApertureWide, double MaxApertureTele)
{
    public bool IsZoom => MaxFocal > MinFocal;
}

internal sealed record Body(string Make, string Model, string FilePrefix, double Crop, double FocalPlaneResPerMm, int PixelWidth, int PixelHeight, bool Phone = false);

internal enum Scene
{
    Landscape,
    Cityscape,
    Night,
    Street,
    Portrait,
    Astro,
    Interior,
    Food,
    Pets,
}

internal sealed record Shot(Scene Scene, Lens Lens, double Focal, double FNumber, double SceneEv);

internal sealed record GenerationStats(int Files, int Captures, int RawPairs, int Junk, TimeSpan Elapsed);

internal sealed class LibraryGenerator(SampleOptions options)
{
    private static readonly double[] ShutterStops =
    [
        30, 25, 20, 15, 13, 10, 8, 6, 5, 4, 3.2, 2.5, 2, 1.6, 1.3, 1, 0.8, 0.6, 0.5, 0.4, 0.3, 1 / 4.0, 1 / 5.0, 1 / 6.0, 1 / 8.0,
        1 / 10.0, 1 / 13.0, 1 / 15.0, 1 / 20.0, 1 / 25.0, 1 / 30.0, 1 / 40.0, 1 / 50.0, 1 / 60.0, 1 / 80.0, 1 / 100.0, 1 / 125.0,
        1 / 160.0, 1 / 200.0, 1 / 250.0, 1 / 320.0, 1 / 400.0, 1 / 500.0, 1 / 640.0, 1 / 800.0, 1 / 1000.0, 1 / 1250.0, 1 / 1600.0,
        1 / 2000.0, 1 / 2500.0, 1 / 3200.0, 1 / 4000.0, 1 / 5000.0, 1 / 6400.0, 1 / 8000.0,
    ];

    private static readonly int[] IsoStops = [100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800, 16000, 20000, 25600, 51200];

    private static readonly double[] FStops = [1.2, 1.4, 1.6, 1.8, 2, 2.2, 2.5, 2.8, 3.2, 3.5, 4, 4.5, 5, 5.6, 6.3, 7.1, 8, 9, 10, 11, 13, 16];

    private static readonly Body ZBody = new("NIKON CORPORATION", "NIKON Z 6_2", "DSC_", 1.0, 168.0, 6048, 4024);
    private static readonly Body FujiBody = new("FUJIFILM", "X-T3", "DSCF", 1.5, 1001.0 / 4.0, 6240, 4160);
    private static readonly Body Phone = new("Apple", "iPhone 15 Pro", "IMG_", 3.5, 0, 4032, 3024, Phone: true);

    private static readonly Lens Z2470 = new("Nikon", "NIKKOR Z 24-70mm f/4 S", 24, 70, 4, 4);
    private static readonly Lens Z50 = new("Nikon", "NIKKOR Z 50mm f/1.8 S", 50, 50, 1.8, 1.8);
    private static readonly Lens Z85 = new("Nikon", "NIKKOR Z 85mm f/1.8 S", 85, 85, 1.8, 1.8);
    private static readonly Lens Xf1855 = new("FUJIFILM", "XF18-55mmF2.8-4 R LM OIS", 18, 55, 2.8, 4);
    private static readonly Lens Xf35 = new("FUJIFILM", "XF35mmF1.4 R", 35, 35, 1.4, 1.4);
    private static readonly Lens PhoneMain = new("Apple", "iPhone 15 Pro back triple camera 6.765mm f/1.78", 6.765, 6.765, 1.78, 1.78);

    private readonly Random _random = new(options.Seed);
    private readonly Dictionary<string, int> _counters = [];

    public GenerationStats Generate()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Directory.CreateDirectory(options.Output);
        int files = 0, captures = 0, rawPairs = 0, junk = 0;
        var start = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var span = (end - start).TotalDays;

        for (var i = 0; i < options.Count; i++)
        {
            var date = start.AddDays(span * i / options.Count + _random.NextDouble() * 2);
            var roll = _random.NextDouble();
            Body body;
            if (roll < 0.14)
            {
                body = Phone;
            }
            else if (date < new DateTime(2022, 9, 1))
            {
                body = FujiBody;
            }
            else
            {
                body = ZBody;
            }

            var shot = PlanShot(body);
            var when = date.Date.AddHours(HourFor(shot.Scene)).AddMinutes(_random.Next(60)).AddSeconds(_random.Next(60));
            var portraitOrientation = shot.Scene is Scene.Portrait ? _random.NextDouble() < 0.7 : _random.NextDouble() < 0.12;
            var (width, height) = Dimensions(body, portraitOrientation);
            using var bitmap = Paint(shot.Scene, width, height);
            var jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg, 84);

            var (exposure, iso) = Expose(shot, body);
            var spec = new ExifSpec
            {
                Make = body.Make,
                Model = body.Model,
                Software = body.Phone ? "18.6" : "Ver.1.60",
                BodySerial = body.Phone ? null : "SN" + (body.FilePrefix.GetHashCode(StringComparison.Ordinal) & 0xFFFFF).ToString(CultureInfo.InvariantCulture),
                LensMake = shot.Lens.Make,
                LensModel = shot.Lens.Model,
                LensSpec = body.Phone ? null : (shot.Lens.MinFocal, shot.Lens.MaxFocal, shot.Lens.MaxApertureWide, shot.Lens.MaxApertureTele),
                FocalLength = shot.Focal,
                FocalLength35 = (int)Math.Round(shot.Focal * body.Crop),
                FNumber = shot.FNumber,
                ExposureTime = exposure,
                Iso = iso,
                ExposureBias = Math.Round((_random.NextDouble() - 0.6) * 2) / 3,
                ExposureProgram = shot.Scene is Scene.Landscape or Scene.Astro ? 1 : 3,
                Flash = shot.Scene is Scene.Interior && _random.NextDouble() < 0.1 ? 1 : 16,
                DateTimeOriginal = when,
                SubSec = _random.Next(100).ToString("00", CultureInfo.InvariantCulture),
                OffsetTime = "+01:00",
                PixelWidth = portraitOrientation ? body.PixelHeight : body.PixelWidth,
                PixelHeight = portraitOrientation ? body.PixelWidth : body.PixelHeight,
                FocalPlaneXResolutionPerMm = body.FocalPlaneResPerMm > 0 ? body.FocalPlaneResPerMm : null,
                Gps = _random.NextDouble() < (body.Phone ? 0.9 : 0.2) ? (59.91 + _random.NextDouble(), 10.75 + _random.NextDouble()) : null,
                Rating = _random.NextDouble() < 0.15 ? _random.Next(1, 6) : null,
            };

            var folder = Path.Combine(options.Output, when.Year.ToString(CultureInfo.InvariantCulture), when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var stem = body.FilePrefix + Next(body.FilePrefix).ToString("0000", CultureInfo.InvariantCulture);
            File.WriteAllBytes(Path.Combine(folder, stem + ".JPG"), ExifWriter.InjectIntoJpeg(jpeg, spec));
            files++;
            captures++;

            if (body == ZBody && _random.NextDouble() < 0.12)
            {
                using var preview = Downscale(bitmap, 640);
                File.WriteAllBytes(Path.Combine(folder, stem + ".NEF"), ExifWriter.BuildTiff(spec, Encode(preview, SKEncodedImageFormat.Jpeg, 80)));
                files++;
                rawPairs++;
            }
        }

        junk += WriteJunk();
        files += junk;
        return new GenerationStats(files, captures, rawPairs, junk, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    private Shot PlanShot(Body body)
    {
        if (body.Phone)
        {
            var phoneScene = Pick((Scene.Food, 3), (Scene.Pets, 3), (Scene.Street, 3), (Scene.Landscape, 2), (Scene.Interior, 2), (Scene.Night, 1));
            return new Shot(phoneScene, PhoneMain, PhoneMain.MinFocal, 1.78, SceneEv(phoneScene));
        }

        var scene = Pick(
            (Scene.Landscape, 26), (Scene.Night, 12), (Scene.Cityscape, 7), (Scene.Street, 14), (Scene.Portrait, 13),
            (Scene.Astro, 4), (Scene.Interior, 8), (Scene.Food, 6), (Scene.Pets, 10));
        var fuji = body == FujiBody;
        var zoom = fuji ? Xf1855 : Z2470;
        var fifty = fuji ? Xf35 : Z50;
        var eightyFive = fuji ? Xf35 : Z85;

        // Habits: wide end of the zoom pinned for landscapes/astro/interiors, fast primes wide open at night and for people.
        return scene switch
        {
            Scene.Landscape => ZoomShot(scene, zoom, wideBias: 0.62, fRange: (7.1, 11)),
            Scene.Cityscape => ZoomShot(scene, zoom, wideBias: 0.35, fRange: (5.6, 8)),
            Scene.Astro => ZoomShot(scene, zoom, wideBias: 1.0, fRange: (0, 0)),
            Scene.Interior => ZoomShot(scene, zoom, wideBias: 0.8, fRange: (0, 4)),
            Scene.Night => Chance(0.55) ? PrimeShot(scene, fifty, (0, 2.2)) : ZoomShot(scene, zoom, wideBias: 0.45, fRange: (0, 0)),
            Scene.Street => Chance(0.45) ? PrimeShot(scene, fifty, (2.8, 5.6)) : ZoomShot(scene, zoom, wideBias: 0.2, fRange: (5.6, 8)),
            Scene.Portrait => Chance(0.65) ? PrimeShot(scene, eightyFive, (0, 2.8)) : PrimeShot(scene, fifty, (0, 2.5)),
            Scene.Food => PrimeShot(scene, fifty, (0, 2.8)),
            Scene.Pets => Chance(0.5) ? PrimeShot(scene, eightyFive, (0, 2.8)) : ZoomShot(scene, zoom, wideBias: -0.6, fRange: (0, 5.6)),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>wideBias ≥ 0: probability of pinning the wide end; negative: probability of pinning the long end.</summary>
    private Shot ZoomShot(Scene scene, Lens lens, double wideBias, (double Min, double Max) fRange)
    {
        double focal;
        if (wideBias >= 0 && Chance(wideBias))
        {
            focal = lens.MinFocal;
        }
        else if (wideBias < 0 && Chance(-wideBias))
        {
            focal = lens.MaxFocal;
        }
        else
        {
            var steps = new[] { 24, 28, 35, 40, 50, 58, 70 }.Select(f => f * lens.MinFocal / 24).Where(f => f <= lens.MaxFocal).ToArray();
            focal = Math.Round(steps[_random.Next(steps.Length)]);
        }

        var maxAperture = lens.MaxApertureWide + ((lens.MaxApertureTele - lens.MaxApertureWide) * (focal - lens.MinFocal) / Math.Max(1, lens.MaxFocal - lens.MinFocal));
        return new Shot(scene, lens, focal, Aperture(maxAperture, fRange), SceneEv(scene));
    }

    private Shot PrimeShot(Scene scene, Lens lens, (double Min, double Max) fRange) =>
        new(scene, lens, lens.MinFocal, Aperture(lens.MaxApertureWide, fRange), SceneEv(scene));

    /// <summary>A stop inside <paramref name="range"/>; (0, x) means "wide open up to x", (0, 0) means wide open.</summary>
    private double Aperture(double maxAperture, (double Min, double Max) range)
    {
        var low = Math.Max(maxAperture, range.Min);
        var high = Math.Max(low, range.Max);
        var stops = FStops.Where(f => f >= low - 0.05 && f <= high + 0.05).ToArray();
        if (stops.Length == 0 || (range.Min == 0 && Chance(0.55)))
        {
            return maxAperture;
        }

        return stops[_random.Next(stops.Length)];
    }

    private double SceneEv(Scene scene) => scene switch
    {
        Scene.Landscape => Chance(0.5) ? Jitter(11, 1.5) : Jitter(14, 1),
        Scene.Cityscape => Jitter(10, 2),
        Scene.Night => Jitter(4, 1.5),
        Scene.Street => Jitter(13, 1.5),
        Scene.Portrait => Chance(0.3) ? Jitter(7, 1) : Jitter(12, 1.5),
        Scene.Astro => Jitter(-4, 0.7),
        Scene.Interior => Jitter(6.5, 1),
        Scene.Food => Jitter(8, 1),
        Scene.Pets => Jitter(10, 2),
        _ => 12,
    };

    private (double Exposure, int Iso) Expose(Shot shot, Body body)
    {
        var n2 = shot.FNumber * shot.FNumber;
        var focal35 = shot.Focal * body.Crop;
        var slowest = shot.Scene == Scene.Astro ? 20 : shot.Scene is Scene.Landscape or Scene.Cityscape && Chance(0.15) ? 2 : Math.Min(1 / 30.0, 1 / focal35);
        var t = n2 / Math.Pow(2, shot.SceneEv);
        var iso = 100.0;
        if (t > slowest)
        {
            iso = 100 * t / slowest;
            t = slowest;
        }

        var isoStop = IsoStops.MinBy(s => Math.Abs(Math.Log2(s / iso)));
        var shutter = ShutterStops.MinBy(s => Math.Abs(Math.Log2(s / t)));
        if (body.Phone)
        {
            isoStop = Math.Clamp((int)Math.Round(iso / 10) * 10, 50, 8000);
        }

        return (shutter, isoStop);
    }

    private static int HourFor(Scene scene) => scene switch
    {
        Scene.Night or Scene.Cityscape => 20 + (scene == Scene.Night ? 1 : 0),
        Scene.Astro => 23,
        Scene.Landscape => 7,
        Scene.Food => 13,
        Scene.Interior => 16,
        _ => 11,
    };

    private (int Width, int Height) Dimensions(Body body, bool portrait)
    {
        var ratio = (double)body.PixelWidth / body.PixelHeight;
        var longEdge = options.LongEdge;
        var shortEdge = (int)Math.Round(longEdge / ratio);
        return portrait ? (shortEdge, longEdge) : (longEdge, shortEdge);
    }

    private int WriteJunk()
    {
        var folder = Path.Combine(options.Output, "Screenshots");
        Directory.CreateDirectory(folder);
        var written = 0;
        for (var i = 0; i < Math.Max(2, options.Count / 60); i++)
        {
            using var bitmap = PaintScreenshot(590, 1278);
            File.WriteAllBytes(Path.Combine(folder, $"Screenshot {i + 1:000}.png"), Encode(bitmap, SKEncodedImageFormat.Png, 100));
            written++;
        }

        File.WriteAllBytes(Path.Combine(options.Output, "broken.jpg"), [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x45, 0x78, 0x69, 0x66]);
        File.WriteAllText(Path.Combine(options.Output, "notes.txt"), "Not an image.");
        return written + 1;
    }

    private int Next(string prefix)
    {
        _counters.TryGetValue(prefix, out var n);
        _counters[prefix] = ++n;
        return n;
    }

    private bool Chance(double p) => _random.NextDouble() < p;

    private double Jitter(double center, double spread) => center + ((_random.NextDouble() * 2) - 1) * spread;

    private Scene Pick(params (Scene Scene, int Weight)[] weights)
    {
        var total = weights.Sum(w => w.Weight);
        var roll = _random.Next(total);
        foreach (var (scene, weight) in weights)
        {
            if ((roll -= weight) < 0)
            {
                return scene;
            }
        }

        return weights[^1].Scene;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    private static SKBitmap Downscale(SKBitmap source, int longEdge)
    {
        var scale = (double)longEdge / Math.Max(source.Width, source.Height);
        return source.Resize(new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear));
    }

    // ---------------------------------------------------------------- painting

    private SKBitmap Paint(Scene scene, int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        switch (scene)
        {
            case Scene.Landscape:
                PaintLandscape(canvas, width, height);
                break;
            case Scene.Cityscape:
                PaintCity(canvas, width, height, night: Chance(0.6));
                break;
            case Scene.Night:
                PaintCity(canvas, width, height, night: true);
                break;
            case Scene.Street:
                PaintStreet(canvas, width, height);
                break;
            case Scene.Portrait:
                PaintPortrait(canvas, width, height);
                break;
            case Scene.Astro:
                PaintAstro(canvas, width, height);
                break;
            case Scene.Interior:
                PaintInterior(canvas, width, height);
                break;
            case Scene.Food:
                PaintFood(canvas, width, height);
                break;
            case Scene.Pets:
                PaintPet(canvas, width, height);
                break;
        }

        AddGrain(bitmap, scene is Scene.Night or Scene.Astro ? 10 : 4);
        return bitmap;
    }

    private void PaintLandscape(SKCanvas canvas, int w, int h)
    {
        var golden = Chance(0.5);
        var horizon = h * (Chance(0.6) ? 0.62 : 0.4);
        var skyTop = golden ? new SKColor(70, 90, 150) : new SKColor(60, 120, 200);
        var skyBottom = golden ? new SKColor(250, 180, 110) : new SKColor(180, 215, 240);
        Gradient(canvas, new SKRect(0, 0, w, (float)horizon), skyTop, skyBottom);

        for (var layer = 0; layer < 3; layer++)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo(0, (float)horizon);
            var baseY = horizon - (h * 0.18 * (3 - layer) / 3);
            for (var x = 0; x <= w; x += w / 12)
            {
                builder.LineTo(x, (float)(baseY + _random.NextDouble() * h * 0.12));
            }

            builder.LineTo(w, (float)horizon);
            builder.Close();
            using var path = builder.Detach();
            var shade = (byte)(60 + layer * 25);
            using var paint = new SKPaint { Color = golden ? new SKColor((byte)(shade + 30), (byte)(shade - 10), (byte)(shade + 20)) : new SKColor((byte)(shade - 20), (byte)(shade + 10), (byte)(shade + 40)), IsAntialias = true };
            canvas.DrawPath(path, paint);
        }

        var ground = Chance(0.4) ? new SKColor(60, 110, 150) : new SKColor(70, 110, 50);
        Gradient(canvas, new SKRect(0, (float)horizon, w, h), ground, Darken(ground, 0.5));
    }

    private void PaintCity(SKCanvas canvas, int w, int h, bool night)
    {
        Gradient(canvas, new SKRect(0, 0, w, h), night ? new SKColor(8, 10, 30) : new SKColor(120, 160, 210), night ? new SKColor(40, 30, 60) : new SKColor(210, 220, 230));
        using var building = new SKPaint { IsAntialias = true };
        using var window = new SKPaint { Color = night ? new SKColor(255, 200, 110) : new SKColor(140, 170, 200) };
        var x = 0f;
        while (x < w)
        {
            var bw = (float)(w * (0.05 + _random.NextDouble() * 0.09));
            var bh = (float)(h * (0.25 + _random.NextDouble() * 0.5));
            building.Color = night ? new SKColor(18, 18, 28) : new SKColor((byte)_random.Next(80, 150), (byte)_random.Next(80, 140), (byte)_random.Next(80, 130));
            canvas.DrawRect(x, h - bh, bw, bh, building);
            for (var wy = h - bh + 8; wy < h - 12; wy += 18)
            {
                for (var wx = x + 6; wx < x + bw - 8; wx += 14)
                {
                    if (!night || Chance(0.35))
                    {
                        canvas.DrawRect(wx, wy, 6, 9, window);
                    }
                }
            }

            x += bw + 2;
        }

        if (night)
        {
            using var glow = new SKPaint { Color = new SKColor(255, 170, 80, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12), IsAntialias = true };
            for (var i = 0; i < 25; i++)
            {
                canvas.DrawCircle((float)(_random.NextDouble() * w), (float)(h * (0.7 + _random.NextDouble() * 0.3)), 6 + _random.Next(10), glow);
            }
        }
    }

    private void PaintStreet(SKCanvas canvas, int w, int h)
    {
        PaintCity(canvas, w, h, night: false);
        using var road = new SKPaint { Color = new SKColor(90, 90, 95) };
        canvas.DrawRect(0, h * 0.8f, w, h * 0.2f, road);
        using var person = new SKPaint { IsAntialias = true };
        using var skin = new SKPaint { Color = new SKColor(220, 180, 150), IsAntialias = true };
        for (var i = 0; i < 5; i++)
        {
            person.Color = new SKColor((byte)_random.Next(20, 200), (byte)_random.Next(20, 120), (byte)_random.Next(20, 120));
            var px = (float)(_random.NextDouble() * w);
            var ph = (float)(h * (0.15 + _random.NextDouble() * 0.2));
            canvas.DrawRoundRect(px, h * 0.85f - ph, ph * 0.3f, ph, 8, 8, person);
            canvas.DrawCircle(px + ph * 0.15f, h * 0.85f - ph - ph * 0.1f, ph * 0.1f, skin);
        }
    }

    private void PaintPortrait(SKCanvas canvas, int w, int h)
    {
        var background = new SKColor((byte)_random.Next(60, 160), (byte)_random.Next(70, 150), (byte)_random.Next(60, 140));
        Gradient(canvas, new SKRect(0, 0, w, h), background, Darken(background, 0.6));
        using (var bokeh = new SKPaint { Color = new SKColor(255, 240, 210, 70), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 25), IsAntialias = true })
        {
            for (var i = 0; i < 14; i++)
            {
                canvas.DrawCircle((float)(_random.NextDouble() * w), (float)(_random.NextDouble() * h * 0.6), 20 + _random.Next(40), bokeh);
            }
        }

        var cx = w * (Chance(0.5) ? 0.38f : 0.62f);
        using var shoulders = new SKPaint { Color = new SKColor((byte)_random.Next(20, 90), (byte)_random.Next(20, 90), (byte)_random.Next(40, 120)), IsAntialias = true };
        canvas.DrawOval(cx, h * 0.95f, w * 0.32f, h * 0.28f, shoulders);
        using var skin = new SKPaint { Color = new SKColor(226, 182, 150), IsAntialias = true };
        canvas.DrawOval(cx, h * 0.45f, w * 0.13f, h * 0.17f, skin);
        using var hair = new SKPaint { Color = new SKColor(60, 40, 30), IsAntialias = true };
        canvas.DrawArc(new SKRect(cx - w * 0.14f, h * 0.26f, cx + w * 0.14f, h * 0.5f), 180, 180, true, hair);
        using var eye = new SKPaint { Color = new SKColor(40, 30, 30), IsAntialias = true };
        canvas.DrawCircle(cx - w * 0.045f, h * 0.44f, w * 0.012f, eye);
        canvas.DrawCircle(cx + w * 0.045f, h * 0.44f, w * 0.012f, eye);
    }

    private void PaintAstro(SKCanvas canvas, int w, int h)
    {
        Gradient(canvas, new SKRect(0, 0, w, h), new SKColor(4, 6, 18), new SKColor(18, 22, 44));
        using (var band = new SKPaint { Color = new SKColor(150, 140, 170, 60), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, h * 0.08f), IsAntialias = true })
        {
            canvas.Save();
            canvas.RotateDegrees(-25, w / 2f, h / 2f);
            canvas.DrawOval(w / 2f, h / 2f, w * 0.7f, h * 0.1f, band);
            canvas.Restore();
        }

        using var star = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < 900; i++)
        {
            star.Color = new SKColor(255, 255, 255, (byte)_random.Next(80, 255));
            canvas.DrawCircle((float)(_random.NextDouble() * w), (float)(_random.NextDouble() * h * 0.85), (float)(_random.NextDouble() * 1.6), star);
        }

        using var ground = new SKPaint { Color = new SKColor(5, 5, 8) };
        canvas.DrawRect(0, h * 0.85f, w, h * 0.15f, ground);
    }

    private void PaintInterior(SKCanvas canvas, int w, int h)
    {
        Gradient(canvas, new SKRect(0, 0, w, h), new SKColor(210, 190, 160), new SKColor(120, 95, 70));
        using var window = new SKPaint { Color = new SKColor(235, 240, 245) };
        canvas.DrawRect(w * 0.1f, h * 0.15f, w * 0.25f, h * 0.45f, window);
        canvas.DrawRect(w * 0.6f, h * 0.15f, w * 0.25f, h * 0.45f, window);
        using var floor = new SKPaint { Color = new SKColor(140, 100, 70) };
        canvas.DrawRect(0, h * 0.72f, w, h * 0.28f, floor);
        using var sofa = new SKPaint { Color = new SKColor((byte)_random.Next(60, 120), (byte)_random.Next(80, 120), (byte)_random.Next(90, 140)), IsAntialias = true };
        canvas.DrawRoundRect(w * 0.3f, h * 0.55f, w * 0.4f, h * 0.2f, 20, 20, sofa);
    }

    private void PaintFood(SKCanvas canvas, int w, int h)
    {
        Gradient(canvas, new SKRect(0, 0, w, h), new SKColor(120, 80, 50), new SKColor(90, 60, 40));
        using var plate = new SKPaint { Color = new SKColor(245, 245, 240), IsAntialias = true };
        var r = Math.Min(w, h) * 0.38f;
        canvas.DrawCircle(w / 2f, h / 2f, r, plate);
        using var food = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 9; i++)
        {
            food.Color = new SKColor((byte)_random.Next(120, 240), (byte)_random.Next(40, 200), (byte)_random.Next(20, 90));
            canvas.DrawCircle(w / 2f + (float)((_random.NextDouble() - 0.5) * r), h / 2f + (float)((_random.NextDouble() - 0.5) * r), r * (0.12f + (float)_random.NextDouble() * 0.15f), food);
        }
    }

    private void PaintPet(SKCanvas canvas, int w, int h)
    {
        Gradient(canvas, new SKRect(0, 0, w, h), new SKColor(110, 150, 90), new SKColor(60, 100, 50));
        var fur = Chance(0.5) ? new SKColor(160, 110, 60) : new SKColor(40, 40, 40);
        using var body = new SKPaint { Color = fur, IsAntialias = true };
        var cx = w * 0.5f;
        var cy = h * 0.6f;
        canvas.DrawOval(cx, cy, w * 0.22f, h * 0.16f, body);
        canvas.DrawCircle(cx + w * 0.2f, cy - h * 0.15f, h * 0.12f, body);
        canvas.DrawOval(cx + w * 0.15f, cy - h * 0.27f, h * 0.035f, h * 0.07f, body);
        canvas.DrawOval(cx + w * 0.25f, cy - h * 0.27f, h * 0.035f, h * 0.07f, body);
        using var eye = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawCircle(cx + w * 0.18f, cy - h * 0.17f, h * 0.012f, eye);
        canvas.DrawCircle(cx + w * 0.23f, cy - h * 0.17f, h * 0.012f, eye);
    }

    private SKBitmap PaintScreenshot(int w, int h)
    {
        var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var bar = new SKPaint { Color = new SKColor(245, 245, 247) };
        canvas.DrawRect(0, 0, w, 90, bar);
        using var text = new SKPaint { Color = new SKColor(60, 60, 67) };
        for (var y = 130; y < h - 80; y += 34)
        {
            canvas.DrawRect(24, y, (float)(w * (0.4 + _random.NextDouble() * 0.5)), 12, text);
        }

        using var button = new SKPaint { Color = new SKColor(0, 122, 255), IsAntialias = true };
        canvas.DrawRoundRect(24, h - 70, w - 48, 44, 12, 12, button);
        return bitmap;
    }

    private static void Gradient(SKCanvas canvas, SKRect rect, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(rect, paint);
    }

    private static SKColor Darken(SKColor color, double factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor));

    private void AddGrain(SKBitmap bitmap, int amplitude)
    {
        var pixels = bitmap.Pixels;
        for (var i = 0; i < pixels.Length; i += 3)
        {
            var p = pixels[i];
            var n = _random.Next(-amplitude, amplitude + 1);
            pixels[i] = new SKColor(Clamp(p.Red + n), Clamp(p.Green + n), Clamp(p.Blue + n), p.Alpha);
        }

        bitmap.Pixels = pixels;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
