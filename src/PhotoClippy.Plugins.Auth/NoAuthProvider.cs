using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Auth;

/// <summary>Turns sign-in off: every request is an anonymous admin. Only for a trusted network or another auth layer in front.</summary>
public sealed class NoAuthProvider : IRequestAuthProvider
{
    public const string PluginId = "builtin.auth.none";
    public const string PluginVersion = "1.0.0";

    private static readonly AuthenticatedUser Anonymous = new()
    {
        Subject = "anonymous",
        Name = "anonymous",
        DisplayName = "Anonymous",
        Roles = [AuthRoles.Admin],
    };

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "No sign-in",
        Version = PluginVersion,
        Kind = ComponentKind.Auth,
        Description = "Disables sign-in; everyone who can reach the server is an admin.",
        Capabilities = ["auth.request"],
    };

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PluginHealth.Ok("sign-in is disabled"));

    public ValueTask<AuthResult> AuthenticateRequestAsync(IncomingRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AuthResult.Success(Anonymous));
}
