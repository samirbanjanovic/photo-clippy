using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Services;

namespace PhotoClippy.Cli.Web;

/// <summary>Editable copy of the config file plus the fields the environment or command line pins (path → source).</summary>
internal sealed record SettingsDraft(AppOptions Options, IReadOnlyDictionary<string, string> Pins, string ConfigPath, bool ConfigExists);

internal sealed record SaveOutcome(bool Saved, bool Deferred, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static SaveOutcome Invalid(IReadOnlyList<string> errors, IReadOnlyList<string> warnings) => new(false, false, errors, warnings);
}

/// <summary>
/// Reads and writes the config file for the web UI and hot-applies changes. Values set by environment variables or
/// command-line flags win over the file, so they are reported as pins and shown read-only. Fields that load code or
/// widen file-system access (plugin directory/external plugins/plugin settings, paths, browse roots, listen URLs,
/// report output) or control sign-in (<c>auth</c>) are never written from the UI.
/// </summary>
internal sealed class SettingsService(
    AppRuntime runtime,
    ScanCoordinator coordinator,
    ScanScheduler scheduler,
    IReadOnlyList<string> seedRoots,
    ILogger<SettingsService> logger,
    CancellationToken stopping)
{
    public const string FromEnvironment = "environment";
    public const string FromCommandLine = "command line";

    private readonly Lock _saveLock = new();

    public event Action? Applied;

    /// <summary>A saved change is waiting for the running scan to finish before it takes effect.</summary>
    public bool PendingApply { get; private set; }

    /// <summary>Folders passed on the command line; used as library roots until the config names its own.</summary>
    public IReadOnlyList<string> SeedRoots => seedRoots;

    public IReadOnlyList<string> BrowseRoots
    {
        get
        {
            var configured = runtime.Options.Serve.BrowseRoots;
            if (configured.Count > 0)
            {
                return configured;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return [.. runtime.Options.Library.Roots, .. seedRoots, .. string.IsNullOrEmpty(home) ? Array.Empty<string>() : [home]];
        }
    }

    public SettingsDraft LoadDraft()
    {
        var layers = runtime.LoadLayers();
        var pins = ComputePins(layers);
        var draft = layers.FileOnly;
        if (draft.Library.Roots.Count == 0 && !pins.ContainsKey("library.roots"))
        {
            draft.Library.Roots = [.. runtime.Options.Library.Roots];
        }

        var path = runtime.WritableConfigFile;
        return new SettingsDraft(draft, pins, path, File.Exists(path));
    }

    public static IReadOnlyDictionary<string, string> ComputePins(ConfigLayers layers)
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Diff(ToNode(layers.FileOnly), ToNode(layers.WithEnvironment), string.Empty, FromEnvironment, pins);
        Diff(ToNode(layers.WithEnvironment), ToNode(layers.Effective), string.Empty, FromCommandLine, pins);
        foreach (var key in pins.Keys.Where(k => k.StartsWith("paths", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            pins.Remove(key);
        }

        return pins;
    }

    public static bool IsPinned(IReadOnlyDictionary<string, string> pins, string path, out string? source)
    {
        if (pins.TryGetValue(path, out source))
        {
            return true;
        }

        foreach (var (key, value) in pins)
        {
            if (key.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))
            {
                source = value;
                return true;
            }
        }

        source = null;
        return false;
    }

    public IReadOnlyList<string> Validate(AppOptions draft, FolderBrowser browser, out IReadOnlyList<string> warnings)
    {
        var errors = new List<string>();
        var warn = new List<string>();
        var current = new HashSet<string>(runtime.Options.Library.Roots.Select(SafeFullPath), StringComparer.Ordinal);
        foreach (var root in draft.Library.Roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (!current.Contains(SafeFullPath(root)) && !browser.IsAllowed(root, out _))
            {
                errors.Add($"Library folder '{root}' is outside the folders this server may browse ({string.Join(", ", browser.Roots)}).");
            }
            else if (!Directory.Exists(root))
            {
                warn.Add($"Library folder '{root}' does not exist (yet).");
            }
        }

        if (draft.Library.Roots.Count(r => !string.IsNullOrWhiteSpace(r)) == 0)
        {
            warn.Add("No library folders are configured, so scans will find nothing.");
        }

        var a = draft.Analysis;
        if (a.Scope is not ("interchangeable" or "all"))
        {
            errors.Add("Scope must be 'interchangeable' or 'all'.");
        }

        Range(errors, "I/O concurrency", a.IoConcurrency, 1, 64);
        Range(errors, "CPU workers", a.CpuConcurrency, 0, 256);
        Range(errors, "Metadata batch size", a.MetadataBatchSize, 1, 10_000);
        Range(errors, "High-ISO threshold", a.HighIsoThreshold, 100, 1_000_000);
        Range(errors, "Minimum genre confidence", a.MinGenreConfidence, 0, 1);
        Range(errors, "Junk threshold", a.JunkThreshold, 0, 1);
        Range(errors, "Recency half-life", a.RecencyHalfLifeYears, 0, 100);
        if (a.Since is { } since && a.Until is { } until && since > until)
        {
            errors.Add("'Since' must be on or before 'Until'.");
        }

        var p = draft.Plugins;
        if (p.Metadata.Count == 0)
        {
            errors.Add("Select at least one metadata extractor.");
        }

        if (string.IsNullOrWhiteSpace(p.Recommender))
        {
            errors.Add("Select a recommender.");
        }

        if (p.Reports.Count == 0)
        {
            errors.Add("Select at least one report format.");
        }

        foreach (var (id, weight) in p.GenreWeights)
        {
            Range(errors, $"Genre weight for {id}", weight, 0, 10);
        }

        var r = draft.Recommendation;
        RecommendationRunner.ParseCandidates(r.Candidates, out var candidateErrors);
        errors.AddRange(candidateErrors.Select(e => $"Default candidate: {e}"));
        Range(errors, "Top N", r.TopN, 1, 50);
        Range(errors, "Bootstrap replicates", r.BootstrapReplicates, 0, 20_000);
        if (r.BudgetUsd is <= 0)
        {
            errors.Add("Budget must be positive (or empty for no limit).");
        }

        if (r.MaxWeightG is <= 0)
        {
            errors.Add("Maximum weight must be positive (or empty for no limit).");
        }

        if (string.IsNullOrWhiteSpace(draft.Report.Title))
        {
            errors.Add("The report needs a title.");
        }

        Range(errors, "Rescan interval", draft.Serve.RescanHours, 0, 24 * 365);
        warnings = warn;
        return errors;
    }

    public async Task<SaveOutcome> SaveAsync(AppOptions draft, FolderBrowser browser, CancellationToken cancellationToken)
    {
        var errors = Validate(draft, browser, out var warnings);
        if (errors.Count > 0)
        {
            return SaveOutcome.Invalid(errors, warnings);
        }

        var path = runtime.WritableConfigFile;
        lock (_saveLock)
        {
            var file = runtime.LoadLayers().FileOnly;
            draft.Paths = file.Paths;
            draft.Plugins.Directory = file.Plugins.Directory;
            draft.Plugins.External = file.Plugins.External;
            draft.Plugins.Settings = file.Plugins.Settings;
            draft.Serve.BrowseRoots = file.Serve.BrowseRoots;
            draft.Serve.Urls = file.Serve.Urls;
            draft.Report.Output = file.Report.Output;
            draft.Report.BaseName = file.Report.BaseName;
            draft.Auth = file.Auth;
            draft.Library.Roots = [.. draft.Library.Roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal)];
            draft.Library.Ignore = [.. draft.Library.Ignore.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())];
            draft.Recommendation.Candidates = [.. draft.Recommendation.Candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())];
            try
            {
                ConfigLoader.Save(draft, path);
                runtime.AdoptConfigFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SaveOutcome.Invalid([$"Could not write {path}: {ex.Message}"], warnings);
            }
        }

        logger.LogInformation("Configuration saved to {Path}", path);
        var apply = coordinator.RunExclusiveAsync(ApplyLatest, stopping);
        if (apply.IsCompleted)
        {
            await apply.ConfigureAwait(false);
            return new SaveOutcome(true, false, [], warnings);
        }

        PendingApply = true;
        _ = apply.ContinueWith(
            t => logger.LogWarning(t.Exception, "Applying saved configuration failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return new SaveOutcome(true, true, [], warnings);
    }

    /// <summary>Reloads file + environment + flags and swaps them in. Runs under the coordinator gate.</summary>
    private void ApplyLatest()
    {
        var effective = runtime.LoadLayers().Effective;
        if (effective.Library.Roots.Count == 0 && seedRoots.Count > 0)
        {
            effective.Library.Roots = [.. seedRoots];
        }

        runtime.ApplyOptions(effective);
        PendingApply = false;
        scheduler.Reschedule();
        coordinator.Notify("config.applied", new { path = runtime.ConfigFile });
        Applied?.Invoke();
    }

    private static JsonNode? ToNode(AppOptions options) => JsonSerializer.SerializeToNode(options, ConfigLoader.JsonOptions);

    private static void Diff(JsonNode? a, JsonNode? b, string path, string source, Dictionary<string, string> pins)
    {
        if (a is JsonObject left && b is JsonObject right)
        {
            foreach (var key in left.Select(p => p.Key).Union(right.Select(p => p.Key), StringComparer.Ordinal))
            {
                Diff(left[key], right[key], path.Length == 0 ? key : $"{path}.{key}", source, pins);
            }

            return;
        }

        if (!JsonNode.DeepEquals(a, b))
        {
            pins.TryAdd(path, source);
        }
    }

    private static void Range(List<string> errors, string name, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            errors.Add($"{name} must be between {min:0.##} and {max:0.##}.");
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
