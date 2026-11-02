namespace PhotoClippy.Contracts;

// Auth plugins prove who someone is; the host owns everything else (session cookies, the sign-in page,
// allow-lists, admin mapping, throttling). A provider implements IAuthProvider plus one or more flows:
//   ICredentialAuthProvider  user name + password (+ optional second factor): local accounts, Proxmox, LDAP, PAM
//   IRedirectAuthProvider    browser redirect to an identity provider: OIDC/OAuth (Pocket ID, Authelia, Authentik)
//   IRequestAuthProvider     identity asserted on every request: a forward-auth proxy, or no sign-in at all
// Providers that own their accounts can also implement IUserDirectory so the host can manage them.

/// <summary>Role names the host interprets. Providers may return other roles; they are kept but ignored.</summary>
public static class AuthRoles
{
    /// <summary>May change settings, download models and manage accounts.</summary>
    public const string Admin = "admin";
}

/// <summary>An identity confirmed by an auth provider.</summary>
public sealed record AuthenticatedUser
{
    /// <summary>Stable, unique id within the provider (an account id, an OIDC <c>sub</c>). Not shown to people.</summary>
    public required string Subject { get; init; }

    /// <summary>Login name, matched (case-insensitively) against the host's allow-list and admin list, e.g. <c>alice</c> or <c>alice@pve</c>.</summary>
    public required string Name { get; init; }

    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// Opaque account version. The host stores it in the session; when <see cref="IAuthProvider.RefreshAsync"/> returns a
    /// user whose stamp differs (for example after a password change) the session ends.
    /// </summary>
    public string? SessionStamp { get; init; }
}

public enum AuthStatus
{
    /// <summary>Signed in; <see cref="AuthResult.User"/> is set.</summary>
    Succeeded,

    /// <summary>Rejected. The host stops trying other providers and shows a generic message.</summary>
    Failed,

    /// <summary>Password accepted; a second factor is needed (<see cref="AuthResult.Challenge"/>).</summary>
    SecondFactorRequired,

    /// <summary>This provider does not know the user or does not handle the request; the host tries the next provider.</summary>
    NoResult,
}

public sealed record AuthResult
{
    public required AuthStatus Status { get; init; }
    public AuthenticatedUser? User { get; init; }

    /// <summary>Optional text for the person signing in. Never reveal whether an account exists.</summary>
    public string? Message { get; init; }

    public SecondFactorChallenge? Challenge { get; init; }

    public static AuthResult Success(AuthenticatedUser user) => new() { Status = AuthStatus.Succeeded, User = user };
    public static AuthResult Fail(string? message = null) => new() { Status = AuthStatus.Failed, Message = message };
    public static AuthResult NeedSecondFactor(SecondFactorChallenge challenge) => new() { Status = AuthStatus.SecondFactorRequired, Challenge = challenge };
    public static AuthResult NoResult { get; } = new() { Status = AuthStatus.NoResult };
}

/// <param name="Prompt">Shown above the code field, e.g. "Enter the code from your authenticator app".</param>
/// <param name="State">Opaque provider state for the second step. The host encrypts it and expires it after five minutes.</param>
public sealed record SecondFactorChallenge(string Prompt, string State);

/// <summary>Base contract for auth plugins (<see cref="ComponentKind.Auth"/>). Implementations must be thread-safe.</summary>
public interface IAuthProvider : IPlugin
{
    /// <summary>Label on the sign-in page and account page, e.g. "Local accounts" or "Proxmox".</summary>
    string DisplayName => Manifest.Name;

    /// <summary>
    /// Re-checks a signed-in user about once a minute. Return the current view of the user (roles may change), or
    /// <c>null</c> to end the session (account deleted or disabled). A changed <see cref="AuthenticatedUser.SessionStamp"/> also ends it.
    /// </summary>
    ValueTask<AuthenticatedUser?> RefreshAsync(AuthenticatedUser user, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuthenticatedUser?>(user);

    /// <summary>Optional single sign-out: where to send the browser after the host has ended its own session.</summary>
    ValueTask<Uri?> GetSignOutUriAsync(AuthenticatedUser user, Uri returnUri, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Uri?>(null);
}

public sealed record CredentialRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }

    /// <summary>Client address after trusted proxies are unwrapped; for logging or provider-side throttling.</summary>
    public string? RemoteAddress { get; init; }
}

public sealed record SecondFactorRequest
{
    public required string Username { get; init; }
    public required string Code { get; init; }

    /// <summary>The <see cref="SecondFactorChallenge.State"/> returned by the first step.</summary>
    public required string State { get; init; }

    public string? RemoteAddress { get; init; }
}

/// <summary>User name and password, optionally followed by a second factor.</summary>
public interface ICredentialAuthProvider : IAuthProvider
{
    /// <summary>Return <see cref="AuthResult.NoResult"/> for unknown users so the host can try the next provider.</summary>
    ValueTask<AuthResult> AuthenticateAsync(CredentialRequest request, CancellationToken cancellationToken = default);

    ValueTask<AuthResult> VerifySecondFactorAsync(SecondFactorRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AuthResult.Fail("This sign-in method does not use a second factor."));
}

public sealed record RedirectRequest
{
    /// <summary>Absolute URL the identity provider must send the browser back to (register it with the identity provider).</summary>
    public required Uri CallbackUri { get; init; }
}

/// <param name="RedirectUri">Where to send the browser (the identity provider's authorization endpoint).</param>
/// <param name="State">Opaque state (nonce, PKCE verifier) the host keeps in an encrypted, short-lived cookie until the callback.</param>
public sealed record RedirectChallenge(Uri RedirectUri, string State);

public sealed record RedirectCallback
{
    public required Uri CallbackUri { get; init; }

    /// <summary>The <see cref="RedirectChallenge.State"/> from <see cref="IRedirectAuthProvider.BeginAsync"/>.</summary>
    public required string State { get; init; }

    /// <summary>Query-string parameters of the callback request (<c>code</c>, <c>state</c>, <c>error</c>…).</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
}

/// <summary>Browser redirect to an external identity provider and back (OIDC authorization-code flow and similar).</summary>
public interface IRedirectAuthProvider : IAuthProvider
{
    ValueTask<RedirectChallenge> BeginAsync(RedirectRequest request, CancellationToken cancellationToken = default);
    ValueTask<AuthResult> CompleteAsync(RedirectCallback callback, CancellationToken cancellationToken = default);
}

public sealed record IncomingRequest
{
    /// <summary>Request headers; names are case-insensitive.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Address of the direct peer (for example the reverse proxy). Check it before trusting identity headers.</summary>
    public string? PeerAddress { get; init; }

    /// <summary>Client address after trusted proxies are unwrapped.</summary>
    public string? RemoteAddress { get; init; }
}

/// <summary>
/// Identity asserted on each request without a session, e.g. headers set by a forward-auth proxy. Called on every such
/// request, so keep it cheap. Return <see cref="AuthResult.NoResult"/> to fall through to the sign-in page.
/// </summary>
public interface IRequestAuthProvider : IAuthProvider
{
    ValueTask<AuthResult> AuthenticateRequestAsync(IncomingRequest request, CancellationToken cancellationToken = default);
}

public sealed record DirectoryUser
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastSignInUtc { get; init; }
}

public sealed record NewDirectoryUser
{
    public required string Name { get; init; }
    public required string Password { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>An account operation was refused; the message is safe to show.</summary>
public sealed class UserDirectoryException(string message) : Exception(message);

/// <summary>
/// Optional capability for providers that own their accounts. The host offers account management (web UI and the
/// <c>users</c> command) for the first configured provider that implements it.
/// </summary>
public interface IUserDirectory
{
    ValueTask<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <exception cref="UserDirectoryException">Invalid or duplicate name, or a password that breaks the policy.</exception>
    ValueTask<DirectoryUser> CreateUserAsync(NewDirectoryUser user, CancellationToken cancellationToken = default);

    /// <summary>Sets a new password and ends the account's other sessions.</summary>
    ValueTask SetPasswordAsync(string name, string newPassword, CancellationToken cancellationToken = default);

    ValueTask SetRolesAsync(string name, IReadOnlyList<string> roles, CancellationToken cancellationToken = default);

    /// <returns><c>false</c> when the account does not exist.</returns>
    ValueTask<bool> DeleteUserAsync(string name, CancellationToken cancellationToken = default);
}
