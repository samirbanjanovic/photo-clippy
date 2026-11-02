using PhotoClippy.Plugins.Lenses;

namespace PhotoClippy.Lenses.Tests;

public sealed class LensNameParserTests
{
    [Theory]
    [InlineData("XF18-55mmF2.8-4 R LM OIS", 18, 55, 2.8, 4.0)]
    [InlineData("Canon EF 16-35mm f/2.8L USM", 16, 35, 2.8, 2.8)]
    [InlineData("Sigma 18-50mm F2.8 DC DN | Contemporary 021", 18, 50, 2.8, 2.8)]
    [InlineData("Canon FDn 50mm 1:1.4", 50, 50, 1.4, 1.4)]
    [InlineData("XF16mmF1.4 R WR", 16, 16, 1.4, 1.4)]
    [InlineData("Nikkor Z 600mm f/6.3 VR S", 600, 600, 6.3, 6.3)]
    [InlineData("Viltrox AF 23mm F1.4 XF/Z/L", 23, 23, 1.4, 1.4)]
    [InlineData("100-400mm f/4.5-5.6", 100, 400, 4.5, 5.6)]
    public void Parses_focal_and_aperture(string name, double min, double max, double wide, double tele)
    {
        Assert.True(LensNameParser.TryParse(name, out var spec));
        Assert.Equal(min, spec.FocalMin);
        Assert.Equal(max, spec.FocalMax);
        Assert.Equal(wide, spec.ApertureWide);
        Assert.Equal(tele, spec.ApertureTele ?? spec.ApertureWide);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_names_without_focal_length(string? name) =>
        Assert.False(LensNameParser.TryParse(name, out _));
}
