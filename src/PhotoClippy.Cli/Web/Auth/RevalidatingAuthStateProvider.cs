using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace PhotoClippy.Cli.Web.Auth;

/// <summary>
/// Re-checks the signed-in user of each open Blazor circuit every minute. Ends the circuit's session when the account is gone
/// or its claims changed (e.g. admin revoked); the browser then reloads and the cookie handler issues the updated principal.
/// </summary>
internal sealed class RevalidatingAuthStateProvider(ILoggerFactory loggerFactory, AuthService auth) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var current = await auth.ValidateAsync(user, cancellationToken).ConfigureAwait(false);
        return current is not null && AuthWeb.Signature(current) == AuthWeb.Signature(user);
    }
}
