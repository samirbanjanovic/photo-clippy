using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Testing;

namespace PhotoClippy.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}

/// <summary>A scratch directory under the test output folder, removed on dispose.</summary>
public sealed class TestDir : IDisposable
{
    public TestDir(string name = "t")
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteBytes(string relative, byte[] bytes)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string WriteText(string relative, string text) => WriteBytes(relative, System.Text.Encoding.UTF8.GetBytes(text));

    public string WriteTiff(string relative, ExifSpec? spec = null) => WriteBytes(relative, ExifWriter.BuildTiff(spec ?? TestData.NikonSpec(new DateTime(2023, 5, 1, 10, 0, 0))));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestData
{
    private static readonly Lazy<GenreTaxonomy> LazyTaxonomy = new(() => TaxonomyLoader.Load(AppPaths.FindDataDirectory()));

    public static GenreTaxonomy Taxonomy => LazyTaxonomy.Value;

    public static SourceFile Src(string path, MediaKind kind = MediaKind.Jpeg, long size = 1000, DateTimeOffset? lastWrite = null) => new()
    {
        Path = path,
        Size = size,
        LastWriteUtc = lastWrite ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Kind = kind,
    };

    public static PhotoMetadata NikonMeta(string path, DateTime? when = null, double focal = 35, double fNumber = 4, int iso = 400) => new()
    {
        Path = path,
        Extractor = "test",
        Make = "NIKON CORPORATION",
        Model = "NIKON Z 6_2",
        LensModel = "NIKKOR Z 24-70mm f/4 S",
        LensSpec = new LensSpecification(24, 70, 4, 4),
        FocalLengthMm = focal,
        FocalLength35mm = focal,
        FNumber = fNumber,
        ExposureTimeS = 1 / 60.0,
        Iso = iso,
        CaptureLocal = when ?? new DateTime(2023, 5, 1, 10, 0, 0),
        Width = 6048,
        Height = 4024,
    };

    public static PhotoMetadata PhoneMeta(string path, DateTime? when = null) => new()
    {
        Path = path,
        Make = "Apple",
        Model = "iPhone 13",
        LensModel = "iPhone 13 back camera 5.1mm f/1.6",
        FocalLengthMm = 5.1,
        FocalLength35mm = 26,
        FNumber = 1.6,
        ExposureTimeS = 1 / 120.0,
        Iso = 50,
        CaptureLocal = when ?? new DateTime(2023, 6, 1, 12, 0, 0),
    };

    public static ExifSpec NikonSpec(DateTime when, double focal = 35) => new()
    {
        Make = "NIKON CORPORATION",
        Model = "NIKON Z 6_2",
        LensMake = "Nikon",
        LensModel = "NIKKOR Z 24-70mm f/4 S",
        LensSpec = (24, 70, 4, 4),
        FocalLength = focal,
        FocalLength35 = (int)focal,
        FNumber = 4,
        ExposureTime = 1 / 60.0,
        Iso = 3200,
        DateTimeOriginal = when,
    };
}
