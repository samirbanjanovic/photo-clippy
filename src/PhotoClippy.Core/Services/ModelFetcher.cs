using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Plugins;

namespace PhotoClippy.Core.Services;

public enum ModelFetchStatus
{
    Present,
    Downloaded,
    Failed,
}

public sealed record PluginModel(string PluginId, ModelAsset Asset);

public sealed record ModelFetchProgress(string RelativePath, long Downloaded, long? Total);

public sealed record ModelFetchResult(PluginModel Model, ModelFetchStatus Status, string? Error = null);

/// <summary>Downloads the model files plugins declare via <see cref="IRequiresModels"/>, verifying SHA-256 when known.</summary>
public sealed class ModelFetcher(HttpClient http, string modelsDirectory, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public static IReadOnlyList<PluginModel> Collect(PluginSet plugins) =>
    [
        .. plugins.All
            .OfType<IRequiresModels>()
            .SelectMany(p => p.Models.Select(m => new PluginModel(((IPlugin)p).Manifest.Id, m)))
            .DistinctBy(m => m.Asset.RelativePath, StringComparer.OrdinalIgnoreCase),
    ];

    public string ResolvePath(ModelAsset asset)
    {
        var root = Path.GetFullPath(modelsDirectory);
        var full = Path.GetFullPath(Path.Combine(root, asset.RelativePath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Model path '{asset.RelativePath}' escapes the models directory.");
        }

        return full;
    }

    public bool IsPresent(ModelAsset asset)
    {
        var path = ResolvePath(asset);
        if (!File.Exists(path))
        {
            return false;
        }

        return asset.SizeBytes is not { } size || new FileInfo(path).Length == size;
    }

    public async Task<IReadOnlyList<ModelFetchResult>> FetchAsync(
        IReadOnlyList<PluginModel> models,
        bool force = false,
        bool verifyExisting = false,
        IProgress<ModelFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModelFetchResult>(models.Count);
        foreach (var model in models)
        {
            results.Add(await FetchOneAsync(model, force, verifyExisting, progress, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<ModelFetchResult> FetchOneAsync(PluginModel model, bool force, bool verifyExisting, IProgress<ModelFetchProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = model.Asset;
        string path;
        try
        {
            path = ResolvePath(asset);
        }
        catch (InvalidOperationException ex)
        {
            return new ModelFetchResult(model, ModelFetchStatus.Failed, ex.Message);
        }

        if (!force && IsPresent(asset))
        {
            if (!verifyExisting || asset.Sha256 is null || string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelFetchResult(model, ModelFetchStatus.Present);
            }

            _logger.LogWarning("Model {Path} failed checksum verification; downloading again", asset.RelativePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        try
        {
            using var response = await http.GetAsync(asset.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long downloaded = 0;
                long lastReported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    if (downloaded - lastReported >= 1 << 20)
                    {
                        progress?.Report(new ModelFetchProgress(asset.RelativePath, downloaded, total));
                        lastReported = downloaded;
                    }
                }

                progress?.Report(new ModelFetchProgress(asset.RelativePath, downloaded, total));
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (asset.Sha256 is { } expected && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new ModelFetchResult(model, ModelFetchStatus.Failed, $"SHA-256 mismatch (expected {expected}, got {actual}).");
            }

            File.Move(partial, path, overwrite: true);
            _logger.LogInformation("Downloaded model {Path}", asset.RelativePath);
            return new ModelFetchResult(model, ModelFetchStatus.Downloaded);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            TryDelete(partial);
            return new ModelFetchResult(model, ModelFetchStatus.Failed, ex.Message);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
