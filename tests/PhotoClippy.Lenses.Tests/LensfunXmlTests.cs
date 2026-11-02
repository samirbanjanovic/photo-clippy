using PhotoClippy.Plugins.Lenses;

namespace PhotoClippy.Lenses.Tests;

public sealed class LensfunXmlTests
{
    private static (List<PhotoClippy.Contracts.CatalogLens> Lenses, List<LensfunCamera> Cameras) Load(string file)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return LensfunXml.Parse(stream);
    }

    [Fact]
    public void Prefers_english_display_names_and_reads_spec()
    {
        var (lenses, cameras) = Load("mil-fujifilm.xml");
        var zoom = Assert.Single(lenses, l => l.FocalMaxMm == 55);
        Assert.Equal("Fujifilm", zoom.Maker);
        Assert.Equal("XF 16-55mm f/2.8 R LM WR", zoom.Model);
        Assert.Equal("Fujifilm XF 16-55mm f/2.8 R LM WR", zoom.DisplayName);
        Assert.Equal(16, zoom.FocalMinMm);
        Assert.Equal(2.8, zoom.MaxApertureWide);
        Assert.Equal(["Fujifilm X"], zoom.Mounts);
        Assert.Null(zoom.Type);

        Assert.Equal("fisheye", Assert.Single(lenses, l => l.FocalMinMm == 8).Type);
        Assert.Equal("Fujifilm X", Assert.Single(cameras).Mount);
    }

    [Fact]
    public void Falls_back_to_focal_and_aperture_elements()
    {
        var mystery = Assert.Single(Load("mil-fujifilm.xml").Lenses, l => l.Model == "XF Mystery Prime");
        Assert.Equal(33, mystery.FocalMinMm);
        Assert.Equal(33, mystery.FocalMaxMm);
        Assert.Equal(1.2, mystery.MaxApertureWide);
    }

    [Fact]
    public void Skips_extenders_combinations_and_generic_mounts()
    {
        var canon = Load("slr-canon.xml").Lenses;
        Assert.Equal(2, canon.Count);
        Assert.DoesNotContain(canon, l => l.Model.Contains("xtender", StringComparison.OrdinalIgnoreCase) || l.Model.Contains('+', StringComparison.Ordinal));
        Assert.Empty(Load("generic.xml").Lenses);
    }

    [Fact]
    public void Ids_are_stable_slugs_without_repeating_the_maker()
    {
        var canon = Load("slr-canon.xml").Lenses;
        Assert.Contains(canon, l => l.Id == "canon/ef-16-35mm-f-2-8l-usm");
        Assert.Equal("ef-16-35mm-f-2-8l-usm", LensfunXml.Slug("EF 16-35mm f/2.8L USM"));
    }
}
