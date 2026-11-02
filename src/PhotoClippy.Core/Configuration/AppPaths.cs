using System.Collections;

namespace PhotoClippy.Core.Configuration;

/// <summary>Resolved directories. Created on demand by <see cref="EnsureCreated"/>.</summary>
public sealed record AppPaths
{
    public required string Home { get; init; }
    public required string Database { get; init; }
    public required string Models { get; init; }
    public required string State { get; init; }
    public required string Data { get; init; }
    public required string Plugins { get; init; }

    public static string DefaultHome(IDictionary? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables();
        if (environment[ConfigLoader.EnvPrefix + "HOME"] is string home && home.Length > 0)
        {
            return Path.GetFullPath(home);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(appData, "photo-clippy");
    }

    public static AppPaths Resolve(AppOptions options)
    {
        var home = Path.GetFullPath(options.Paths.Home ?? DefaultHome());
        return new AppPaths
        {
            Home = home,
            Database = Path.GetFullPath(options.Paths.Database ?? Path.Combine(home, "cache.db")),
            Models = Path.GetFullPath(options.Paths.Models ?? Path.Combine(home, "models")),
            State = Path.GetFullPath(options.Paths.State ?? Path.Combine(home, "state")),
            Data = Path.GetFullPath(options.Paths.Data ?? FindDataDirectory()),
            Plugins = Path.GetFullPath(options.Plugins.Directory ?? Path.Combine(home, "plugins")),
        };
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
    }

    /// <summary>&lt;app&gt;/data, or the nearest ancestor 'data' folder containing the taxonomy (development runs).</summary>
    public static string FindDataDirectory()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "data");
        if (File.Exists(Path.Combine(candidate, "taxonomy", "genres.json")))
        {
            return candidate;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(probe, "taxonomy", "genres.json")))
            {
                return probe;
            }
        }

        return candidate;
    }
}
