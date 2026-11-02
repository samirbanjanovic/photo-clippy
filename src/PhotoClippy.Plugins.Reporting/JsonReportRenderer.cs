using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

/// <summary>Writes the complete <see cref="ReportModel"/> as indented JSON (<see cref="ContractJson.Indented"/>), UTF-8 without BOM.</summary>
public sealed class JsonReportRenderer : IReportRenderer
{
    public const string PluginId = "builtin.report.json";
    public const string PluginVersion = "1.0.0";

    public JsonReportRenderer()
    {
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "JSON report",
            Version = PluginVersion,
            Kind = ComponentKind.Report,
            Description = "The full report model as indented JSON, readable back with ContractJson.Options.",
            Capabilities = ["report.json"],
            Fingerprint = RenderIo.Fingerprint("json-1;indented"),
        };
    }

    public PluginManifest Manifest { get; }

    public string Format => "json";

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(PluginHealth.Ok());

    public async Task<RenderResult> RenderAsync(ReportModel model, RenderTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dir = RenderIo.PrepareDirectory(target);
        var path = Path.Combine(dir, RenderIo.BaseName(target) + ".json");
        var tmp = path + ".partial";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, model, ContractJson.Indented, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            RenderIo.TryDelete(tmp);
            throw;
        }

        return new RenderResult { Files = [path] };
    }
}
