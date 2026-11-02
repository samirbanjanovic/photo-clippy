using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// <c>metadata.extract</c> adapter. Splits batches by the plugin's <c>maxBatch</c>, matches results back to inputs
/// (by <c>path</c>, then by position) and always returns exactly one result per input, in input order.
/// </summary>
internal sealed class ExternalMetadataExtractor(ExternalPluginConnection connection) : ExternalPluginBase(connection), IMetadataExtractor
{
    private const string Method = "metadata.extract";
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public bool CanHandle(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var descriptor = Connection.Descriptor;
        if (descriptor.Extensions.Count == 0 && descriptor.MediaKinds.Count == 0)
        {
            return true;
        }

        return descriptor.Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) || descriptor.MediaKinds.Contains(file.Kind);
    }

    public async Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return [];
        }

        var chunks = batch.Chunk(Connection.Descriptor.MaxBatch).ToArray();
        if (chunks.Length == 1)
        {
            return await ExtractChunkAsync(chunks[0], cancellationToken).ConfigureAwait(false);
        }

        var results = await Task.WhenAll(chunks.Select(chunk => ExtractChunkAsync(chunk, cancellationToken))).ConfigureAwait(false);
        var merged = new PhotoMetadata[batch.Count];
        var offset = 0;
        foreach (var part in results)
        {
            part.CopyTo(merged, offset);
            offset += part.Length;
        }

        return merged;
    }

    private async Task<PhotoMetadata[]> ExtractChunkAsync(SourceFile[] files, CancellationToken cancellationToken)
    {
        var id = Manifest.Id;
        JsonElement items;
        try
        {
            var result = await Connection.CallAsync(Method, JsonRpc.Params(new MetadataExtractParams { Files = files }), cancellationToken).ConfigureAwait(false);
            items = result.ValueKind == JsonValueKind.Array ? result
                : result.ValueKind == JsonValueKind.Object && result.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r
                : throw new ExternalPluginException(Options.Id, ExternalPluginErrorCodes.InvalidResponse, $"'{Method}' result has no 'results' array");
        }
        catch (ExternalPluginException ex) when (IsPluginError(ex))
        {
            Logger.LogWarning("External plugin {PluginId} failed to extract metadata for {Count} file(s): {Error}", Options.Id, files.Length, ex.Message);
            var warning = $"{id}: {ex.RemoteMessage} (code {ex.Code})";
            return Array.ConvertAll(files, f => Missing(f.Path, id, warning));
        }

        var output = new PhotoMetadata?[files.Length];
        var leftovers = new List<(int Index, JsonElement Element)>();
        var byPath = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < files.Length; i++)
        {
            if (!byPath.TryGetValue(files[i].Path, out var queue))
            {
                byPath[files[i].Path] = queue = new Queue<int>();
            }

            queue.Enqueue(i);
        }

        var position = 0;
        foreach (var element in items.EnumerateArray())
        {
            var index = position++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                Logger.LogWarning("External plugin {PluginId} returned a non-object metadata result at index {Index}", Options.Id, index);
                continue;
            }

            var path = TryGetString(element, "path");
            if (path is not null && byPath.TryGetValue(path, out var queue) && queue.TryDequeue(out var target))
            {
                output[target] = Convert(element, files[target].Path, id);
            }
            else
            {
                leftovers.Add((index, element));
            }
        }

        foreach (var (index, element) in leftovers)
        {
            if (index < output.Length && output[index] is null && TryGetString(element, "path") is null)
            {
                output[index] = Convert(element, files[index].Path, id);
            }
            else
            {
                Logger.LogWarning("External plugin {PluginId} returned an unmatched metadata result at index {Index}", Options.Id, index);
            }
        }

        var missing = 0;
        var final = new PhotoMetadata[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            if (output[i] is { } metadata)
            {
                final[i] = metadata;
            }
            else
            {
                missing++;
                final[i] = Missing(files[i].Path, id, $"{id}: no result returned");
            }
        }

        if (missing > 0)
        {
            Logger.LogDebug("External plugin {PluginId} returned no usable result for {Missing}/{Count} file(s)", Options.Id, missing, files.Length);
        }

        return final;
    }

    private PhotoMetadata? Convert(JsonElement element, string path, string extractorId)
    {
        try
        {
            var node = JsonObject.Create(element, NodeOptions)!;
            node["path"] = path;
            var metadata = node.Deserialize<PhotoMetadata>(ContractJson.Options);
            if (metadata is null)
            {
                return null;
            }

            return metadata with
            {
                Extractor = string.IsNullOrWhiteSpace(metadata.Extractor) ? extractorId : metadata.Extractor,
                Warnings = metadata.Warnings ?? [],
                Extras = metadata.Extras ?? new Dictionary<string, string>(),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            Logger.LogWarning("External plugin {PluginId} returned invalid metadata for {Path}: {Error}", Options.Id, path, ex.Message);
            return null;
        }
    }

    private static PhotoMetadata Missing(string path, string extractorId, string warning) =>
        new() { Path = path, Extractor = extractorId, Warnings = [warning] };

    private static string? TryGetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
