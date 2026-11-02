namespace PhotoClippy.Contracts;

/// <summary>How well a lens suits a genre; used by recommenders.</summary>
public sealed record GenreAffinity
{
    /// <summary>Preferred 35 mm-equivalent focal range.</summary>
    public double FocalMin35 { get; init; }
    public double FocalMax35 { get; init; }
    /// <summary>When true, faster lenses (≤ <see cref="FastAperture"/>) are favoured.</summary>
    public bool PrefersFast { get; init; }
    public double FastAperture { get; init; } = 2.0;
    /// <summary>Minimum magnification required (e.g. 0.5 for macro), null when irrelevant.</summary>
    public double? MinMagnification { get; init; }
    /// <summary>When true, subject isolation (shallow depth of field) is typically wanted.</summary>
    public bool WantsIsolation { get; init; }
}

public sealed record GenreDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Grouping for display, e.g. "Places", "People", "Nature", "Things", "Night", "Other".</summary>
    public string Group { get; init; } = "Other";
    /// <summary>Text prompts for zero-shot classifiers (CLIP).</summary>
    public IReadOnlyList<string> Prompts { get; init; } = [];
    /// <summary>Non-photographic content (screenshots, documents) excluded from shooting statistics.</summary>
    public bool Junk { get; init; }
    public GenreAffinity? Affinity { get; init; }
}

public sealed record GenreTaxonomy
{
    public string Version { get; init; } = "1";
    public IReadOnlyList<GenreDefinition> Genres { get; init; } = [];

    public GenreDefinition? Find(string id) => Genres.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
}
