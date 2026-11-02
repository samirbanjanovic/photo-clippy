using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Lenses;

/// <summary>A camera body from the Lensfun database: every maker/model spelling plus its mount.</summary>
internal sealed record LensfunCamera(IReadOnlyList<string> Makers, IReadOnlyList<string> Models, string Mount);

/// <summary>Parses Lensfun database XML (<c>&lt;lensdatabase&gt;</c>) into catalog lenses and camera → mount entries.</summary>
internal static class LensfunXml
{
    private static readonly string[] SkipWords = ["extender", "converter", "teleconverter", " + ", "adapter"];

    public static (List<CatalogLens> Lenses, List<LensfunCamera> Cameras) Parse(Stream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var reader = XmlReader.Create(xml, settings);
        return Parse(XDocument.Load(reader));
    }

    public static (List<CatalogLens> Lenses, List<LensfunCamera> Cameras) Parse(XDocument document)
    {
        var lenses = new List<CatalogLens>();
        var cameras = new List<LensfunCamera>();
        var root = document.Root;
        if (root is null)
        {
            return (lenses, cameras);
        }

        foreach (var element in root.Elements("lens"))
        {
            if (ParseLens(element) is { } lens)
            {
                lenses.Add(lens);
            }
        }

        foreach (var element in root.Elements("camera"))
        {
            var mount = Text(element.Element("mount"));
            var models = Variants(element, "model");
            if (mount is not null && models.Count > 0)
            {
                cameras.Add(new LensfunCamera(Variants(element, "maker"), models, mount));
            }
        }

        return (lenses, cameras);
    }

    internal static CatalogLens? ParseLens(XElement element)
    {
        var maker = Preferred(element, "maker");
        var model = Text(element.Elements("model").FirstOrDefault(e => e.Attribute("lang") is null)) ?? Preferred(element, "model");
        var display = Preferred(element, "model");
        if (maker is null || model is null || display is null)
        {
            return null;
        }

        if (SkipWords.Any(w => model.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var mounts = element.Elements("mount").Select(Text).OfType<string>()
            .Where(m => !string.Equals(m, "Generic", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mounts.Count == 0)
        {
            return null;
        }

        double? focalMin = null, focalMax = null, apertureWide = null, apertureTele = null;
        if (LensNameParser.TryParse(model, out var spec) || LensNameParser.TryParse(display, out spec))
        {
            focalMin = spec.FocalMin;
            focalMax = spec.FocalMax;
            apertureWide = spec.ApertureWide;
            apertureTele = spec.ApertureTele;
        }

        if (element.Element("focal") is { } focal)
        {
            var value = Number(focal, "value");
            focalMin ??= Number(focal, "min") ?? value;
            focalMax ??= Number(focal, "max") ?? value ?? focalMin;
        }

        if (apertureWide is null && element.Element("aperture") is { } aperture && Number(aperture, "min") is { } widest)
        {
            apertureWide = widest;
            apertureTele = widest;
        }

        if (focalMin is not { } fmin || focalMax is not { } fmax || apertureWide is not { } awide || fmin <= 0)
        {
            return null;
        }

        var type = Text(element.Element("type"));
        return new CatalogLens
        {
            Id = Slug(maker) + "/" + Slug(StripMaker(model, maker)),
            Maker = maker,
            Model = StripMaker(display, maker),
            Mounts = mounts,
            FocalMinMm = fmin,
            FocalMaxMm = Math.Max(fmin, fmax),
            MaxApertureWide = awide,
            MaxApertureTele = Math.Max(awide, apertureTele ?? awide),
            Type = type is null || string.Equals(type, "rectilinear", StringComparison.OrdinalIgnoreCase) ? null : type,
        };
    }

    private static string StripMaker(string model, string maker) =>
        model.Length > maker.Length + 1 && model.StartsWith(maker + " ", StringComparison.OrdinalIgnoreCase)
            ? model[(maker.Length + 1)..].Trim()
            : model;

    /// <summary>English variant when present, else the untagged value.</summary>
    private static string? Preferred(XElement element, string name)
    {
        var all = element.Elements(name).ToList();
        return Text(all.FirstOrDefault(e => (string?)e.Attribute("lang") == "en"))
            ?? Text(all.FirstOrDefault(e => e.Attribute("lang") is null))
            ?? Text(all.FirstOrDefault());
    }

    private static List<string> Variants(XElement element, string name) =>
        [.. element.Elements(name).Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double? Number(XElement element, string attribute) =>
        double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    internal static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
