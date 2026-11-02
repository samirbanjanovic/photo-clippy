using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli.Web.Auth;

internal enum SignInStatus
{
    SignedIn,
    SecondFactor,
    Failed,
}

internal sealed record SignInOutcome(SignInStatus Status, ClaimsPrincipal? Principal = null, string? Message = null, string? PendingToken = null, string? Prompt = null)
{
    public static SignInOutcome Ok(ClaimsPrincipal principal) => new(SignInStatus.SignedIn, principal);
    public static SignInOutcome Fail(string message) => new(SignInStatus.Failed, Message: message);
    public static SignInOutcome Pending(string token, string prompt) => new(SignInStatus.SecondFactor, PendingToken: token, Prompt: prompt);
}

internal sealed record ExternalChallenge(Uri RedirectUri, string ProtectedState);

/// <summary>
/// Host side of sign-in. Runs the configured auth plugins in order, applies the access policy (allow-list, admin
/// list), builds session principals and re-checks them with their provider about once a minute.
/// </summary>
internal sealed class AuthService : IDisposable
{
    public const string Scheme = "clippy";
    public const string ProviderClaim = "pa:provider";
    public const string SubjectClaim = "pa:sub";
    public const string DisplayNameClaim = "pa:display";
    public const string StampClaim = "pa:stamp";
    public const string FlowClaim = "pa:flow";
    public const string RequestFlow = "request";
    private const string SessionFlow = "session";
    private const string GenericFailure = "Wrong user name or password.";
    private const string SetupThrottleKey = "#setup";
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SecondFactorLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExternalLifetime = TimeSpan.FromMinutes(10);

    private readonly AuthProviderSet _providers;
    private readonly LoginThrottle _throttle;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ITimeLimitedDataProtector _secondFactorProtector;
    private readonly ITimeLimitedDataProtector _externalProtector;
    private readonly ConcurrentDictionary<string, (ClaimsPrincipal? Principal, DateTimeOffset Expires)> _validated = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private readonly Lock _setupCodeLock = new();
    private string? _setupCode;

    public AuthService(AuthProviderSet providers, AuthOptions options, IDataProtectionProvider dataProtection, LoginThrottle throttle, ILogger logger, TimeProvider? time = null)
    {
        _providers = providers;
        Options = options;
        _throttle = throttle;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _secondFactorProtector = dataProtection.CreateProtector("PhotoClippy.Auth.SecondFactor").ToTimeLimitedDataProtector();
        _externalProtector = dataProtection.CreateProtector("PhotoClippy.Auth.External").ToTimeLimitedDataProtector();
    }

    public AuthOptions Options { get; }

    public IReadOnlyList<AuthProviderEntry> Providers => _providers.Providers;

    public AuthProviderEntry? DirectoryProvider => _providers.DirectoryEntry;

    public IUserDirectory? Directory => _providers.Directory;

    public bool HasCredentialProviders => Providers.Any(p => p.Provider is ICredentialAuthProvider);

    public bool HasRequestProviders => Providers.Any(p => p.Provider is IRequestAuthProvider);

    public IEnumerable<AuthProviderEntry> RedirectProviders => Providers.Where(p => p.Provider is IRedirectAuthProvider);

    /// <summary>True when sign-in is turned off (<c>builtin.auth.none</c> is configured).</summary>
    public bool SignInDisabled => Providers.Any(p => string.Equals(p.Id, AuthOptions.NoneProvider, StringComparison.OrdinalIgnoreCase));

    // ---- Password (+ second factor) -------------------------------------------------------------------------------

    public async Task<SignInOutcome> SignInAsync(string? username, string? password, string? remoteAddress, CancellationToken cancellationToken)
    {
        username = username?.Trim() ?? string.Empty;
        password ??= string.Empty;
        if (username.Length == 0 || password.Length == 0)
        {
            return SignInOutcome.Fail("Enter your user name and password.");
        }

        if (_throttle.RetryAfter(username, remoteAddress) is { } wait)
        {
            _logger.LogWarning("Sign-in for {User} from {Address} throttled", username, remoteAddress);
            return SignInOutcome.Fail(TooManyAttempts(wait));
        }

        var unavailable = false;
        foreach (var entry in Providers)
        {
            if (entry.Provider is not ICredentialAuthProvider provider)
            {
                continue;
            }

            AuthResult result;
            try
            {
                result = await provider.AuthenticateAsync(new CredentialRequest { Username = username, Password = password, RemoteAddress = remoteAddress }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Auth plugin {Provider} failed while signing in {User}", entry.Id, username);
                unavailable = true;
                continue;
            }

            switch (result.Status)
            {
                case AuthStatus.Succeeded when result.User is not null:
                    return Complete(entry, result.User, username, remoteAddress);
                case AuthStatus.SecondFactorRequired when result.Challenge is not null:
                    return SignInOutcome.Pending(ProtectSecondFactor(entry.Id, username, result.Challenge), result.Challenge.Prompt);
                case AuthStatus.NoResult:
                    continue;
                default:
                    return Failed(entry.Id, username, remoteAddress, result.Message);
            }
        }

        return unavailable
            ? SignInOutcome.Fail("Sign-in is unavailable right now. Try again shortly.")
            : Failed(null, username, remoteAddress, null);
    }

    public async Task<SignInOutcome> VerifySecondFactorAsync(string? pendingToken, string? code, string? remoteAddress, CancellationToken cancellationToken)
    {
        if (UnprotectSecondFactor(pendingToken) is not { } pending)
        {
            return SignInOutcome.Fail("That sign-in took too long. Start again.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return SignInOutcome.Fail("Enter the code.");
        }

        if (_throttle.RetryAfter(pending.User, remoteAddress) is { } wait)
        {
            return SignInOutcome.Fail(TooManyAttempts(wait));
        }

        if (Find(pending.Provider) is not { Provider: ICredentialAuthProvider provider } entry)
        {
            return SignInOutcome.Fail("That sign-in took too long. Start again.");
        }

        var result = await provider.VerifySecondFactorAsync(
            new SecondFactorRequest { Username = pending.User, Code = code.Trim(), State = pending.State, RemoteAddress = remoteAddress },
            cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            AuthStatus.Succeeded when result.User is not null => Complete(entry, result.User, pending.User, remoteAddress),
            AuthStatus.SecondFactorRequired when result.Challenge is not null =>
                SignInOutcome.Pending(ProtectSecondFactor(entry.Id, pending.User, result.Challenge), result.Challenge.Prompt),
            _ => Failed(entry.Id, pending.User, remoteAddress, result.Message ?? "That code didn't work."),
        };
    }

    /// <summary>User and prompt of a pending second factor, or <c>null</c> when the token is missing or expired.</summary>
    public (string User, string Prompt)? ReadPendingSecondFactor(string? pendingToken) =>
        UnprotectSecondFactor(pendingToken) is { } pending ? (pending.User, pending.Prompt) : null;

    // ---- Redirect (OIDC and similar) ------------------------------------------------------------------------------

    public async Task<ExternalChallenge?> BeginExternalAsync(string providerId, Uri callbackUri, string returnUrl, CancellationToken cancellationToken)
    {
        if (Find(providerId) is not { Provider: IRedirectAuthProvider provider } entry)
        {
            return null;
        }

        var challenge = await provider.BeginAsync(new RedirectRequest { CallbackUri = callbackUri }, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new ExternalState(entry.Id, challenge.State, returnUrl));
        return new ExternalChallenge(challenge.RedirectUri, _externalProtector.Protect(payload, ExternalLifetime));
    }

    public async Task<(SignInOutcome Outcome, string ReturnUrl)> CompleteExternalAsync(
        string providerId, string? protectedState, Uri callbackUri, IReadOnlyDictionary<string, string> parameters, string? remoteAddress, CancellationToken cancellationToken)
    {
        ExternalState? state = null;
        try
        {
            state = protectedState is null ? null : JsonSerializer.Deserialize<ExternalState>(_externalProtector.Unprotect(protectedState));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
        }

        if (state is null || !string.Equals(state.Provider, providerId, StringComparison.OrdinalIgnoreCase)
            || Find(state.Provider) is not { Provider: IRedirectAuthProvider provider } entry)
        {
            return (SignInOutcome.Fail("That sign-in took too long or was started elsewhere. Start again."), "/");
        }

        AuthResult result;
        try
        {
            result = await provider.CompleteAsync(new RedirectCallback { CallbackUri = callbackUri, State = state.State, Parameters = parameters }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Auth plugin {Provider} failed to complete a sign-in", entry.Id);
            return (SignInOutcome.Fail($"{entry.DisplayName} sign-in failed. Try again."), state.ReturnUrl);
        }

        var outcome = result is { Status: AuthStatus.Succeeded, User: not null }
            ? Complete(entry, result.User, result.User.Name, remoteAddress)
            : SignInOutcome.Fail(result.Message ?? $"{entry.DisplayName} did not sign you in.");
        return (outcome, state.ReturnUrl);
    }

    // ---- Per-request identity (forward auth, no sign-in) ----------------------------------------------------------

    public async Task<ClaimsPrincipal?> AuthenticateRequestAsync(IncomingRequest request, CancellationToken cancellationToken)
    {
        foreach (var entry in Providers)
        {
            if (entry.Provider is not IRequestAuthProvider provider)
            {
                continue;
            }

            var result = await provider.AuthenticateRequestAsync(request, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case AuthStatus.Succeeded when result.User is not null:
                    return CreatePrincipal(entry.Id, result.User, RequestFlow);
                case AuthStatus.NoResult:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    // ---- Session revalidation -------------------------------------------------------------------------------------

    /// <summary>
    /// The current principal for a session (roles may have changed), or <c>null</c> when it must end: the account is
    /// gone, its password changed, its provider was removed from the config or the allow-list no longer admits it.
    /// </summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.FindFirstValue(FlowClaim) == RequestFlow)
        {
            return principal;
        }

        var providerId = principal.FindFirstValue(ProviderClaim);
        var subject = principal.FindFirstValue(SubjectClaim);
        var name = principal.FindFirstValue(ClaimTypes.Name);
        if (providerId is null || subject is null || name is null || Find(providerId) is not { } entry)
        {
            return null;
        }

        var stamp = principal.FindFirstValue(StampClaim);
        var key = string.Join('\n', entry.Id, subject, stamp);
        var now = _time.GetUtcNow();
        if (_validated.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.Principal;
        }

        AuthenticatedUser? current;
        try
        {
            current = await entry.Provider.RefreshAsync(ToUser(principal, subject, name, stamp), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider outage should not sign everyone out; try again on the next request.
            _logger.LogWarning(ex, "Auth plugin {Provider} could not refresh {User}; keeping the session for now", entry.Id, name);
            return principal;
        }

        var result = current is null || !string.Equals(current.SessionStamp, stamp, StringComparison.Ordinal)
            ? null
            : CreatePrincipal(entry.Id, current, SessionFlow);
        if (_validated.Count > 1_000)
        {
            foreach (var (k, v) in _validated)
            {
                if (v.Expires <= now)
                {
                    _validated.TryRemove(k, out _);
                }
            }
        }

        _validated[key] = (result, now + RevalidateEvery);
        return result;
    }

    /// <summary>Forgets cached session checks so account changes apply on the next request.</summary>
    public void InvalidateSessions() => _validated.Clear();

    public void Dispose() => _setupGate.Dispose();

    // ---- First-run setup ------------------------------------------------------------------------------------------

    public async Task<bool> NeedsSetupAsync(CancellationToken cancellationToken) =>
        Directory is { } directory && (await directory.ListUsersAsync(cancellationToken).ConfigureAwait(false)).Count == 0;

    /// <summary>The one-time code that authorises creating the first admin; logged when first generated.</summary>
    public string EnsureSetupCode()
    {
        lock (_setupCodeLock)
        {
            if (_setupCode is null)
            {
                const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
                var chars = RandomNumberGenerator.GetItems<char>(alphabet, 12);
                _setupCode = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
                _logger.LogWarning(
                    "No accounts yet. Open the web UI and create the first admin with setup code {SetupCode} (or run `photo-clippy users add <name> --admin`).",
                    _setupCode);
            }

            return _setupCode;
        }
    }

    public async Task<SignInOutcome> CompleteSetupAsync(string? code, string? username, string? password, string? confirm, string? remoteAddress, CancellationToken cancellationToken)
    {
        if (Directory is not { } directory)
        {
            return SignInOutcome.Fail("None of the configured sign-in methods manages accounts.");
        }

        if (_throttle.RetryAfter(SetupThrottleKey, remoteAddress) is { } wait)
        {
            return SignInOutcome.Fail(TooManyAttempts(wait));
        }

        username = username?.Trim() ?? string.Empty;
        if (Options.AllowedUsers.Count > 0 && !Contains(Options.AllowedUsers, username))
        {
            return SignInOutcome.Fail($"'{username}' is not in auth.allowedUsers, so it could not sign in.");
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            return SignInOutcome.Fail("The passwords don't match.");
        }

        await _setupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await NeedsSetupAsync(cancellationToken).ConfigureAwait(false))
            {
                return SignInOutcome.Fail("Setup is already done. Sign in instead.");
            }

            string expected;
            lock (_setupCodeLock)
            {
                expected = _setupCode ?? string.Empty;
            }

            if (expected.Length == 0 || !FixedTimeEquals(NormalizeCode(code), NormalizeCode(expected)))
            {
                _throttle.Failed(SetupThrottleKey, remoteAddress);
                _logger.LogWarning("Wrong setup code from {Address}", remoteAddress);
                return SignInOutcome.Fail("That setup code is wrong. It is in the server log (docker logs photo-clippy).");
            }

            try
            {
                await directory.CreateUserAsync(new NewDirectoryUser { Name = username, Password = password ?? string.Empty, Roles = [AuthRoles.Admin] }, cancellationToken).ConfigureAwait(false);
            }
            catch (UserDirectoryException ex)
            {
                return SignInOutcome.Fail(ex.Message);
            }

            lock (_setupCodeLock)
            {
                _setupCode = null;
            }

            _logger.LogInformation("First admin {User} created from {Address}", username, remoteAddress);
        }
        finally
        {
            _setupGate.Release();
        }

        return await SignInAsync(username, password, remoteAddress, cancellationToken).ConfigureAwait(false);
    }

    // ---- Account management ---------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="user"/> signed in through the provider that owns the account directory.</summary>
    public bool IsDirectoryAccount(ClaimsPrincipal user) =>
        DirectoryProvider is { } directory && string.Equals(user.FindFirstValue(ProviderClaim), directory.Id, StringComparison.OrdinalIgnoreCase);

    public async Task ChangeOwnPasswordAsync(ClaimsPrincipal user, string? current, string? next, string? confirm, string? remoteAddress, CancellationToken cancellationToken)
    {
        if (!IsDirectoryAccount(user) || DirectoryProvider?.Provider is not ICredentialAuthProvider provider || Directory is not { } directory)
        {
            throw new UserDirectoryException("Your password is managed by your sign-in provider.");
        }

        var name = user.Identity?.Name ?? string.Empty;
        if (!string.Equals(next, confirm, StringComparison.Ordinal))
        {
            throw new UserDirectoryException("The new passwords don't match.");
        }

        if (_throttle.RetryAfter(name, remoteAddress) is { } wait)
        {
            throw new UserDirectoryException(TooManyAttempts(wait));
        }

        var check = await provider.AuthenticateAsync(new CredentialRequest { Username = name, Password = current ?? string.Empty, RemoteAddress = remoteAddress }, cancellationToken).ConfigureAwait(false);
        var proven = check.Status == AuthStatus.SecondFactorRequired
            || (check.Status == AuthStatus.Succeeded && check.User?.Subject == user.FindFirstValue(SubjectClaim));
        if (!proven)
        {
            _throttle.Failed(name, remoteAddress);
            throw new UserDirectoryException("Your current password is wrong.");
        }

        await directory.SetPasswordAsync(name, next ?? string.Empty, cancellationToken).ConfigureAwait(false);
        InvalidateSessions();
        _logger.LogInformation("{User} changed their password", name);
    }

    public async Task<DirectoryUser> CreateUserAsync(ClaimsPrincipal actor, NewDirectoryUser user, CancellationToken cancellationToken)
    {
        var directory = RequireAdmin(actor);
        var created = await directory.CreateUserAsync(user, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Actor} created account {User}", actor.Identity?.Name, created.Name);
        return created;
    }

    public async Task ResetPasswordAsync(ClaimsPrincipal actor, string name, string password, CancellationToken cancellationToken)
    {
        await RequireAdmin(actor).SetPasswordAsync(name, password, cancellationToken).ConfigureAwait(false);
        InvalidateSessions();
        _logger.LogInformation("{Actor} reset the password of {User}", actor.Identity?.Name, name);
    }

    public async Task SetAdminAsync(ClaimsPrincipal actor, DirectoryUser user, bool admin, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles = admin
            ? [.. user.Roles.Append(AuthRoles.Admin).Distinct(StringComparer.Ordinal)]
            : [.. user.Roles.Where(r => r != AuthRoles.Admin)];
        await RequireAdmin(actor).SetRolesAsync(user.Name, roles, cancellationToken).ConfigureAwait(false);
        InvalidateSessions();
        _logger.LogInformation("{Actor} {Change} admin for {User}", actor.Identity?.Name, admin ? "granted" : "revoked", user.Name);
    }

    public async Task<bool> DeleteUserAsync(ClaimsPrincipal actor, string name, CancellationToken cancellationToken)
    {
        var removed = await RequireAdmin(actor).DeleteUserAsync(name, cancellationToken).ConfigureAwait(false);
        InvalidateSessions();
        _logger.LogInformation("{Actor} deleted account {User}", actor.Identity?.Name, name);
        return removed;
    }

    /// <summary>Where to send the browser after signing out, if the user's provider has single sign-out.</summary>
    public async Task<Uri?> GetSignOutUriAsync(ClaimsPrincipal user, Uri returnUri, CancellationToken cancellationToken)
    {
        if (user.FindFirstValue(ProviderClaim) is not { } providerId || Find(providerId) is not { } entry
            || user.FindFirstValue(SubjectClaim) is not { } subject || user.Identity?.Name is not { } name)
        {
            return null;
        }

        try
        {
            return await entry.Provider.GetSignOutUriAsync(ToUser(user, subject, name, user.FindFirstValue(StampClaim)), returnUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Auth plugin {Provider} failed to build a sign-out URL", entry.Id);
            return null;
        }
    }

    // ---- Principal mapping ----------------------------------------------------------------------------------------

    /// <summary>Applies the access policy and maps the user to claims; <c>null</c> when the allow-list refuses them.</summary>
    public ClaimsPrincipal? CreatePrincipal(string providerId, AuthenticatedUser user, string flow = SessionFlow)
    {
        if (Options.AllowedUsers.Count > 0 && !Contains(Options.AllowedUsers, user.Name))
        {
            return null;
        }

        var roles = user.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (Contains(Options.Admins, user.Name) && !roles.Contains(AuthRoles.Admin))
        {
            roles.Add(AuthRoles.Admin);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"{providerId}|{user.Subject}"),
            new(ProviderClaim, providerId),
            new(SubjectClaim, user.Subject),
            new(ClaimTypes.Name, user.Name),
            new(FlowClaim, flow),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim(DisplayNameClaim, user.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (user.SessionStamp is not null)
        {
            claims.Add(new Claim(StampClaim, user.SessionStamp));
        }

        claims.AddRange(roles.Distinct(StringComparer.Ordinal).Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static string DisplayName(ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayNameClaim) ?? user.Identity?.Name ?? "unknown";

    public string ProviderName(ClaimsPrincipal user) =>
        user.FindFirstValue(ProviderClaim) is { } id && Find(id) is { } entry ? entry.DisplayName : user.FindFirstValue(ProviderClaim) ?? "unknown";

    private AuthProviderEntry? Find(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private IUserDirectory RequireAdmin(ClaimsPrincipal actor)
    {
        if (!actor.IsInRole(AuthRoles.Admin))
        {
            throw new UserDirectoryException("Only admins can manage accounts.");
        }

        return Directory ?? throw new UserDirectoryException("None of the configured sign-in methods manages accounts.");
    }

    private SignInOutcome Complete(AuthProviderEntry entry, AuthenticatedUser user, string attemptedName, string? remoteAddress)
    {
        var principal = CreatePrincipal(entry.Id, user);
        if (principal is null)
        {
            _logger.LogWarning("{User} ({Provider}) signed in from {Address} but is not in auth.allowedUsers", user.Name, entry.Id, remoteAddress);
            return SignInOutcome.Fail("This account is not allowed to use photo-clippy.");
        }

        _throttle.Succeeded(attemptedName);
        _logger.LogInformation("{User} signed in with {Provider} from {Address}", user.Name, entry.Id, remoteAddress);
        return SignInOutcome.Ok(principal);
    }

    private SignInOutcome Failed(string? providerId, string username, string? remoteAddress, string? message)
    {
        _throttle.Failed(username, remoteAddress);
        _logger.LogWarning("Failed sign-in for {User} from {Address} ({Provider})", username, remoteAddress, providerId ?? "no provider knows the user");
        return SignInOutcome.Fail(message ?? GenericFailure);
    }

    private string ProtectSecondFactor(string providerId, string username, SecondFactorChallenge challenge) =>
        _secondFactorProtector.Protect(JsonSerializer.Serialize(new PendingSecondFactor(providerId, username, challenge.State, challenge.Prompt)), SecondFactorLifetime);

    private PendingSecondFactor? UnprotectSecondFactor(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingSecondFactor>(_secondFactorProtector.Unprotect(token));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static AuthenticatedUser ToUser(ClaimsPrincipal principal, string subject, string name, string? stamp) => new()
    {
        Subject = subject,
        Name = name,
        DisplayName = principal.FindFirstValue(DisplayNameClaim),
        Email = principal.FindFirstValue(ClaimTypes.Email),
        Roles = [.. principal.FindAll(ClaimTypes.Role).Select(c => c.Value)],
        SessionStamp = stamp,
    };

    private static bool Contains(IEnumerable<string> names, string name) =>
        names.Any(n => string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeCode(string? code) =>
        new([.. (code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string TooManyAttempts(TimeSpan wait) =>
        $"Too many attempts. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min.";

    private sealed record PendingSecondFactor(string Provider, string User, string State, string Prompt);

    private sealed record ExternalState(string Provider, string State, string ReturnUrl);
}
