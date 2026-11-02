using PhotoClippy.Contracts;

namespace PhotoClippy.Cli;

internal sealed record PluginRow(
    string Id,
    ComponentKind Kind,
    string? Version,
    string Source,
    string? Location,
    bool Selected,
    bool? Healthy,
    string? Health,
    string? Description,
    IReadOnlyList<ModelAsset> Models);

/// <summary>Instantiates every known plugin (built-in, directory-loaded and external) to report versions, selection and health.</summary>
internal static class PluginInventory
{
    private static readonly ComponentKind[] AllKinds = Enum.GetValues<ComponentKind>();

    public static async Task<(IReadOnlyList<PluginRow> Rows, IReadOnlyList<string> Notes)> CollectAsync(AppRuntime runtime, bool checkHealth, CancellationToken cancellationToken)
    {
        var options = runtime.Options.Plugins;
        var selectedIds = new HashSet<string>(
            [.. options.Metadata, .. options.Composition, .. options.Color, options.Recommender, options.LensCatalog, .. options.Reports, .. runtime.Options.Auth.Providers],
            StringComparer.OrdinalIgnoreCase);
        var rows = new List<PluginRow>();
        var notes = new List<string>(runtime.Catalog.LoadErrors);

        await using var selected = await runtime.CreatePluginsAsync(new HashSet<ComponentKind>(AllKinds), checkHealth: false, cancellationToken).ConfigureAwait(false);
        notes.AddRange(selected.Notes);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in selected.All)
        {
            var id = plugin.Manifest.Id;
            seen.Add(id);
            var descriptor = runtime.Catalog.TryGet(id, out var d) ? d : null;
            rows.Add(await DescribeAsync(
                plugin,
                descriptor?.Source.ToString().ToLowerInvariant() ?? "external",
                descriptor?.Location,
                selected: descriptor is null || selectedIds.Contains(id),
                checkHealth,
                cancellationToken).ConfigureAwait(false));
        }

        var host = runtime.CreateHost();
        foreach (var descriptor in runtime.Catalog.Factories.Where(f => !seen.Contains(f.Factory.Id)))
        {
            IPlugin? plugin = null;
            try
            {
                plugin = descriptor.Factory.Create(host.CreateContext(options, descriptor.Factory.Id));
                rows.Add(await DescribeAsync(plugin, descriptor.Source.ToString().ToLowerInvariant(), descriptor.Location, selectedIds.Contains(descriptor.Factory.Id), checkHealth, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rows.Add(new PluginRow(descriptor.Factory.Id, descriptor.Factory.Kind, null, descriptor.Source.ToString().ToLowerInvariant(), descriptor.Location, false, false, ex.Message, null, []));
            }
            finally
            {
                await DisposeAsync(plugin).ConfigureAwait(false);
            }
        }

        return ([.. rows.OrderBy(r => r.Kind).ThenByDescending(r => r.Selected).ThenBy(r => r.Id, StringComparer.Ordinal)], notes);
    }

    private static async Task<PluginRow> DescribeAsync(IPlugin plugin, string source, string? location, bool selected, bool checkHealth, CancellationToken cancellationToken)
    {
        var manifest = plugin.Manifest;
        bool? healthy = null;
        string? message = null;
        if (checkHealth)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var health = await plugin.CheckHealthAsync(timeout.Token).ConfigureAwait(false);
                healthy = health.Healthy;
                message = health.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                healthy = false;
                message = "health check timed out";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                healthy = false;
                message = ex.Message;
            }
        }

        var models = plugin is IRequiresModels requires ? requires.Models : [];
        return new PluginRow(manifest.Id, manifest.Kind, manifest.Version, source, location, selected, healthy, message, manifest.Description, models);
    }

    private static async ValueTask DisposeAsync(IPlugin? plugin)
    {
        switch (plugin)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
