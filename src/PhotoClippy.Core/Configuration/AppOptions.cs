using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Configuration;

/// <summary>Root configuration (photo-clippy.json, PHOTO_CLIPPY_* environment variables, CLI flags).</summary>
public sealed class AppOptions
{
    public LibraryOptions Library { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public PluginOptions Plugins { get; set; } = new();
    public RecommendationOptions Recommendation { get; set; } = new();
    public ReportOptions Report { get; set; } = new();
    public PathOptions Paths { get; set; } = new();
    public ServeOptions Serve { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
}

public sealed class LibraryOptions
{
    public List<string> Roots { get; set; } = [];
    /// <summary>Glob patterns (relative to a root, '/' separators) to skip, e.g. "**/exports/**".</summary>
    public List<string> Ignore { get; set; } = [];
    public bool FollowSymlinks { get; set; }
    public bool IncludeHidden { get; set; }
}

public sealed class AnalysisOptions
{
    /// <summary>"interchangeable" (default; falls back to all when no ILC captures exist) or "all".</summary>
    public string Scope { get; set; } = "interchangeable";
    /// <summary>Run pixel analyzers (composition, color). False = metadata only.</summary>
    public bool Frames { get; set; } = true;
    /// <summary>Concurrent metadata batches (I/O bound; raise for network shares).</summary>
    public int IoConcurrency { get; set; } = 8;
    /// <summary>Concurrent decode+analyze workers; 0 = processor count.</summary>
    public int CpuConcurrency { get; set; }
    public int MetadataBatchSize { get; set; } = 64;
    public int HighIsoThreshold { get; set; } = 3200;
    /// <summary>Recency weighting of recommendation samples (half-life in years); 0 disables.</summary>
    public double RecencyHalfLifeYears { get; set; }
    /// <summary>Minimum fused genre probability to assign a genre.</summary>
    public double MinGenreConfidence { get; set; } = 0.15;
    /// <summary>Junk genres (screenshots, documents) at or above this probability are excluded from statistics.</summary>
    public double JunkThreshold { get; set; } = 0.5;
    public bool RetryFailed { get; set; }
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
}

public sealed class PluginOptions
{
    /// <summary>Additional directory scanned for plugin assemblies (one sub-folder per plugin).</summary>
    public string? Directory { get; set; }
    public List<string> Metadata { get; set; } = ["builtin.metadata.native"];
    public List<string> Composition { get; set; } = ["builtin.composition.clip", "builtin.composition.heuristic"];
    public List<string> Color { get; set; } = ["builtin.color"];
    public string Recommender { get; set; } = "builtin.recommend.mcda";
    public List<string> Reports { get; set; } = ["builtin.report.html", "builtin.report.json", "builtin.report.csv"];
    /// <summary>Weight of each composition analyzer in genre fusion (default 1; heuristic priors 0.25).</summary>
    public Dictionary<string, double> GenreWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["builtin.composition.clip"] = 1.0,
        ["builtin.composition.heuristic"] = 0.25,
    };
    /// <summary>Plugin id → settings object handed to the plugin as <see cref="PluginContext.Settings"/>.</summary>
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Out-of-process plugins; each element is bound by the host's external plugin activator.</summary>
    public List<JsonElement> External { get; set; } = [];
}

public sealed class RecommendationOptions
{
    /// <summary>Optional shorthand defaults for explicit recommendation requests; never evaluated during automatic scans.</summary>
    public List<string> Candidates { get; set; } = [];
    public string Preset { get; set; } = "balanced";
    public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double? BudgetUsd { get; set; }
    public double? MaxWeightG { get; set; }
    /// <summary>Body key or display substring to target; default = most used recent body.</summary>
    public string? Body { get; set; }
    public int BootstrapReplicates { get; set; } = 500;
    public int Seed { get; set; } = 1729;
    public int TopN { get; set; } = 8;
    /// <summary>Prompt on an interactive terminal when a recommendation is requested and no candidates are supplied.</summary>
    public bool Prompt { get; set; } = true;
}

public sealed class ReportOptions
{
    public string Output { get; set; } = "photo-report";
    public string BaseName { get; set; } = "photo-report";
    public string Title { get; set; } = "Photo habits";
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PathOptions
{
    /// <summary>Base directory for cache, models, state and plugins. Default: PHOTO_CLIPPY_HOME or the per-user app-data folder.</summary>
    public string? Home { get; set; }
    public string? Database { get; set; }
    public string? Models { get; set; }
    public string? State { get; set; }
    /// <summary>Bundled data (taxonomy). Default: &lt;app&gt;/data.</summary>
    public string? Data { get; set; }
}

public sealed class ServeOptions
{
    public string Urls { get; set; } = "http://0.0.0.0:8080";
    /// <summary>Periodic rescan interval in hours; 0 = only on demand.</summary>
    public double RescanHours { get; set; }
    public bool RunOnStart { get; set; } = true;
    /// <summary>
    /// Folders the web UI may browse when picking library roots (the UI cannot add roots outside them).
    /// Empty = the configured library roots plus the user's home folder.
    /// </summary>
    public List<string> BrowseRoots { get; set; } = [];
}

/// <summary>Web UI sign-in. Read at start-up from the config file or environment only; the web UI never writes it.</summary>
public sealed class AuthOptions
{
    public const string LocalProvider = "builtin.auth.local";
    public const string NoneProvider = "builtin.auth.none";

    /// <summary>
    /// Auth plugins, tried in order. <c>builtin.auth.local</c> keeps accounts in the data folder;
    /// <c>builtin.auth.none</c> turns sign-in off (everyone is an admin).
    /// </summary>
    public List<string> Providers { get; set; } = [LocalProvider];

    /// <summary>When non-empty, only these user names may sign in, whatever the provider says.</summary>
    public List<string> AllowedUsers { get; set; } = [];

    /// <summary>User names that get the admin role in addition to any roles from the provider.</summary>
    public List<string> Admins { get; set; } = [];

    /// <summary>Sliding session lifetime in hours.</summary>
    public double SessionHours { get; set; } = 12;

    /// <summary>
    /// Proxies (IPs or CIDR ranges) whose X-Forwarded-For/-Proto headers are trusted, e.g. Traefik. Default: loopback
    /// and private ranges, which suits a reverse proxy on a home network.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];
}
