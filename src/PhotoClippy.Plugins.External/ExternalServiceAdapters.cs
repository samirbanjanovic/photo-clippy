using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary><c>recommend.run</c> adapter. Fills <c>recommenderId</c>, <c>preset</c> and <c>target</c> when the plugin omits them.</summary>
internal sealed class ExternalRecommender(ExternalPluginConnection connection) : ExternalPluginBase(connection), IRecommender
{
    private const string Method = "recommend.run";
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<RecommendationSet> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Connection.CallAsync(Method, JsonRpc.Params(request), cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ExternalPluginException(Options.Id, ExternalPluginErrorCodes.InvalidResponse, $"'{Method}' must return an object");
        }

        var node = JsonObject.Create(result, NodeOptions)!;
        SetDefault(node, "recommenderId", () => JsonValue.Create(Manifest.Id));
        SetDefault(node, "preset", () => JsonValue.Create(request.Preferences.Preset));
        SetDefault(node, "target", () => JsonSerializer.SerializeToNode(request.Target, ContractJson.Options));
        return Connection.Decode<RecommendationSet>(ToElement(node), Method);
    }

    private static void SetDefault(JsonObject node, string name, Func<JsonNode?> value)
    {
        if (node[name] is null)
        {
            node[name] = value();
        }
    }
}

/// <summary><c>report.render</c> adapter. Relative file paths in the result are resolved against the output directory.</summary>
internal sealed class ExternalReportRenderer(ExternalPluginConnection connection) : ExternalPluginBase(connection), IReportRenderer
{
    private const string Method = "report.render";

    public string Format => Connection.Descriptor.Format;

    public async Task<RenderResult> RenderAsync(ReportModel model, RenderTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(target);
        var outputDirectory = Path.GetFullPath(target.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var parameters = new ReportRenderParams { Model = model, Target = target with { OutputDirectory = outputDirectory } };
        var result = await Connection.CallAsync(Method, JsonRpc.Params(parameters), cancellationToken).ConfigureAwait(false);
        var rendered = Connection.Decode<RenderResult>(result, Method);
        var files = (rendered.Files ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f, outputDirectory))
            .ToArray();
        return rendered with { Files = files };
    }
}

/// <summary><c>lenses.info</c> / <c>lenses.search</c> / <c>lenses.refresh</c> / <c>lenses.mount</c> adapter.</summary>
internal sealed class ExternalLensCatalog(ExternalPluginConnection connection) : ExternalPluginBase(connection), ILensCatalog
{
    public async Task<LensCatalogInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var result = await Connection.CallAsync("lenses.info", null, cancellationToken).ConfigureAwait(false);
        return Connection.Decode<LensCatalogInfo>(result, "lenses.info");
    }

    public async Task<IReadOnlyList<CatalogLens>> SearchAsync(LensCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = await Connection.CallAsync("lenses.search", JsonRpc.Params(query), cancellationToken).ConfigureAwait(false);
        var lenses = Connection.Decode<LensSearchResult>(result, "lenses.search").Lenses ?? [];
        return [.. lenses.Where(l => l is not null).Take(Math.Max(1, query.Limit))];
    }

    public async Task<LensCatalogInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var result = await Connection.CallAsync("lenses.refresh", null, cancellationToken).ConfigureAwait(false);
        return Connection.Decode<LensCatalogInfo>(result, "lenses.refresh");
    }

    public async Task<string?> MountForBodyAsync(string? maker, string? model, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Connection.CallAsync("lenses.mount", JsonRpc.Params(new LensMountParams { Maker = maker, Model = model }), cancellationToken).ConfigureAwait(false);
            return Connection.Decode<LensMountResult>(result, "lenses.mount").Mount;
        }
        catch (ExternalPluginException ex) when (ex.IsRemote && ex.Code == ExternalPluginErrorCodes.MethodNotFound)
        {
            return null;
        }
    }
}
