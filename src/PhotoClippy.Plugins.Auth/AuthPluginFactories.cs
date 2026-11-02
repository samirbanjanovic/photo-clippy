using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Auth;

/// <summary>Host entry point for <c>builtin.auth.local</c>; reads <see cref="LocalAccountsSettings"/>.</summary>
public sealed class LocalAccountsProviderFactory : IPluginFactory
{
    public string Id => LocalAccountsProvider.PluginId;

    public ComponentKind Kind => ComponentKind.Auth;

    public IPlugin Create(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new LocalAccountsProvider(context.StateDirectory, context.GetSettings<LocalAccountsSettings>());
    }
}

/// <summary>Host entry point for <c>builtin.auth.none</c>.</summary>
public sealed class NoAuthProviderFactory : IPluginFactory
{
    public string Id => NoAuthProvider.PluginId;

    public ComponentKind Kind => ComponentKind.Auth;

    public IPlugin Create(PluginContext context) => new NoAuthProvider();
}
