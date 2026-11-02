using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Plugins.Auth;

namespace PhotoClippy.Auth.Tests;

public sealed class LocalAccountsProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-auth-tests", Guid.NewGuid().ToString("N"));
    private readonly List<LocalAccountsProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task First_account_is_always_an_admin()
    {
        var provider = Create();

        var first = await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        var second = await provider.CreateUserAsync(new NewDirectoryUser { Name = "anna", Password = "second-password" });

        Assert.Contains(AuthRoles.Admin, first.Roles);
        Assert.Empty(second.Roles);
    }

    [Fact]
    public async Task Authenticates_known_users_and_defers_unknown_ones()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password", DisplayName = "Samir" });

        var ok = await provider.AuthenticateAsync(Credentials("SAMIR", "first-password"));
        var wrong = await provider.AuthenticateAsync(Credentials("samir", "nope-nope-nope"));
        var unknown = await provider.AuthenticateAsync(Credentials("ghost", "first-password"));

        Assert.Equal(AuthStatus.Succeeded, ok.Status);
        Assert.Equal("samir", ok.User!.Name);
        Assert.Equal("Samir", ok.User.DisplayName);
        Assert.NotNull(ok.User.SessionStamp);
        Assert.Equal(AuthStatus.Failed, wrong.Status);
        Assert.Equal(AuthStatus.NoResult, unknown.Status);
        Assert.NotNull((await provider.ListUsersAsync()).Single().LastSignInUtc);
    }

    [Theory]
    [InlineData("", "long-enough-pw")]
    [InlineData("-leading-dash", "long-enough-pw")]
    [InlineData("has space", "long-enough-pw")]
    [InlineData("samir", "short")]
    [InlineData("password1", "PASSWORD1")]
    public async Task Rejects_invalid_names_and_passwords(string name, string password)
    {
        var provider = Create();

        await Assert.ThrowsAsync<UserDirectoryException>(async () => await provider.CreateUserAsync(new NewDirectoryUser { Name = name, Password = password }));
        Assert.Empty(await provider.ListUsersAsync());
    }

    [Fact]
    public async Task Names_are_unique_regardless_of_case()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });

        await Assert.ThrowsAsync<UserDirectoryException>(async () => await provider.CreateUserAsync(new NewDirectoryUser { Name = "Samir", Password = "other-password" }));
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_or_deleted()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "anna", Password = "second-password" });

        await Assert.ThrowsAsync<UserDirectoryException>(async () => await provider.SetRolesAsync("samir", []));
        await Assert.ThrowsAsync<UserDirectoryException>(async () => await provider.DeleteUserAsync("samir"));

        await provider.SetRolesAsync("anna", [AuthRoles.Admin]);
        await provider.SetRolesAsync("samir", []);
        Assert.True(await provider.DeleteUserAsync("samir"));
        Assert.False(await provider.DeleteUserAsync("samir"));
        Assert.Equal(["anna"], (await provider.ListUsersAsync()).Select(u => u.Name));
    }

    [Fact]
    public async Task Changing_a_password_rotates_the_session_stamp()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        var before = (await provider.AuthenticateAsync(Credentials("samir", "first-password"))).User!;

        await provider.SetPasswordAsync("samir", "second-password");

        var refreshed = await provider.RefreshAsync(before);
        Assert.NotNull(refreshed);
        Assert.NotEqual(before.SessionStamp, refreshed.SessionStamp);
        Assert.Equal(AuthStatus.Failed, (await provider.AuthenticateAsync(Credentials("samir", "first-password"))).Status);
        Assert.Equal(AuthStatus.Succeeded, (await provider.AuthenticateAsync(Credentials("samir", "second-password"))).Status);
    }

    [Fact]
    public async Task Role_changes_keep_the_stamp_but_show_up_on_refresh()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "anna", Password = "second-password" });
        var anna = (await provider.AuthenticateAsync(Credentials("anna", "second-password"))).User!;

        await provider.SetRolesAsync("anna", [" Admin "]);

        var refreshed = await provider.RefreshAsync(anna);
        Assert.Equal(anna.SessionStamp, refreshed!.SessionStamp);
        Assert.Equal([AuthRoles.Admin], refreshed.Roles);
    }

    [Fact]
    public async Task Sees_changes_made_by_another_instance()
    {
        var server = Create();
        var cli = Create();
        await cli.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        await cli.CreateUserAsync(new NewDirectoryUser { Name = "anna", Password = "second-password" });

        var anna = (await server.AuthenticateAsync(Credentials("anna", "second-password"))).User!;
        Assert.True(await cli.DeleteUserAsync("anna"));

        Assert.Null(await server.RefreshAsync(anna));
        Assert.Equal(AuthStatus.NoResult, (await server.AuthenticateAsync(Credentials("anna", "second-password"))).Status);
    }

    [Fact]
    public async Task Weak_hashes_are_upgraded_at_sign_in()
    {
        await Create().CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });
        var stronger = Create(iterations: 2_000);

        Assert.Equal(AuthStatus.Succeeded, (await stronger.AuthenticateAsync(Credentials("samir", "first-password"))).Status);

        Assert.Contains("pbkdf2-sha256$2000$", StoredHash(stronger), StringComparison.Ordinal);
        Assert.Equal(AuthStatus.Succeeded, (await Create().AuthenticateAsync(Credentials("samir", "first-password"))).Status);
    }

    [Fact]
    public async Task Stores_only_hashes_in_a_private_file()
    {
        var provider = Create();
        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });

        var text = await File.ReadAllTextAsync(provider.FilePath);
        Assert.DoesNotContain("first-password", text, StringComparison.Ordinal);
        Assert.Contains("pbkdf2-sha256$1000$", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(provider.FilePath));
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(provider.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Health_reports_the_account_count()
    {
        var provider = Create();
        Assert.True((await provider.CheckHealthAsync()).Healthy);

        await provider.CreateUserAsync(new NewDirectoryUser { Name = "samir", Password = "first-password" });

        Assert.StartsWith("1 account", (await provider.CheckHealthAsync()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_auth_provider_signs_everyone_in_as_admin()
    {
        var provider = new NoAuthProvider();

        var result = await provider.AuthenticateRequestAsync(new IncomingRequest { Headers = new Dictionary<string, string>() });

        Assert.Equal(AuthStatus.Succeeded, result.Status);
        Assert.Contains(AuthRoles.Admin, result.User!.Roles);
    }

    private LocalAccountsProvider Create(int iterations = 1_000)
    {
        var provider = new LocalAccountsProvider(_dir, new LocalAccountsSettings { Iterations = iterations });
        _providers.Add(provider);
        return provider;
    }

    private static CredentialRequest Credentials(string name, string password) => new() { Username = name, Password = password };

    private static string StoredHash(LocalAccountsProvider provider)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(provider.FilePath));
        return doc.RootElement.GetProperty("users")[0].GetProperty("passwordHash").GetString()!;
    }
}
