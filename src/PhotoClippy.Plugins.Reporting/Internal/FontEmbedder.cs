using System.Text;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Embeds local .woff2 files as base64 @font-face rules so the report never fetches fonts.</summary>
internal static class FontEmbedder
{
    private const long MaxFileBytes = 600 * 1024;
    private const long MaxTotalBytes = 1200 * 1024;

    public sealed record Result(string Css, IReadOnlyList<string> Families);

    public static Result Embed(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new Result(string.Empty, []);
        }

        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return new Result(string.Empty, []);
            }

            files = Directory.GetFiles(directory, "*.woff2", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Result(string.Empty, []);
        }

        Array.Sort(files, StringComparer.Ordinal);
        var sb = new StringBuilder();
        var families = new List<string>();
        long total = 0;
        foreach (var file in files)
        {
            byte[] bytes;
            try
            {
                var info = new FileInfo(file);
                if (info.Length is 0 or > MaxFileBytes || total + info.Length > MaxTotalBytes)
                {
                    continue;
                }

                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            total += bytes.Length;
            var (family, weight, italic) = Describe(Path.GetFileNameWithoutExtension(file));
            if (!families.Contains(family, StringComparer.Ordinal))
            {
                families.Add(family);
            }

            sb.Append("@font-face{font-family:\"").Append(family).Append("\";font-style:")
                .Append(italic ? "italic" : "normal").Append(";font-weight:").Append(weight)
                .Append(";font-display:swap;src:url(data:font/woff2;base64,")
                .Append(Convert.ToBase64String(bytes)).Append(") format(\"woff2\")}");
        }

        return new Result(sb.ToString(), families);
    }

    internal static (string Family, string Weight, bool Italic) Describe(string name)
    {
        var parts = name.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        var family = new StringBuilder();
        var weight = "400";
        var italic = false;
        var variable = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            var lower = token.ToLowerInvariant();
            if (lower.Length > 8 && lower.EndsWith("variable", StringComparison.Ordinal))
            {
                variable = true;
                token = token[..^8];
                lower = lower[..^8];
            }

            var w = WeightOf(lower);
            if (lower.Contains("italic", StringComparison.Ordinal))
            {
                italic = true;
                w ??= WeightOf(lower.Replace("italic", string.Empty, StringComparison.Ordinal));
            }

            if (lower is "variable" or "var" or "vf" || lower.StartsWith("opsz", StringComparison.Ordinal) || lower.StartsWith("wght", StringComparison.Ordinal))
            {
                variable = true;
                continue;
            }

            if (w is not null)
            {
                weight = w;
                continue;
            }

            if (italic && lower.Contains("italic", StringComparison.Ordinal))
            {
                continue;
            }

            if (i > 0 && family.Length > 0 && (lower is "display" or "text" or "latin" or "web" || IsDigits(lower)))
            {
                continue;
            }

            if (family.Length > 0)
            {
                family.Append(' ');
            }

            foreach (var ch in token)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ')
                {
                    family.Append(ch);
                }
            }
        }

        var f = family.Length == 0 ? "Inter" : family.ToString();
        if (f.Contains("Inter", StringComparison.OrdinalIgnoreCase) && f.Length <= 8)
        {
            f = "Inter";
        }

        return (f, variable ? "100 900" : weight, italic);
    }

    private static bool IsDigits(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static string? WeightOf(string token) => token switch
    {
        "thin" or "hairline" or "100" => "100",
        "extralight" or "ultralight" or "200" => "200",
        "light" or "300" => "300",
        "regular" or "normal" or "book" or "roman" or "400" => "400",
        "medium" or "500" => "500",
        "semibold" or "demibold" or "600" => "600",
        "bold" or "700" => "700",
        "extrabold" or "ultrabold" or "800" => "800",
        "black" or "heavy" or "900" => "900",
        _ => null,
    };
}
