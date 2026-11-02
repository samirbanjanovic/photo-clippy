using System.Text.Json;

namespace PhotoClippy.Cli.Tests;

public sealed class UsersCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public UsersCommandTests()
    {
        _cli.Options.Plugins.Settings["builtin.auth.local"] = JsonSerializer.SerializeToElement(new { iterations = 1_000 });
        _cli.SaveConfig();
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Manages_local_accounts()
    {
        var first = await RunWithInputAsync("first-password\n", "users", "add", "alice", "--display-name", "Alice B", "--password-stdin");
        var second = await RunWithInputAsync("second-password\n", "users", "add", "anna", "--password-stdin");
        var list = await _cli.RunAsync("users", "list");

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Created alice (admin)", first.All, StringComparison.Ordinal);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Created anna (viewer)", second.All, StringComparison.Ordinal);
        Assert.Equal(0, list.ExitCode);
        Assert.Matches(@"anna\s+viewer", list.Out);
        Assert.Matches(@"alice\s+Alice B\s+admin", list.Out);
        Assert.Single(Directory.EnumerateFiles(_cli.Home, "users.json", SearchOption.AllDirectories));

        var lastAdmin = await _cli.RunAsync("users", "admin", "alice", "--revoke");
        Assert.Equal(2, lastAdmin.ExitCode);
        Assert.Contains("last admin", lastAdmin.All, StringComparison.Ordinal);

        Assert.Equal(0, (await _cli.RunAsync("users", "admin", "anna")).ExitCode);
        Assert.Equal(0, (await RunWithInputAsync("third-password\n", "users", "passwd", "anna", "--password-stdin")).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("users", "remove", "alice")).ExitCode);
        Assert.NotEqual(0, (await _cli.RunAsync("users", "remove", "alice")).ExitCode);

        var remaining = await _cli.RunAsync("users", "list");
        Assert.Contains("anna", remaining.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", remaining.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_missing_or_weak_passwords()
    {
        var noTerminal = await _cli.RunAsync("users", "add", "alice");
        var tooShort = await RunWithInputAsync("short\n", "users", "add", "alice", "--password-stdin");
        var empty = await RunWithInputAsync(string.Empty, "users", "add", "alice", "--password-stdin");

        Assert.Equal(2, noTerminal.ExitCode);
        Assert.Contains("--password-stdin", noTerminal.All, StringComparison.Ordinal);
        Assert.Equal(2, tooShort.ExitCode);
        Assert.Contains("at least 8", tooShort.All, StringComparison.Ordinal);
        Assert.NotEqual(0, empty.ExitCode);
        Assert.Contains("No accounts", (await _cli.RunAsync("users", "list")).All, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CliResult> RunWithInputAsync(string input, params string[] args)
    {
        var original = Console.In;
        Console.SetIn(new StringReader(input));
        try
        {
            return await _cli.RunAsync(args);
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}
