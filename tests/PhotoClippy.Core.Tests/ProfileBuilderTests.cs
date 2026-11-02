using PhotoClippy.Contracts;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Profiling;

namespace PhotoClippy.Core.Tests;

public sealed class ProfileBuilderTests
{
    private static readonly DateTime T0 = new(2023, 5, 1, 10, 0, 0);

    private static CaptureSet Captures(params PhotoMetadata[] metas) =>
        CaptureBuilder.Build([.. metas.Select(m => CaptureBuilder.ToFileFacts(TestData.Src(m.Path), m))]);

    private static ProfileBuilder Builder(CaptureSet set) =>
        new(TestData.Taxonomy, new ProfileBuilderOptions { LensSpecs = set.LensSpecs, Bodies = set.Bodies });

    private static CaptureSet MixedLibrary()
    {
        var metas = new List<PhotoMetadata>();
        for (var i = 0; i < 4; i++)
        {
            metas.Add(TestData.NikonMeta($"/lib/n35-{i}.jpg", T0.AddHours(i), focal: 35));
        }

        for (var i = 0; i < 2; i++)
        {
            metas.Add(TestData.NikonMeta($"/lib/n70-{i}.jpg", T0.AddDays(1).AddHours(i), focal: 70));
        }

        metas.Add(TestData.PhoneMeta("/lib/p0.jpg", T0.AddDays(2)));
        metas.Add(TestData.PhoneMeta("/lib/p1.jpg", T0.AddDays(2).AddHours(1)));
        return Captures([.. metas]);
    }

    private static ShootingProfile BuildMixed()
    {
        var set = MixedLibrary();
        var builder = Builder(set);
        foreach (var c in set.Captures)
        {
            builder.Add(new CaptureAnalysis { Facts = c.Facts, InScope = c.Facts.Device == DeviceClass.InterchangeableLens });
        }

        return builder.Build(new LibraryInfo { Roots = ["/lib"], FilesSeen = 8, FilesWithMetadata = 8 }).Profile;
    }

    [Fact]
    public void Counts_captures_and_scope()
    {
        var profile = BuildMixed();

        Assert.Equal(8, profile.Library.Captures);
        Assert.Equal(6, profile.Library.CapturesInScope);
        Assert.Equal(8, profile.Library.FilesSeen);
        Assert.Equal(new[] { "/lib" }, profile.Library.Roots);
        Assert.Equal(6, profile.Library.DeviceClasses["InterchangeableLens"]);
        Assert.Equal(2, profile.Library.DeviceClasses["Phone"]);
        Assert.Equal(T0, profile.Library.FirstCapture);
        Assert.Equal(T0.AddDays(2).AddHours(1), profile.Library.LastCapture);
    }

    [Fact]
    public void Distribution_shares_sum_to_one()
    {
        var profile = BuildMixed();

        foreach (var id in new[] { DistributionIds.Focal35, DistributionIds.Aperture, DistributionIds.Iso, DistributionIds.Shutter, DistributionIds.Lens, DistributionIds.DeviceClass, DistributionIds.Hour })
        {
            var d = profile.Distributions[id];
            Assert.Equal(1.0, d.Bins.Sum(b => b.Share), 6);
        }
    }

    [Fact]
    public void Exposure_distributions_only_count_in_scope_captures()
    {
        var profile = BuildMixed();

        var focal = profile.Distributions[DistributionIds.Focal35];
        Assert.Equal(6, focal.Bins.Sum(b => b.Count));
        Assert.Equal(4, focal.Bins.Single(b => b.Label == "35mm").Count);
        Assert.Equal(2, focal.Bins.Single(b => b.Label == "70mm").Count);
        Assert.Equal("35mm", focal.Bins[0].Label);
        Assert.Equal("70mm", focal.Bins[^1].Label);

        var lens = Assert.Single(profile.Distributions[DistributionIds.Lens].Bins);
        Assert.Equal("nikkor-z-24-70mm-f/4-s", lens.Key);
        Assert.Equal(6, lens.Count);

        var iso = Assert.Single(profile.Distributions[DistributionIds.Iso].Bins);
        Assert.Equal("400", iso.Label);

        var devices = profile.Distributions[DistributionIds.DeviceClass].Bins;
        Assert.Equal(0.75, devices.Single(b => b.Key == "InterchangeableLens").Share, 6);
        Assert.Equal(0.25, devices.Single(b => b.Key == "Phone").Share, 6);
        Assert.Equal(35, profile.Signals.MedianFocal35);
        Assert.Equal(6, profile.Signals.SampleCount);
    }

    [Fact]
    public void Focal_peaks_are_disjoint_and_never_double_count()
    {
        var profile = BuildMixed();

        Assert.Equal(2, profile.FocalPeaks.Count);
        Assert.Equal(35, profile.FocalPeaks[0].Focal35Mm);
        Assert.Equal(4 / 6.0, profile.FocalPeaks[0].Share, 6);
        Assert.Equal(70, profile.FocalPeaks[1].Focal35Mm);
        Assert.Equal(2 / 6.0, profile.FocalPeaks[1].Share, 6);
        Assert.True(profile.FocalPeaks.Sum(p => p.Share) <= 1 + 1e-9);
        Assert.True(profile.FocalPeaks[0].UpperMm < profile.FocalPeaks[1].LowerMm);
    }

    [Fact]
    public void Junk_captures_are_counted_but_excluded()
    {
        var set = Captures(TestData.NikonMeta("/lib/a.jpg", T0), TestData.NikonMeta("/lib/b.jpg", T0.AddHours(1), focal: 70));
        var builder = Builder(set);
        builder.Add(new CaptureAnalysis { Facts = set.Captures[0].Facts, InScope = true });
        builder.Add(new CaptureAnalysis { Facts = set.Captures[1].Facts, InScope = true, Junk = true });

        var profile = builder.Build(new LibraryInfo()).Profile;

        Assert.Equal(2, profile.Library.Captures);
        Assert.Equal(1, profile.Library.CapturesInScope);
        Assert.Equal(1, profile.Quality.JunkExcluded);
        Assert.Single(profile.Distributions[DistributionIds.Focal35].Bins);
    }

    [Fact]
    public void Frame_features_feed_genres_and_extensions()
    {
        var set = Captures(TestData.NikonMeta("/lib/a.jpg", T0), TestData.NikonMeta("/lib/b.jpg", T0.AddHours(1)));
        var builder = Builder(set);
        builder.AddGenreSource("zeta");
        builder.AddGenreSource("alpha");
        foreach (var c in set.Captures)
        {
            builder.Add(new CaptureAnalysis
            {
                Facts = c.Facts,
                InScope = true,
                Genre = "landscape",
                GenreConfidence = 0.8,
                Composition = new CompositionFeatures
                {
                    Genres = new Dictionary<string, double> { ["landscape"] = 0.8 },
                    ThirdsScore = 0.65,
                    Extras = new Dictionary<string, double> { ["clip.top1Prob"] = 0.75, ["bad"] = double.NaN },
                },
                Color = new ColorFeatures { MeanLuma = 0.5, Extras = new Dictionary<string, double> { ["color.meanHue"] = 0.3 } },
            });
        }

        var profile = builder.Build(new LibraryInfo()).Profile;

        Assert.Equal(2, profile.Library.FramesAnalyzed);
        var genre = Assert.Single(profile.Distributions[DistributionIds.Genre].Bins);
        Assert.Equal("landscape", genre.Key);
        Assert.Equal("Landscape", genre.Label);
        Assert.Equal("Places", Assert.Single(profile.Distributions[DistributionIds.GenreGroup].Bins).Label);
        Assert.Equal("alpha+zeta", profile.Quality.GenreSource);
        Assert.Equal(0.8, profile.Quality.MeanGenreConfidence!.Value, 6);
        Assert.Equal(2, profile.Distributions[DistributionIds.Thirds].Bins[6].Count);

        Assert.Equal(new[] { "color.meanHue", "composition.clip.top1Prob" }, profile.Extensions.Keys.Order(StringComparer.Ordinal));
        var clip = profile.Extensions["composition.clip.top1Prob"];
        Assert.Equal("Clip · top 1 prob", clip.Title);
        Assert.Equal(10, clip.Bins.Count);
        Assert.Equal(2, clip.Bins[7].Count);
        Assert.Equal("Mean hue", profile.Extensions["color.meanHue"].Title);
    }

    [Fact]
    public void Missing_fields_are_reported_in_quality()
    {
        var bare = TestData.NikonMeta("/lib/a.jpg", T0) with { FocalLengthMm = null, FocalLength35mm = null, Iso = null };
        var set = Captures(bare, TestData.NikonMeta("/lib/b.jpg", T0.AddHours(1)));
        var builder = Builder(set);
        foreach (var c in set.Captures)
        {
            builder.Add(new CaptureAnalysis { Facts = c.Facts, InScope = true });
        }

        var profile = builder.Build(new LibraryInfo { MetadataErrors = 3, Notes = ["note"] }).Profile;

        Assert.Equal(1, profile.Quality.MissingFocal);
        Assert.Equal(1, profile.Quality.MissingIso);
        Assert.Equal(0, profile.Quality.MissingAperture);
        Assert.Equal(3, profile.Quality.MetadataErrors);
        Assert.Equal(new[] { "note" }, profile.Quality.Notes);
        Assert.Equal("none", profile.Quality.GenreSource);
    }

    [Fact]
    public void Empty_builder_produces_empty_profile()
    {
        var profile = new ProfileBuilder(TestData.Taxonomy, new ProfileBuilderOptions()).Build(new LibraryInfo()).Profile;

        Assert.Equal(0, profile.Library.Captures);
        Assert.Empty(profile.FocalPeaks);
        Assert.Empty(profile.Extensions);
        Assert.False(profile.Distributions.ContainsKey(DistributionIds.Focal35));
        Assert.Null(profile.Signals.MedianFocal35);
    }

    [Theory]
    [InlineData("composition.clip.top1Prob", "Clip · top 1 prob")]
    [InlineData("color.meanHue", "Mean hue")]
    [InlineData("single", "Single")]
    [InlineData("x.snake_case", "Snake case")]
    [InlineData("a.b.c", "B · c")]
    [InlineData("color.HDRScore", "Hdrscore")]
    [InlineData("...", "...")]
    public void Humanizes_extension_keys(string key, string expected) => Assert.Equal(expected, ProfileBuilder.HumanizeExtension(key));
}
