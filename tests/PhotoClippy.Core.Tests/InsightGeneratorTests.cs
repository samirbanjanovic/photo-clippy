using PhotoClippy.Contracts;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Profiling;

namespace PhotoClippy.Core.Tests;

public sealed class InsightGeneratorTests
{
    private static readonly DateTime T0 = new(2023, 5, 1, 10, 0, 0);

    private static ShootingProfile Build(params PhotoMetadata[] metas)
    {
        var set = CaptureBuilder.Build([.. metas.Select(m => CaptureBuilder.ToFileFacts(TestData.Src(m.Path), m))]);
        var builder = new ProfileBuilder(TestData.Taxonomy, new ProfileBuilderOptions { LensSpecs = set.LensSpecs, Bodies = set.Bodies });
        foreach (var c in set.Captures)
        {
            builder.Add(new CaptureAnalysis { Facts = c.Facts, InScope = c.Facts.Device == DeviceClass.InterchangeableLens });
        }

        return builder.Build(new LibraryInfo { Roots = ["/lib"], FilesSeen = metas.Length, FilesWithMetadata = metas.Length }).Profile;
    }

    private static ShootingProfile NikonProfile()
    {
        var metas = new List<PhotoMetadata>();
        for (var i = 0; i < 6; i++)
        {
            metas.Add(TestData.NikonMeta($"/lib/n35-{i}.jpg", T0.AddHours(i), focal: 35, fNumber: 2.8));
        }

        for (var i = 0; i < 2; i++)
        {
            metas.Add(TestData.NikonMeta($"/lib/n70-{i}.jpg", T0.AddDays(1).AddHours(i), focal: 70, fNumber: 2.8));
        }

        metas.Add(TestData.PhoneMeta("/lib/p0.jpg", T0.AddDays(2)));
        return Build([.. metas]);
    }

    private static LensSpec Lens(string text)
    {
        Assert.True(LensSpec.TryParse(text, out var spec, out var error), error);
        return spec!;
    }

    [Fact]
    public void Empty_library_gets_a_single_attention_overview()
    {
        var insights = InsightGenerator.Generate(Build(), null, TestData.Taxonomy);

        var overview = Assert.Single(insights, i => i.Section == ReportSections.Overview);
        Assert.Equal("No photos found", overview.Title);
        Assert.Equal(InsightTone.Attention, overview.Tone);
        var method = Assert.Single(insights, i => i.Title == "Subject recognition");
        Assert.StartsWith("No subject recognition ran", method.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(insights, i => i.Section == ReportSections.Focal);
    }

    [Fact]
    public void Populated_profile_produces_overview_focal_aperture_and_gear()
    {
        var insights = InsightGenerator.Generate(NikonProfile(), null, TestData.Taxonomy);

        var library = Assert.Single(insights, i => i.Title == "Library");
        Assert.Equal("8", library.Metric);
        Assert.Contains("9 captures from 9 files", library.Text, StringComparison.Ordinal);
        Assert.Contains("interchangeable-lens cameras", library.Text, StringComparison.Ordinal);

        var focal = Assert.Single(insights, i => i.Title == "Favourite focal lengths");
        Assert.Equal(ReportSections.Focal, focal.Section);
        Assert.Equal("35mm", focal.Metric);
        Assert.Equal(InsightTone.Positive, focal.Tone);

        Assert.Equal("f/2.8", Assert.Single(insights, i => i.Title == "Typical aperture").Metric);
        Assert.Contains(insights, i => i.Section == ReportSections.Gear);
        Assert.DoesNotContain(insights, i => i.Title == "RAW + JPEG");
        Assert.All(insights, i => Assert.False(string.IsNullOrWhiteSpace(i.Text)));
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var profile = NikonProfile();

        var first = InsightGenerator.Generate(profile, null, TestData.Taxonomy);
        var second = InsightGenerator.Generate(profile, null, TestData.Taxonomy);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Raw_plus_jpeg_and_target_system_are_reported()
    {
        var profile = NikonProfile();
        profile = profile with
        {
            Library = profile.Library with { RawPlusJpeg = 3 },
            Target = new TargetSystem { BodyDisplay = "Nikon Z 6", Format = "full-frame", CropFactor = 1 },
        };

        var insights = InsightGenerator.Generate(profile, null, TestData.Taxonomy);

        Assert.Equal("3", Assert.Single(insights, i => i.Title == "RAW + JPEG").Metric);
        var target = Assert.Single(insights, i => i.Title == "Target system");
        Assert.Equal("Nikon Z 6", target.Metric);
        Assert.Contains("crop 1.0×", target.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommendation_insights_follow_the_ranking()
    {
        var profile = NikonProfile();
        var target = new TargetSystem { Format = "full-frame", CropFactor = 1 };
        Assert.DoesNotContain(InsightGenerator.Generate(profile, null, TestData.Taxonomy), i => i.Section == ReportSections.Recommendation);

        var set = new RecommendationSet
        {
            RecommenderId = "test",
            Preset = "balanced",
            Target = target,
            Ranked =
            [
                new LensRecommendation { Lens = Lens("35/1.2 \"Wide fast\""), Rank = 1, Score = 0.82, ProbabilityBest = 0.45 },
                new LensRecommendation { Lens = Lens("24/1.4 \"Wider\""), Rank = 2, Score = 0.8, ProbabilityBest = 0.4 },
            ],
        };

        var insights = InsightGenerator.Generate(profile, set, TestData.Taxonomy).Where(i => i.Section == ReportSections.Recommendation).ToList();

        Assert.Equal(new[] { "Best fit", "Close call" }, insights.Select(i => i.Title));
        Assert.Equal("Wide fast ranks first with a score of 82 / 100.", insights[0].Text);
        Assert.Equal("45%", insights[1].Metric);

        var confident = set with { Headline = "Clear winner.", Ranked = [set.Ranked[0] with { ProbabilityBest = 0.9 }] };
        var single = Assert.Single(InsightGenerator.Generate(profile, confident, TestData.Taxonomy), i => i.Section == ReportSections.Recommendation);
        Assert.Equal("Clear winner.", single.Text);
    }
}
