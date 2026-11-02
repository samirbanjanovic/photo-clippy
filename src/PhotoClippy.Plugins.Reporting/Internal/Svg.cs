using System.Text;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Minimal append-only SVG writer. Colors come from CSS classes so charts follow the theme.</summary>
internal sealed class Svg
{
    private readonly StringBuilder _sb;

    public Svg(StringBuilder sb, double width, double height, string label, string? cssClass = null)
    {
        _sb = sb;
        Width = width;
        Height = height;
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").N(width).Append(' ').N(height)
            .Append("\" class=\"chart");
        if (cssClass is not null)
        {
            sb.Append(' ').Append(cssClass);
        }

        sb.Append('"');
        if (cssClass == "mini")
        {
            sb.Append(" preserveAspectRatio=\"none\"");
        }

        sb.Append(" role=\"img\" aria-label=\"").AppendEsc(label).Append("\">");
    }

    public double Width { get; }

    public double Height { get; }

    public StringBuilder Raw => _sb;

    public Svg Rect(double x, double y, double w, double h, string cls, string? title = null, double? opacity = null, string? fill = null)
    {
        if (w <= 0 || h <= 0)
        {
            return this;
        }

        _sb.Append("<rect x=\"").N(x).Append("\" y=\"").N(y).Append("\" width=\"").N(Math.Max(w, 0.1)).Append("\" height=\"").N(Math.Max(h, 0.1)).Append('"');
        Class(cls);
        if (opacity is { } o)
        {
            _sb.Append(" fill-opacity=\"").Append(Math.Round(Math.Clamp(o, 0, 1), 2).ToString("0.##", Fmt.Inv)).Append('"');
        }

        if (fill is not null)
        {
            _sb.Append(" fill=\"").Append(fill).Append('"');
        }

        return CloseWithTitle("rect", title);
    }

    public Svg Line(double x1, double y1, double x2, double y2, string cls, string? title = null)
    {
        _sb.Append("<line x1=\"").N(x1).Append("\" y1=\"").N(y1).Append("\" x2=\"").N(x2).Append("\" y2=\"").N(y2).Append('"');
        Class(cls);
        return CloseWithTitle("line", title);
    }

    public Svg Circle(double cx, double cy, double r, string cls, string? title = null)
    {
        _sb.Append("<circle cx=\"").N(cx).Append("\" cy=\"").N(cy).Append("\" r=\"").N(r).Append('"');
        Class(cls);
        return CloseWithTitle("circle", title);
    }

    public Svg Path(string d, string cls, string? title = null)
    {
        _sb.Append("<path d=\"").Append(d).Append('"');
        Class(cls);
        return CloseWithTitle("path", title);
    }

    /// <summary>Text with anchor "start", "middle" or "end".</summary>
    public Svg Text(double x, double y, string? text, string cls, string anchor = "start", string? title = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        _sb.Append("<text x=\"").N(x).Append("\" y=\"").N(y).Append('"');
        Class(cls);
        if (anchor != "start")
        {
            _sb.Append(" text-anchor=\"").Append(anchor).Append('"');
        }

        _sb.Append('>');
        if (title is not null)
        {
            _sb.Append("<title>").AppendEsc(title).Append("</title>");
        }

        _sb.AppendEsc(text).Append("</text>");
        return this;
    }

    /// <summary>Places text inside [0, Width] by switching the anchor near the edges.</summary>
    public Svg TextClamped(double x, double y, string? text, string cls, double fontSize, string? title = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        var w = Fmt.TextWidth(text, fontSize);
        if (x - (w / 2) < 0)
        {
            return Text(Math.Max(x - 4, 0), y, text, cls, "start", title);
        }

        if (x + (w / 2) > Width)
        {
            return Text(Math.Min(x + 4, Width), y, text, cls, "end", title);
        }

        return Text(x, y, text, cls, "middle", title);
    }

    public Svg Group(string? cls = null, string? dataKey = null, string? title = null)
    {
        _sb.Append("<g");
        if (cls is not null)
        {
            Class(cls);
        }

        if (dataKey is not null)
        {
            _sb.Append(" data-k=\"").AppendEsc(dataKey).Append('"');
        }

        _sb.Append('>');
        if (title is not null)
        {
            _sb.Append("<title>").AppendEsc(title).Append("</title>");
        }

        return this;
    }

    public Svg EndGroup()
    {
        _sb.Append("</g>");
        return this;
    }

    public void End() => _sb.Append("</svg>");

    private void Class(string cls)
    {
        if (cls.Length > 0)
        {
            _sb.Append(" class=\"").Append(cls).Append('"');
        }
    }

    private Svg CloseWithTitle(string element, string? title)
    {
        if (title is null)
        {
            _sb.Append("/>");
        }
        else
        {
            _sb.Append("><title>").AppendEsc(title).Append("</title></").Append(element).Append('>');
        }

        return this;
    }
}

/// <summary>Builds SVG path data.</summary>
internal sealed class PathData
{
    private readonly StringBuilder _sb = new();

    public PathData M(double x, double y)
    {
        _sb.Append('M').N(x).Append(',').N(y);
        return this;
    }

    public PathData L(double x, double y)
    {
        _sb.Append('L').N(x).Append(',').N(y);
        return this;
    }

    public PathData Z()
    {
        _sb.Append('Z');
        return this;
    }

    public override string ToString() => _sb.ToString();
}
