using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli.Web.Auth;

internal sealed record AuthProviderEntry(string Id, IAuthProvider Provider)
{
    public string DisplayName => Provider.DisplayName;
}

/// <summary>The auth plugins listed in <c>auth.providers</c>, created in order. Fails closed on any problem.</summary>
internal sealed class AuthProviderSet : IAsyncDisposable
{
    public AuthProviderSet(IReadOnlyList<AuthProviderEntry> providers)
    {
        Providers = providers;
        DirectoryEntry = providers.FirstOrDefault(p => p.Provider is IUserDirectory);
    }

    public IReadOnlyList<AuthProviderEntry> Providers { get; }

    /// <summary>The first provider that manages its own accounts, if any.</summary>
    public AuthProviderEntry? DirectoryEntry { get; }

    public IUserDirectory? Directory => DirectoryEntry?.Provider as IUserDirectory;

    public static AuthProviderSet Load(AppRuntime runtime)
    {
        var ids = runtime.Options.Auth.Providers
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            throw new CliException(
                ExitCodes.Usage,
                "auth.providers is empty, so nobody could sign in.",
                $"List at least one auth plugin, e.g. \"{AuthOptions.LocalProvider}\", or \"{AuthOptions.NoneProvider}\" to turn sign-in off.");
        }

        var host = runtime.CreateHost();
        var created = new List<AuthProviderEntry>();
        try
        {
            foreach (var id in ids)
            {
                if (!runtime.Catalog.TryGet(id, out var descriptor))
                {
                    throw new CliException(ExitCodes.Usage, $"Auth plugin '{id}' (auth.providers) is not installed.", "`photo-clippy plugins` lists the installed plugins.");
                }

                if (descriptor.Factory.Kind != ComponentKind.Auth)
                {
                    throw new CliException(ExitCodes.Usage, $"'{id}' is a {descriptor.Factory.Kind.ToString().ToLowerInvariant()} plugin, not an auth plugin.");
                }

                IPlugin plugin;
                try
                {
                    plugin = descriptor.Factory.Create(host.CreateContext(runtime.Options.Plugins, id));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or IOException)
                {
                    throw new CliException(ExitCodes.Usage, $"Auth plugin '{id}' could not start: {ex.Message}", $"Check plugins.settings[\"{id}\"].");
                }

                if (plugin is not IAuthProvider provider)
                {
                    (plugin as IDisposable)?.Dispose();
                    throw new CliException(ExitCodes.Error, $"Plugin '{id}' is registered as an auth plugin but does not implement IAuthProvider.");
                }

                created.Add(new AuthProviderEntry(id, provider));
            }
        }
        catch
        {
            DisposeAll(created);
            throw;
        }

        return new AuthProviderSet(created);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in Providers)
        {
            switch (entry.Provider)
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

    private static void DisposeAll(IEnumerable<AuthProviderEntry> entries)
    {
        foreach (var entry in entries)
        {
            (entry.Provider as IDisposable)?.Dispose();
        }
    }
}
