using System.Globalization;
using System.Text;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Reporting;

namespace PhotoClippy.Reporting.Tests;

public sealed class CsvReportTests
{
    private static readonly string[] Suffixes = ["lenses", "bodies", "distributions", "recommendations"];

    [Fact]
    public async Task Writes_four_tables_with_one_row_per_item()
    {
        var model = ReportFixture.Full;
        var tables = await RenderAsync(model);

        Assert.Equal(Suffixes, tables.Keys);
        Assert.Equal(model.Profile.Lenses.Count, tables["lenses"].Count - 1);
        Assert.Equal(model.Profile.Bodies.Count, tables["bodies"].Count - 1);
        Assert.Equal(model.Recommendation!.Ranked.Count, tables["recommendations"].Count - 1);
        Assert.True(tables["distributions"].Count > 50);

        foreach (var (name, rows) in tables)
        {
            var width = rows[0].Count;
            Assert.All(rows, r => Assert.Equal(width, r.Count));
            Assert.DoesNotContain(rows.SelectMany(r => r), v => v is "NaN" or "Infinity" or "-Infinity");
            Assert.True(rows[0].All(h => h.Length > 0), $"{name}: empty header");
        }
    }

    [Fact]
    public async Task Text_with_commas_quotes_and_markup_survives_rfc4180_quoting()
    {
        var tables = await RenderAsync(ReportFixture.Full);
        var lenses = tables["lenses"];
        var display = lenses[0].IndexOf("display");

        Assert.Contains(lenses.Skip(1), r => r[display] == ReportFixture.HostileLensName);
    }

    [Fact]
    public async Task Recommendations_are_in_rank_order_with_criterion_columns()
    {
        var model = ReportFixture.Full;
        var rows = (await RenderAsync(model))["recommendations"];
        var header = rows[0];
        var rank = header.IndexOf("rank");
        var lensId = header.IndexOf("lens_id");

        Assert.Equal(Enumerable.Range(1, rows.Count - 1).Select(i => i.ToString(CultureInfo.InvariantCulture)), rows.Skip(1).Select(r => r[rank]));
        Assert.Equal(model.Recommendation!.Ranked.OrderBy(r => r.Rank).Select(r => r.Lens.Id), rows.Skip(1).Select(r => r[lensId]));
        Assert.Contains(header, h => h.StartsWith("criterion_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Numbers_use_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        // The repo runs with InvariantGlobalization, so simulate a comma-decimal locale instead of loading de-DE.
        var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
        commaDecimal.NumberFormat.NumberGroupSeparator = ".";
        CultureInfo.CurrentCulture = commaDecimal;
        try
        {
            var rows = (await RenderAsync(ReportFixture.Full))["lenses"];
            var share = rows[0].IndexOf("share");

            Assert.All(rows.Skip(1), r => Assert.True(double.TryParse(r[share], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1, r[share]));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Empty_model_writes_header_only_tables()
    {
        var tables = await RenderAsync(ReportFixture.Empty());

        Assert.Equal(Suffixes, tables.Keys);
        Assert.Single(tables["lenses"]);
        Assert.Single(tables["bodies"]);
        Assert.Single(tables["recommendations"]);
    }

    private static async Task<Dictionary<string, List<List<string>>>> RenderAsync(ReportModel model)
    {
        var dir = ReportFixture.NewTempDir();
        try
        {
            var result = await new CsvReportRenderer().RenderAsync(model, new RenderTarget { OutputDirectory = dir, BaseName = "r" });
            Assert.Equal(Suffixes.Select(s => $"r-{s}.csv"), result.Files.Select(Path.GetFileName));

            var tables = new Dictionary<string, List<List<string>>>();
            foreach (var (suffix, file) in Suffixes.Zip(result.Files))
            {
                var bytes = await File.ReadAllBytesAsync(file);
                Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "CSV must be written without a BOM");
                tables[suffix] = Parse(Encoding.UTF8.GetString(bytes));
            }

            return tables;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, embedded commas and newlines.</summary>
    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
