using System.Globalization;

namespace PhotoClippy.Contracts;

/// <summary>One lens in a catalog, in native focal lengths.</summary>
public sealed record CatalogLens
{
    /// <summary>Stable id within the catalog, e.g. <c>fujifilm/xf-16-55mm-f-2-8-r-lm-wr</c>.</summary>
    public required string Id { get; init; }
    public required string Maker { get; init; }
    /// <summary>Model name without the maker, e.g. <c>XF 16-55mm f/2.8 R LM WR</c>.</summary>
    public required string Model { get; init; }
    /// <summary>Mounts the lens is made for, e.g. <c>Fujifilm X</c>, <c>Sony E</c>.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];
    public required double FocalMinMm { get; init; }
    public required double FocalMaxMm { get; init; }
    public required double MaxApertureWide { get; init; }
    public required double MaxApertureTele { get; init; }
    /// <summary>Projection when not rectilinear, e.g. <c>fisheye</c>.</summary>
    public string? Type { get; init; }
    public double? WeightG { get; init; }
    public double? PriceUsd { get; init; }

    /// <summary>Maker and model, e.g. <c>Fujifilm XF 16-55mm f/2.8 R LM WR</c>.</summary>
    public string DisplayName => Model.StartsWith(Maker, StringComparison.OrdinalIgnoreCase) ? Model : $"{Maker} {Model}";

    /// <summary>Candidate shorthand understood by <see cref="LensSpec.TryParse"/>, e.g. <c>16-55/2.8 "Fujifilm XF 16-55mm f/2.8 R LM WR"</c>.</summary>
    public string ToShorthand()
    {
        var text = LensSpec.Shorthand(FocalMinMm, FocalMaxMm, MaxApertureWide, MaxApertureTele);
        if (WeightG is { } g)
        {
            text += " " + g.ToString("0", CultureInfo.InvariantCulture) + "g";
        }

        if (PriceUsd is { } p)
        {
            text += " $" + p.ToString("0", CultureInfo.InvariantCulture);
        }

        var label = DisplayName.Replace('"', '\'');
        return $"{text} \"{label}\"";
    }
}

public sealed record LensCatalogQuery
{
    /// <summary>Free text matched against maker and model; every word must match. Spec-like words (<c>35mm</c>, <c>f/1.4</c>) match the focal range or aperture.</summary>
    public string? Text { get; init; }
    public string? Maker { get; init; }
    public string? Mount { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed record LensCatalogInfo
{
    /// <summary>False until the catalog data has been downloaded or loaded.</summary>
    public required bool Available { get; init; }
    public int LensCount { get; init; }
    public IReadOnlyList<string> Makers { get; init; } = [];
    public IReadOnlyList<string> Mounts { get; init; } = [];
    public DateTimeOffset? UpdatedUtc { get; init; }
    /// <summary>Where the data comes from (URL or path).</summary>
    public string? Source { get; init; }
    /// <summary>License and attribution text to show with the data.</summary>
    public string? Attribution { get; init; }
}

/// <summary>
/// Searchable lens database used to pick candidate lenses by maker and model instead of typing specs.
/// Implementations must not touch the network except in <see cref="RefreshAsync"/>, which the host calls only on explicit request.
/// </summary>
public interface ILensCatalog : IPlugin
{
    Task<LensCatalogInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogLens>> SearchAsync(LensCatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Downloads or reloads the catalog data and returns the new state.</summary>
    Task<LensCatalogInfo> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Mount of a camera body (e.g. <c>Fujifilm</c>, <c>X-T5</c> → <c>Fujifilm X</c>), or null when unknown.</summary>
    Task<string?> MountForBodyAsync(string? maker, string? model, CancellationToken cancellationToken = default);
}
