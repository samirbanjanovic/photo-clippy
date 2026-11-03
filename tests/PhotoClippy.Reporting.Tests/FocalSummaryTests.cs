using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Reporting;

namespace PhotoClippy.Reporting.Tests;

public sealed class FocalSummaryTests
{
    [Fact]
    public void Median_and_dominant_peak_tell_distinct_stories_with_consistent_precision()
    {
        var profile = Profile() with
        {
            Signals = new ShootingSignals { MedianFocal35 = 49.5 },
            FocalPeaks = [new(62, 0.22, 55, 70), new(24, 0.27, 22, 26), new(35, 0.17, 30, 40)],
        };

        Assert.Equal("49.5 mm", FocalSummary.Median(profile));
        var dominant = FocalSummary.Dominant(profile);
        Assert.Equal("24 mm", dominant.Value);
        Assert.Equal("Dominant focal peak: 24 mm (27% of focal-length samples)", dominant.Caption);
    }

    [Fact]
    public void Equal_peak_shares_choose_the_lower_focal_length()
    {
        var profile = Profile() with { FocalPeaks = [new(50, 0.5, 45, 55), new(24, 0.5, 22, 26)] };

        Assert.Equal("24 mm", FocalSummary.Dominant(profile).Value);
    }

    [Fact]
    public void Missing_peaks_use_a_clearly_labelled_histogram_range()
    {
        var profile = Profile() with
        {
            Distributions = new Dictionary<string, Distribution>
            {
                [DistributionIds.Focal35] = new()
                {
                    Id = DistributionIds.Focal35,
                    Title = "Focal length",
                    Bins =
                    [
                        new() { Label = "wide", Lower = 20, Upper = 28, Count = 75 },
                        new() { Label = "normal", Lower = 40, Upper = 60, Count = 25 },
                        new() { Label = "invalid", Count = double.NaN },
                        new() { Label = "empty", Count = 0 },
                    ],
                },
            },
        };

        var dominant = FocalSummary.Dominant(profile);
        Assert.Equal("20–28 mm", dominant.Value);
        Assert.Equal("Busiest focal range: 20–28 mm (75% of focal-length samples; peak unavailable)", dominant.Caption);
    }

    [Fact]
    public void A_median_alone_is_never_presented_as_a_peak()
    {
        var profile = Profile() with { Signals = new ShootingSignals { MedianFocal35 = 49.5 } };

        Assert.Equal("49.5 mm", FocalSummary.Median(profile));
        Assert.Null(FocalSummary.Dominant(profile).Value);
        Assert.Equal("Dominant focal peak: unavailable", FocalSummary.Dominant(profile).Caption);
    }

    [Fact]
    public void Empty_data_is_explicitly_unavailable()
    {
        Assert.Equal("–", FocalSummary.Median(Profile()));
        Assert.Null(FocalSummary.Dominant(Profile()).Value);
        Assert.Equal("Dominant focal peak: unavailable", FocalSummary.Dominant(Profile()).Caption);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-24)]
    public void Invalid_focal_lengths_are_not_displayed(double focal)
    {
        var profile = Profile() with
        {
            Signals = new ShootingSignals { MedianFocal35 = focal },
            FocalPeaks = [new(focal, 1, 0, 100)],
        };

        Assert.Equal("–", FocalSummary.Median(profile));
        Assert.Null(FocalSummary.Dominant(profile).Value);
    }

    private static ShootingProfile Profile() => new() { Library = new LibrarySummary() };
}
