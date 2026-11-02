using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Cli.Web.Auth;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Plugins.Auth;

namespace PhotoClippy.Cli.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photo-clippy-auth-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Sign_in_falls_through_providers_that_do_not_know_the_user()
    {
        var first = new FakeCredentialProvider("test.first", _ => AuthResult.NoResult);
        var second = new FakeCredentialProvider("test.second", r => AuthResult.Success(User(r.Username)));
        var auth = Create([first, second]);

        var outcome = await auth.SignInAsync(" samir ", "pw", "10.0.0.5", default);

        var principal = Assert.IsType<ClaimsPrincipal>(outcome.Principal);
        Assert.Equal(SignInStatus.SignedIn, outcome.Status);
        Assert.Equal("test.second", principal.FindFirstValue(AuthService.ProviderClaim));
        Assert.Equal("samir", principal.Identity!.Name);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task A_rejection_stops_the_chain_with_a_generic_message()
    {
        var first = new FakeCredentialProvider("test.first", _ => AuthResult.Fail());
        var second = new FakeCredentialProvider("test.second", r => AuthResult.Success(User(r.Username)));
        var auth = Create([first, second]);

        var outcome = await auth.SignInAsync("samir", "pw", null, default);

        Assert.Equal(SignInStatus.Failed, outcome.Status);
        Assert.Equal("Wrong user name or password.", outcome.Message);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Missing_credentials_never_reach_a_provider()
    {
        var provider = new FakeCredentialProvider("test.only", r => AuthResult.Success(User(r.Username)));
        var auth = Create([provider]);

        Assert.Equal(SignInStatus.Failed, (await auth.SignInAsync("  ", "pw", null, default)).Status);
        Assert.Equal(SignInStatus.Failed, (await auth.SignInAsync("samir", null, null, default)).Status);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_broken_provider_is_skipped_and_reported_only_when_nobody_else_answers()
    {
        var broken = new FakeCredentialProvider("test.broken", _ => throw new InvalidOperationException("down"));

        var alone = await Create([broken, new FakeCredentialProvider("test.unknown", _ => AuthResult.NoResult)]).SignInAsync("samir", "pw", null, default);
        var covered = await Create([broken, new FakeCredentialProvider("test.ok", r => AuthResult.Success(User(r.Username)))]).SignInAsync("samir", "pw", null, default);

        Assert.Contains("unavailable", alone.Message, StringComparison.Ordinal);
        Assert.Equal(SignInStatus.SignedIn, covered.Status);
    }

    [Fact]
    public async Task Access_policy_applies_the_allow_list_and_admin_list()
    {
        var provider = new FakeCredentialProvider("test.only", r => AuthResult.Success(User(r.Username)));
        var auth = Create([provider], new AuthOptions { AllowedUsers = ["samir", "anna"], Admins = [" SAMIR "] });

        var samir = await auth.SignInAsync("samir", "pw", null, default);
        var anna = await auth.SignInAsync("anna", "pw", null, default);
        var eve = await auth.SignInAsync("eve", "pw", null, default);

        Assert.True(samir.Principal!.IsInRole(AuthRoles.Admin));
        Assert.False(anna.Principal!.IsInRole(AuthRoles.Admin));
        Assert.Equal(SignInStatus.Failed, eve.Status);
        Assert.Contains("not allowed", eve.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_failures_are_throttled_until_the_window_passes()
    {
        var provider = new FakeCredentialProvider("test.only", _ => AuthResult.Fail());
        var auth = Create([provider]);
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInAsync("samir", "wrong", "10.0.0.9", default);
        }

        var blocked = await auth.SignInAsync("Samir", "right", "10.0.0.9", default);
        _time.Advance(TimeSpan.FromMinutes(16));
        await auth.SignInAsync("samir", "right", "10.0.0.9", default);

        Assert.StartsWith("Too many attempts. Try again in 15 min", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(6, provider.Calls);
    }

    [Fact]
    public async Task Second_factor_round_trips_through_a_protected_token()
    {
        var provider = new FakeCredentialProvider("test.otp", _ => AuthResult.NeedSecondFactor(new SecondFactorChallenge("Code from your app", "state-1")))
        {
            SecondFactor = r => r is { Code: "123456", State: "state-1" } ? AuthResult.Success(User(r.Username)) : AuthResult.Fail(),
        };
        var auth = Create([provider]);

        var pending = await auth.SignInAsync("samir", "pw", null, default);
        var read = auth.ReadPendingSecondFactor(pending.PendingToken);
        var wrong = await auth.VerifySecondFactorAsync(pending.PendingToken, "000000", null, default);
        var forged = await auth.VerifySecondFactorAsync("not-a-token", "123456", null, default);
        var ok = await auth.VerifySecondFactorAsync(pending.PendingToken, " 123456 ", null, default);

        Assert.Equal(SignInStatus.SecondFactor, pending.Status);
        Assert.Equal("samir", read?.User);
        Assert.Equal("Code from your app", read?.Prompt);
        Assert.Null(auth.ReadPendingSecondFactor(pending.PendingToken + "x"));
        Assert.Equal("That code didn't work.", wrong.Message);
        Assert.Equal(SignInStatus.Failed, forged.Status);
        Assert.Equal(SignInStatus.SignedIn, ok.Status);
    }

    [Fact]
    public async Task Sessions_pick_up_role_changes_and_end_when_the_stamp_changes()
    {
        var current = User("samir", "s1");
        var provider = new FakeCredentialProvider("test.only", _ => AuthResult.Success(current)) { Refresh = _ => current };
        var auth = Create([provider]);
        var principal = (await auth.SignInAsync("samir", "pw", null, default)).Principal!;

        current = User("samir", "s1", AuthRoles.Admin);
        var promoted = await auth.ValidateAsync(principal, default);
        current = User("samir", "s2");
        var cached = await auth.ValidateAsync(principal, default);
        _time.Advance(TimeSpan.FromSeconds(61));
        var ended = await auth.ValidateAsync(principal, default);

        Assert.True(promoted!.IsInRole(AuthRoles.Admin));
        Assert.NotNull(cached);
        Assert.Null(ended);
    }

    [Fact]
    public async Task Sessions_survive_provider_outages_but_not_provider_removal()
    {
        var provider = new FakeCredentialProvider("test.only", r => AuthResult.Success(User(r.Username))) { Refresh = _ => throw new IOException("down") };
        var auth = Create([provider]);
        var principal = (await auth.SignInAsync("samir", "pw", null, default)).Principal!;
        var orphan = auth.CreatePrincipal("test.removed", User("samir"))!;

        Assert.Same(principal, await auth.ValidateAsync(principal, default));
        Assert.Null(await auth.ValidateAsync(orphan, default));
    }

    [Fact]
    public async Task No_auth_provider_turns_sign_in_off()
    {
        var auth = Create([new NoAuthProvider()]);

        var principal = await auth.AuthenticateRequestAsync(new IncomingRequest { Headers = new Dictionary<string, string>() }, default);

        Assert.True(auth.SignInDisabled);
        Assert.True(principal!.IsInRole(AuthRoles.Admin));
        Assert.Same(principal, await auth.ValidateAsync(principal, default));
    }

    [Fact]
    public async Task First_run_setup_needs_the_logged_code_and_works_once()
    {
        var auth = Create([LocalAccounts()]);

        Assert.True(await auth.NeedsSetupAsync(default));
        var code = auth.EnsureSetupCode();
        Assert.Equal(code, auth.EnsureSetupCode());

        var wrong = await auth.CompleteSetupAsync("0000-0000-0000", "samir", "first-password", "first-password", null, default);
        var mismatch = await auth.CompleteSetupAsync(code, "samir", "first-password", "other-password", null, default);
        var done = await auth.CompleteSetupAsync($" {code.Replace('-', ' ').ToLowerInvariant()} ", "samir", "first-password", "first-password", null, default);
        var again = await auth.CompleteSetupAsync(code, "eve", "eve-password", "eve-password", null, default);

        Assert.Contains("setup code is wrong", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("don't match", mismatch.Message, StringComparison.Ordinal);
        Assert.Equal(SignInStatus.SignedIn, done.Status);
        Assert.True(done.Principal!.IsInRole(AuthRoles.Admin));
        Assert.False(await auth.NeedsSetupAsync(default));
        Assert.Contains("already done", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_your_password_needs_the_current_one_and_ends_the_session()
    {
        var local = LocalAccounts();
        await local.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        var auth = Create([local]);
        var principal = (await auth.SignInAsync("samir", "first-password", null, default)).Principal!;

        Assert.True(auth.IsDirectoryAccount(principal));
        await Assert.ThrowsAsync<UserDirectoryException>(() => auth.ChangeOwnPasswordAsync(principal, "wrong-password", "second-password", "second-password", null, default));
        await Assert.ThrowsAsync<UserDirectoryException>(() => auth.ChangeOwnPasswordAsync(principal, "first-password", "second-password", "typo-password", null, default));
        await auth.ChangeOwnPasswordAsync(principal, "first-password", "second-password", "second-password", null, default);

        Assert.Null(await auth.ValidateAsync(principal, default));
        Assert.Equal(SignInStatus.SignedIn, (await auth.SignInAsync("samir", "second-password", null, default)).Status);
    }

    [Fact]
    public async Task Only_admins_manage_accounts_and_changes_reach_open_sessions()
    {
        var local = LocalAccounts();
        await local.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        await local.CreateUserAsync(new NewDirectoryUser { Name = "anna", Password = "second-password" });
        var auth = Create([local]);
        var samir = (await auth.SignInAsync("samir", "first-password", null, default)).Principal!;
        var anna = (await auth.SignInAsync("anna", "second-password", null, default)).Principal!;
        Assert.NotNull(await auth.ValidateAsync(anna, default));

        await Assert.ThrowsAsync<UserDirectoryException>(() => auth.CreateUserAsync(anna, new NewDirectoryUser { Name = "eve", Password = "third-password" }, default));
        await Assert.ThrowsAsync<UserDirectoryException>(() => auth.DeleteUserAsync(anna, "samir", default));
        var annaAccount = (await local.ListUsersAsync()).Single(u => u.Name == "anna");
        await auth.SetAdminAsync(samir, annaAccount, admin: true, default);

        Assert.True((await auth.ValidateAsync(anna, default))!.IsInRole(AuthRoles.Admin));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/settings", "/settings")]
    [InlineData("/report/index.html?x=1#y", "/report/index.html?x=1#y")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("settings", "/")]
    [InlineData("/login?returnUrl=/", "/")]
    [InlineData("/auth/logout", "/")]
    [InlineData("/a\r\nSet-Cookie: x", "/")]
    public void Return_urls_stay_on_this_site(string? url, string expected)
    {
        Assert.Equal(expected, AuthWeb.SafeReturnUrl(url));
    }

    private AuthService Create(IAuthProvider[] providers, AuthOptions? options = null)
    {
        var set = new AuthProviderSet([.. providers.Select(p => new AuthProviderEntry(p.Manifest.Id, p))]);
        var service = new AuthService(set, options ?? new AuthOptions(), new EphemeralDataProtectionProvider(), new LoginThrottle(_time), NullLogger.Instance, _time);
        _owned.Add(service);
        return service;
    }

    private LocalAccountsProvider LocalAccounts()
    {
        var provider = new LocalAccountsProvider(_directory, new LocalAccountsSettings { Iterations = 1_000 });
        _owned.Add(provider);
        return provider;
    }

    private static AuthenticatedUser User(string name, string? stamp = "s1", params string[] roles) =>
        new() { Subject = "id-" + name, Name = name, SessionStamp = stamp, Roles = roles };
}

public sealed class LoginThrottleTests
{
    [Fact]
    public void Limits_per_user_and_per_address_within_a_sliding_window()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time, perUser: 2, perAddress: 3, window: TimeSpan.FromMinutes(10));

        throttle.Failed("samir", "10.0.0.1");
        time.Advance(TimeSpan.FromMinutes(1));
        throttle.Failed(" SAMIR ", "10.0.0.1");

        Assert.Equal(TimeSpan.FromMinutes(9), throttle.RetryAfter("samir", "10.0.0.2"));
        Assert.Null(throttle.RetryAfter("anna", "10.0.0.1"));

        throttle.Failed("anna", "10.0.0.1");
        Assert.NotNull(throttle.RetryAfter("bob", "10.0.0.1"));
        Assert.Null(throttle.RetryAfter("bob", "10.0.0.2"));
        Assert.Null(throttle.RetryAfter("bob", null));

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Null(throttle.RetryAfter("samir", "10.0.0.2"));
    }

    [Fact]
    public void Success_clears_the_user_but_not_the_address()
    {
        var throttle = new LoginThrottle(new ManualTime(), perUser: 1, perAddress: 1);

        throttle.Failed("samir", "10.0.0.1");
        throttle.Succeeded("samir");

        Assert.Null(throttle.RetryAfter("samir", null));
        Assert.NotNull(throttle.RetryAfter("samir", "10.0.0.1"));
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeCredentialProvider(string id, Func<CredentialRequest, AuthResult> authenticate) : ICredentialAuthProvider
{
    public int Calls { get; private set; }

    public Func<AuthenticatedUser, AuthenticatedUser?> Refresh { get; init; } = user => user;

    public Func<SecondFactorRequest, AuthResult>? SecondFactor { get; init; }

    public PluginManifest Manifest { get; } = new() { Id = id, Name = id, Version = "1.0.0", Kind = ComponentKind.Auth };

    public ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(PluginHealth.Ok("ok"));

    public ValueTask<AuthResult> AuthenticateAsync(CredentialRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult(authenticate(request));
    }

    public ValueTask<AuthResult> VerifySecondFactorAsync(SecondFactorRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SecondFactor?.Invoke(request) ?? AuthResult.Fail());

    public ValueTask<AuthenticatedUser?> RefreshAsync(AuthenticatedUser user, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Refresh(user));
}
