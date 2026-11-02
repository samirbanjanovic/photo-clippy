using System.Globalization;
using System.Text;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>RFC 4180 writer: CRLF line endings, fields quoted when they contain a comma, quote, CR or LF, invariant culture.</summary>
internal sealed class CsvTable
{
    private readonly StringBuilder _sb = new();
    private readonly int _columns;

    public CsvTable(IReadOnlyList<string> header)
    {
        _columns = header.Count;
        Write(header);
    }

    public int DataRows { get; private set; }

    public void Row(IReadOnlyList<string?> fields)
    {
        if (fields.Count != _columns)
        {
            throw new ArgumentException($"Expected {_columns} fields but got {fields.Count}.", nameof(fields));
        }

        Write(fields);
        DataRows++;
    }

    public override string ToString() => _sb.ToString();

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string Num(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    public static string Num(double? value) => value is { } v ? Num(v) : string.Empty;

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Date(DateTime? value) => value is { } d ? d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    private void Write(IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                _sb.Append(',');
            }

            _sb.Append(Escape(fields[i]));
        }

        _sb.Append("\r\n");
    }
}
