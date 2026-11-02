using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Tests;

public sealed class LensSpecTests
{
    [Theory]
    [InlineData("24/1.4", 24, 24, 1.4, 1.4, "24/1.4")]
    [InlineData("14-24/2.8", 14, 24, 2.8, 2.8, "14-24/2.8")]
    [InlineData("24-70mm f/2.8", 24, 70, 2.8, 2.8, "24-70/2.8")]
    [InlineData("35mm f1.2", 35, 35, 1.2, 1.2, "35/1.2")]
    [InlineData(" 50 / 1.2 ", 50, 50, 1.2, 1.2, "50/1.2")]
    [InlineData("100-400/4.5-5.6", 100, 400, 4.5, 5.6, "100-400/4.5-5.6")]
    [InlineData("70-200 F/2.8", 70, 200, 2.8, 2.8, "70-200/2.8")]
    public void Parses_shorthand(string text, double fMin, double fMax, double aWide, double aTele, string id)
    {
        Assert.True(LensSpec.TryParse(text, out var spec, out var error), error);
        Assert.Equal(fMin, spec.FocalMinMm);
        Assert.Equal(fMax, spec.FocalMaxMm);
        Assert.Equal(aWide, spec.MaxApertureWide);
        Assert.Equal(aTele, spec.MaxApertureTele);
        Assert.Equal(id, spec.Id);
        Assert.Equal(LensSpecSource.User, spec.Source);
        Assert.Null(spec.WeightG);
        Assert.Null(spec.PriceUsd);
    }

    [Fact]
    public void Parses_optional_weight_price_and_label()
    {
        Assert.True(LensSpec.TryParse("35/1.2 1060g $2800 \"Z 35mm f/1.2 S\"", out var spec, out var error), error);
        Assert.Equal(1060, spec.WeightG);
        Assert.Equal(2800, spec.PriceUsd);
        Assert.Equal("Z 35mm f/1.2 S", spec.Name);
        Assert.Equal("35/1.2", spec.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nifty fifty")]
    [InlineData("24/0.2")]
    [InlineData("70-24/2.8")]
    [InlineData("35/1.2 heavy")]
    public void Rejects_invalid(string text)
    {
        Assert.False(LensSpec.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parses_list_with_quoted_commas_and_dedupes()
    {
        var list = LensSpec.ParseList("24/1.4, 14-24/2.8; 24-70/2.8, 35/1.2 \"Big, heavy\", 50/1.2, 24/1.4", out var errors);
        Assert.Empty(errors);
        Assert.Equal(["24/1.4", "14-24/2.8", "24-70/2.8", "35/1.2", "50/1.2"], list.Select(l => l.Id));
        Assert.Equal("Big, heavy", list[3].Name);
    }

    [Fact]
    public void Interpolates_zoom_aperture_in_log_focal_space()
    {
        Assert.True(LensSpec.TryParse("100-400/4.5-5.6", out var spec, out _));
        Assert.Equal(4.5, spec.MaxApertureAt(100), 3);
        Assert.Equal(5.6, spec.MaxApertureAt(400), 3);
        var mid = spec.MaxApertureAt(200);
        Assert.InRange(mid, 4.9, 5.1);
    }
}
