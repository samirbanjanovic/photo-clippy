using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Reporting;

namespace PhotoClippy.Reporting.Tests;

public sealed class JsonReportTests
{
    [Fact]
    public async Task Full_model_round_trips_through_contract_json()
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            var model = ReportFixture.Full;
            var result = await new JsonReportRenderer().RenderAsync(model, new RenderTarget { OutputDirectory = dir });

            var file = Assert.Single(result.Files);
            Assert.Equal("photo-report.json", Path.GetFileName(file));
            Assert.Single(Directory.GetFiles(dir));

            var bytes = await File.ReadAllBytesAsync(file);
            Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "JSON must be written without a BOM");

            var back = JsonSerializer.Deserialize<ReportModel>(bytes, ContractJson.Options);
            Assert.NotNull(back);
            Assert.Equal(model.Title, back.Title);
            Assert.Equal(model.Run.RunId, back.Run.RunId);
            Assert.Equal(model.Profile.Library.Captures, back.Profile.Library.Captures);
            Assert.Equal(model.Profile.Lenses.Select(l => l.Display), back.Profile.Lenses.Select(l => l.Display));
            Assert.Equal(model.Profile.Distributions.Keys.Order(), back.Profile.Distributions.Keys.Order());
            Assert.Equal(model.Recommendation!.Ranked.Select(r => (r.Rank, r.Lens.Id)), back.Recommendation!.Ranked.Select(r => (r.Rank, r.Lens.Id)));
            Assert.Equal(model.Insights.Count, back.Insights.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_model_is_valid_json()
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            var result = await new JsonReportRenderer().RenderAsync(ReportFixture.Empty(), new RenderTarget { OutputDirectory = dir, BaseName = "empty" });

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(result.Files)));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.TryGetProperty("profile", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_render_leaves_no_partial_file()
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JsonReportRenderer().RenderAsync(ReportFixture.Full, new RenderTarget { OutputDirectory = dir }, cts.Token));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
