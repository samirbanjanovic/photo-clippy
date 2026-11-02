using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Lenses;

namespace PhotoClippy.Lenses.Tests;

public sealed class LensfunCatalogTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly string _state = Path.Combine(Path.GetTempPath(), "pc-lenses-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private LensfunCatalog FromFixtures() => new(new LensfunSettings { Directory = Fixtures }, _state, NullLogger.Instance);

    [Fact]
    public async Task Loads_a_local_folder_without_refresh()
    {
        using var catalog = FromFixtures();
        var info = await catalog.GetInfoAsync();
        Assert.True(info.Available);
        Assert.Equal(8, info.LensCount);
        Assert.Contains("Sigma", info.Makers);
        Assert.Contains("Fujifilm X", info.Mounts);
        Assert.Contains("CC BY-SA", info.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merges_duplicate_lenses_and_normalises_maker_case()
    {
        using var catalog = FromFixtures();
        var sigma = Assert.Single(await catalog.SearchAsync(new LensCatalogQuery { Maker = "sigma" }));
        Assert.Equal("Sigma", sigma.Maker);
        Assert.Equal(["Fujifilm X", "Leica L", "Sony E"], sigma.Mounts.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("23", "Fujifilm X", new[] { "XF 23mm f/1.4 R", "XF 16-55mm f/2.8 R LM WR", "18-50mm F2.8 DC DN | Contemporary 021" })]
    [InlineData("23mm sigma", null, new[] { "18-50mm F2.8 DC DN | Contemporary 021" })]
    [InlineData("f/1.4", "Fujifilm X", new[] { "XF 23mm f/1.4 R", "XF Mystery Prime" })]
    [InlineData("16-55", null, new[] { "XF 16-55mm f/2.8 R LM WR" })]
    [InlineData("17-50", "Fujifilm X", new[] { "18-50mm F2.8 DC DN | Contemporary 021" })]
    [InlineData("xf16-55", null, new[] { "XF 16-55mm f/2.8 R LM WR" })]
    [InlineData("nikkor 2.8", null, new[] { "Nikkor Z 24-70mm f/2.8 S" })]
    [InlineData("fisheye", null, new[] { "XF8mmF3.5 R WR" })]
    public async Task Search_filters_by_words_focal_aperture_and_mount(string text, string? mount, string[] expected)
    {
        using var catalog = FromFixtures();
        var found = await catalog.SearchAsync(new LensCatalogQuery { Text = text, Mount = mount });
        Assert.Equal(expected.Order(StringComparer.Ordinal), found.Select(l => l.Model).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, "Fujifilm X-T5", "Fujifilm X")]
    [InlineData("Fujifilm", "X-T5", "Fujifilm X")]
    [InlineData(null, "Nikon Z 6", "Nikon Z")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6", "Nikon Z")]
    [InlineData(null, "Canon EOS 5DS", "Canon EF")]
    [InlineData(null, "Leica M11", null)]
    public async Task Resolves_a_body_to_its_mount(string? maker, string model, string? mount)
    {
        using var catalog = FromFixtures();
        Assert.Equal(mount, await catalog.MountForBodyAsync(maker, model));
    }

    [Fact]
    public async Task Shorthand_round_trips_through_the_candidate_parser()
    {
        using var catalog = FromFixtures();
        foreach (var lens in await catalog.SearchAsync(new LensCatalogQuery { Limit = 100 }))
        {
            Assert.True(LensSpec.TryParse(lens.ToShorthand(), out var spec, out var error), error);
            Assert.Equal(lens.FocalMinMm, spec.FocalMinMm);
            Assert.Equal(lens.FocalMaxMm, spec.FocalMaxMm);
            Assert.Equal(lens.MaxApertureWide, spec.MaxApertureWide);
            Assert.Equal(lens.DisplayName.Replace('"', '\''), spec.Name);
        }
    }

    [Fact]
    public async Task Refresh_downloads_listed_files_and_caches_them()
    {
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var handler = new StubHandler(uri =>
        {
            requests.Enqueue(uri.AbsolutePath);
            if (uri.Host == "index.test")
            {
                return """[{"name":"mil-fujifilm.xml"},{"name":"mil-sigma.xml"},{"name":"generic.xml"},{"name":"timestamp.txt"},{"name":"README.md"}]""";
            }

            return File.ReadAllText(Path.Combine(Fixtures, Path.GetFileName(uri.AbsolutePath)));
        });
        var settings = new LensfunSettings { IndexUrl = "https://index.test/db", BaseUrl = "https://files.test/db/" };

        using (var empty = new LensfunCatalog(settings, _state, NullLogger.Instance, () => new HttpClient(handler)))
        {
            Assert.False((await empty.GetInfoAsync()).Available);
            Assert.True((await empty.CheckHealthAsync()).Healthy);
            var info = await empty.RefreshAsync();
            Assert.Equal(5, info.LensCount);
        }

        Assert.Equal(["/db", "/db/mil-fujifilm.xml", "/db/mil-sigma.xml"], requests.Order(StringComparer.Ordinal));

        requests.Clear();
        using var reopened = new LensfunCatalog(settings, _state, NullLogger.Instance, () => new HttpClient(handler));
        Assert.Equal(5, (await reopened.GetInfoAsync()).LensCount);
        Assert.Equal("Fujifilm X", await reopened.MountForBodyAsync(null, "Fujifilm X-T5"));
        Assert.Empty(requests);
    }

    private sealed class StubHandler(Func<Uri, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request.RequestUri!), Encoding.UTF8) });
    }
}
