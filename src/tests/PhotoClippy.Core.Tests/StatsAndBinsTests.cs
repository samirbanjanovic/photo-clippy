using PhotoClippy.Contracts;
using PhotoClippy.Core.Profiling;

namespace PhotoClippy.Core.Tests;

public sealed class StatsTests
{
    [Fact]
    public void Percentile_interpolates_and_sorts_in_place()
    {
        var values = new List<double> { 4, 1, 3, 2 };
        Assert.Equal(2.5, Stats.Percentile(values, 0.5), 10);
        Assert.Equal(new double[] { 1, 2, 3, 4 }, values);
        Assert.Equal(1, Stats.Percentile(values, 0));
        Assert.Equal(4, Stats.Percentile(values, 1));
        Assert.Equal(5, Stats.Median([5]));
    }

    [Fact]
    public void Empty_inputs_give_nan()
    {
        Assert.True(double.IsNaN(Stats.Median([])));
        Assert.True(double.IsNaN(Stats.PercentileSorted([], 0.5)));
        Assert.True(double.IsNaN(Stats.WeightedPercentile([], 0.5)));
    }

    [Fact]
    public void Percentile_sorted_clamps_p()
    {
        double[] sorted = [10, 20];
        Assert.Equal(12.5, Stats.PercentileSorted(sorted, 0.25), 10);
        Assert.Equal(20, Stats.PercentileSorted(sorted, 2));
        Assert.Equal(10, Stats.PercentileSorted(sorted, -1));
    }

    [Fact]
    public void Weighted_percentile_respects_weights()
    {
        List<(double, double)> Items() => [(3, 2), (1, 1), (2, 1)];
        Assert.Equal(2, Stats.WeightedPercentile(Items(), 0.5));
        Assert.Equal(3, Stats.WeightedPercentile(Items(), 0.75));
        Assert.Equal(1, Stats.WeightedPercentile(Items(), 0));
    }
}

public sealed class BinSchemeTests
{
    [Fact]
    public void Linear_bins_label_and_clamp()
    {
        var bins = BinScheme.Linear(0, 1, 10, "0.0");
        Assert.Equal(10, bins.Count);
        Assert.Equal("0.0–0.1", bins.Labels[0]);
        Assert.Equal("0.9–1.0", bins.Labels[^1]);
        Assert.Equal(0, bins.IndexOf(-5));
        Assert.Equal(5, bins.IndexOf(0.55));
        Assert.Equal(9, bins.IndexOf(1.0));
        Assert.Equal(AxisScale.Linear, bins.Scale);
    }

    [Theory]
    [InlineData(35, "35mm")]
    [InlineData(36, "35mm")]
    [InlineData(83, "85mm")]
    [InlineData(1, "8mm")]
    [InlineData(5000, "800mm")]
    public void Focal_bins_use_log_centres(double focal, string label) => Assert.Equal(label, StandardBins.Focal.Labels[StandardBins.Focal.IndexOf(focal)]);

    [Theory]
    [InlineData(4.0, "f/4.0")]
    [InlineData(1.4, "f/1.4")]
    [InlineData(0.95, "f/0.95")]
    [InlineData(11, "f/11")]
    public void Aperture_bins_snap_to_series(double f, string label) => Assert.Equal(label, StandardBins.Aperture.Labels[StandardBins.Aperture.IndexOf(f)]);

    [Theory]
    [InlineData(3000, "3200")]
    [InlineData(100, "100")]
    [InlineData(0, "25")]
    public void Iso_bins_snap_to_series(double iso, string label) => Assert.Equal(label, StandardBins.Iso.Labels[StandardBins.Iso.IndexOf(iso)]);

    [Theory]
    [InlineData(1 / 50.0, "1/60")]
    [InlineData(2.2, "2s")]
    [InlineData(1 / 7000.0, "1/8000")]
    public void Shutter_bins_snap_to_full_stops(double seconds, string label) => Assert.Equal(label, StandardBins.Shutter.Labels[StandardBins.Shutter.IndexOf(seconds)]);

    [Fact]
    public void Thirds_bins_have_open_end_and_signed_labels()
    {
        var stops = StandardBins.StopsFromMax;
        Assert.Equal(13, stops.Count);
        Assert.Equal("4+", stops.Labels[^1]);
        Assert.Null(stops.Upper[^1]);
        Assert.Equal("1⅓", stops.Labels[4]);
        Assert.Equal(12, stops.IndexOf(10));

        var bias = StandardBins.ExposureBias;
        Assert.Equal(19, bias.Count);
        Assert.Equal("−3", bias.Labels[0]);
        Assert.Equal("0", bias.Labels[9]);
        Assert.Equal("+3", bias.Labels[^1]);
        Assert.Equal("−⅔", bias.Labels[bias.IndexOf(-0.7)]);
    }

    [Fact]
    public void Hour_bins_are_two_digit()
    {
        var hours = StandardBins.Hour;
        Assert.Equal(24, hours.Count);
        Assert.Equal("00", hours.Labels[0]);
        Assert.Equal("23", hours.Labels[^1]);
        Assert.Equal(13, hours.IndexOf(13.4));
    }

    [Theory]
    [InlineData(0, false, "0")]
    [InlineData(1, false, "⅓")]
    [InlineData(5, false, "1⅔")]
    [InlineData(-2, true, "−⅔")]
    [InlineData(3, true, "+1")]
    [InlineData(-4, false, "−1⅓")]
    public void Thirds_labels(int thirds, bool signed, string expected) => Assert.Equal(expected, BinScheme.ThirdsLabel(thirds, signed));

    [Theory]
    [InlineData(2, "2s")]
    [InlineData(1.5, "1.5s")]
    [InlineData(1 / 60.0, "1/60")]
    [InlineData(0.3, "1/3")]
    public void Formats_shutter(double seconds, string expected) => Assert.Equal(expected, BinScheme.FormatShutter(seconds));

    [Theory]
    [InlineData(1.4, "f/1.4")]
    [InlineData(2, "f/2.0")]
    [InlineData(1.25, "f/1.25")]
    [InlineData(11, "f/11")]
    public void Formats_aperture(double f, string expected) => Assert.Equal(expected, BinScheme.FormatAperture(f));

    [Fact]
    public void Formats_focal() => Assert.Equal("24mm", BinScheme.FormatFocal(23.6));
}

public sealed class FocalPeakFinderTests
{
    [Fact]
    public void Empty_and_invalid_input_yields_no_peaks()
    {
        var finder = new FocalPeakFinder();
        finder.Add(null);
        finder.Add(-1);
        finder.Add(double.NaN);
        finder.Add(double.PositiveInfinity);
        Assert.Empty(finder.Find());
    }

    [Fact]
    public void Two_primes_give_two_disjoint_peaks()
    {
        var finder = new FocalPeakFinder();
        for (var i = 0; i < 10; i++)
        {
            finder.Add(35);
            finder.Add(85);
        }

        var peaks = finder.Find();

        Assert.Equal(2, peaks.Count);
        Assert.Equal(new double[] { 35, 85 }, peaks.Select(p => p.Focal35Mm).Order());
        Assert.All(peaks, p => Assert.Equal(0.5, p.Share, 6));
        Assert.True(peaks.Sum(p => p.Share) <= 1 + 1e-9);
    }

    [Fact]
    public void Weights_drive_share_and_order()
    {
        var finder = new FocalPeakFinder();
        finder.Add(35, 3);
        finder.Add(85, 1);

        var peaks = finder.Find();

        Assert.Equal(35, peaks[0].Focal35Mm);
        Assert.Equal(0.75, peaks[0].Share, 6);
        Assert.Equal(0.25, peaks[1].Share, 6);
    }

    [Fact]
    public void Zoom_jitter_merges_into_one_peak_at_the_mode()
    {
        var finder = new FocalPeakFinder();
        for (var i = 0; i < 5; i++)
        {
            finder.Add(24);
        }

        for (var i = 0; i < 3; i++)
        {
            finder.Add(25);
        }

        finder.Add(26);
        finder.Add(26);

        var peak = Assert.Single(finder.Find());
        Assert.Equal(24, peak.Focal35Mm);
        Assert.Equal(1, peak.Share, 6);
        Assert.True(peak.LowerMm <= 24 && peak.UpperMm >= 26);
    }

    [Fact]
    public void Min_share_and_max_count_filter_peaks()
    {
        var finder = new FocalPeakFinder();
        for (var i = 0; i < 29; i++)
        {
            finder.Add(50);
        }

        finder.Add(400);
        Assert.Single(finder.Find(minShare: 0.05));
        Assert.Equal(2, finder.Find(minShare: 0.01).Count);

        var many = new FocalPeakFinder();
        foreach (var f in new double[] { 14, 35, 85, 200 })
        {
            many.Add(f);
        }

        Assert.Equal(2, many.Find(max: 2).Count);
    }

    [Fact]
    public void Describe_rounds_to_mm() => Assert.Equal("35mm", FocalPeakFinder.Describe(new FocalPeak(35.4, 0.5, 30, 40)));
}
