using System.Text;
using System.Text.RegularExpressions;

namespace PhotoClippy.Core.Normalization;

/// <summary>Display names and stable keys for bodies and lenses, derived purely from EXIF strings.</summary>
public static partial class CameraNames
{
    private static readonly (string Prefix, string Display)[] Makes =
    [
        ("NIKON", "Nikon"), ("CANON", "Canon"), ("SONY", "Sony"), ("FUJIFILM", "Fujifilm"), ("FUJI PHOTO", "Fujifilm"),
        ("OM DIGITAL", "OM System"), ("OLYMPUS", "Olympus"), ("PANASONIC", "Panasonic"), ("LEICA", "Leica"),
        ("RICOH", "Ricoh"), ("PENTAX", "Pentax"), ("ASAHI", "Pentax"), ("SAMSUNG", "Samsung"), ("APPLE", "Apple"),
        ("GOOGLE", "Google"), ("HASSELBLAD", "Hasselblad"), ("SIGMA", "Sigma"), ("DJI", "DJI"), ("GOPRO", "GoPro"),
        ("EASTMAN KODAK", "Kodak"), ("KODAK", "Kodak"), ("KONICA MINOLTA", "Konica Minolta"), ("MINOLTA", "Minolta"),
        ("PHASE ONE", "Phase One"), ("HUAWEI", "Huawei"), ("XIAOMI", "Xiaomi"), ("ONEPLUS", "OnePlus"),
        ("MOTOROLA", "Motorola"), ("EPSON", "Epson"), ("HMD GLOBAL", "Nokia"), ("NOKIA", "Nokia"), ("OPPO", "OPPO"),
        ("VIVO", "vivo"), ("ZEISS", "Zeiss"), ("INSTA360", "Insta360"), ("ESSENTIAL", "Essential"), ("NOTHING", "Nothing"),
        ("FAIRPHONE", "Fairphone"), ("HONOR", "Honor"), ("REALME", "realme"), ("LGE", "LG"), ("LG ELECTRONICS", "LG"),
        ("HTC", "HTC"), ("ASUS", "ASUS"), ("ZTE", "ZTE"), ("PLUSTEK", "Plustek"), ("PARROT", "Parrot"), ("SKYDIO", "Skydio"),
    ];

    private static readonly string[] CorporateSuffixes =
    [
        " CORPORATION", " CORP.", " CORP", " CO., LTD.", " CO.,LTD.", " CO., LTD", " CO.,LTD", " COMPANY, LTD.",
        " COMPANY", " IMAGING", " INC.", " INC", " LTD.", " LTD", " AG", " GMBH", " ELECTRONICS",
    ];

    public static string? NormalizeMake(string? make)
    {
        var clean = Clean(make);
        if (clean is null)
        {
            return null;
        }

        foreach (var (prefix, display) in Makes)
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        var upper = clean.ToUpperInvariant();
        foreach (var suffix in CorporateSuffixes)
        {
            if (upper.EndsWith(suffix, StringComparison.Ordinal))
            {
                clean = clean[..^suffix.Length].TrimEnd(' ', ',', '.');
                upper = clean.ToUpperInvariant();
            }
        }

        return clean.Length == 0 ? null : clean == upper && clean.Length > 3 ? TitleCase(clean) : clean;
    }

    /// <summary>Model without a repeated make prefix; Nikon "_2"/"_3" suffixes become " II"/" III".</summary>
    public static string? NormalizeModel(string? rawMake, string? model)
    {
        var clean = Clean(model);
        if (clean is null)
        {
            return null;
        }

        var make = NormalizeMake(rawMake);
        foreach (var prefix in new[] { Clean(rawMake), make, Clean(rawMake)?.Split(' ')[0] })
        {
            if (!string.IsNullOrEmpty(prefix) && clean.Length > prefix.Length + 1 &&
                clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && clean[prefix.Length] == ' ')
            {
                clean = clean[(prefix.Length + 1)..].TrimStart();
                break;
            }
        }

        clean = NikonMarkSuffix().Replace(clean, m => m.Groups[1].Value switch { "2" => " II", "3" => " III", _ => m.Value });
        return clean;
    }

    public static string? BodyDisplay(string? rawMake, string? model)
    {
        var make = NormalizeMake(rawMake);
        var normalizedModel = NormalizeModel(rawMake, model);
        return (make, normalizedModel) switch
        {
            (null, null) => null,
            (null, { } m) => m,
            ({ } mk, null) => mk,
            ({ } mk, { } m) => $"{mk} {m}",
        };
    }

    /// <summary>Lens display name or null when the string is missing or a placeholder.</summary>
    public static string? LensDisplay(string? lensModel)
    {
        var clean = Clean(lensModel);
        if (clean is null || PlaceholderLens().IsMatch(clean))
        {
            return null;
        }

        return clean;
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        var dash = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '/' or '+')
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = MultiSpace().Replace(value.Replace('\0', ' '), " ").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string TitleCase(string upper)
    {
        var chars = upper.ToLowerInvariant().ToCharArray();
        var start = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (start && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
            }

            start = chars[i] is ' ' or '-';
        }

        return new string(chars);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"_(\d)$")]
    private static partial Regex NikonMarkSuffix();

    [GeneratedRegex(@"^(?:[-\s0.]*|.*\b0(?:\.0)?\s*mm\s*f/?0(?:\.0)?\b.*|unknown.*|n/?a|none|manual lens.*|\(\d+\))$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderLens();
}
