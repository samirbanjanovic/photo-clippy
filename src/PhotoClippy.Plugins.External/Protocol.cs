using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

internal sealed record InitializeParams
{
    public required string ContractVersion { get; init; }
    public required ComponentKind Kind { get; init; }
    public required string PluginId { get; init; }
    public required JsonElement Settings { get; init; }
    public required string DataDirectory { get; init; }
    public required string ModelsDirectory { get; init; }
    public required string StateDirectory { get; init; }
    public required GenreTaxonomy Taxonomy { get; init; }
}

internal sealed record InitializeResult
{
    public PluginManifest? Manifest { get; init; }
    public RequirementsDto? Requirements { get; init; }

    /// <summary>Report renderers: output format (e.g. <c>md</c>).</summary>
    public string? Format { get; init; }

    /// <summary>Metadata extractors: lower-case file extensions handled (e.g. <c>.jpg</c>); empty = all.</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>Metadata extractors: media kinds handled; empty = all.</summary>
    public IReadOnlyList<MediaKind>? MediaKinds { get; init; }

    /// <summary>Metadata extractors: maximum files per <c>metadata.extract</c> request.</summary>
    public int? MaxBatch { get; init; }
}

internal sealed record RequirementsDto
{
    public int? MaxEdge { get; init; }
}

internal sealed record HealthResult
{
    public bool Healthy { get; init; } = true;
    public string? Message { get; init; }
}

internal sealed record MetadataExtractParams
{
    public required IReadOnlyList<SourceFile> Files { get; init; }
}

internal sealed record ReportRenderParams
{
    public required ReportModel Model { get; init; }
    public required RenderTarget Target { get; init; }
}

internal sealed record LensSearchResult
{
    public IReadOnlyList<CatalogLens> Lenses { get; init; } = [];
}

internal sealed record LensMountParams
{
    public string? Maker { get; init; }
    public string? Model { get; init; }
}

internal sealed record LensMountResult
{
    public string? Mount { get; init; }
}
