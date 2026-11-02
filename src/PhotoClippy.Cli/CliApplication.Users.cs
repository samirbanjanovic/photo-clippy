using System.CommandLine;
using System.Globalization;
using PhotoClippy.Cli.Web.Auth;
using PhotoClippy.Contracts;
using Spectre.Console;

namespace PhotoClippy.Cli;

internal static partial class CliApplication
{
    private static Command BuildUsersCommand()
    {
        var command = new Command("users", "Manage web UI accounts (when a sign-in method that stores accounts, such as local accounts, is configured). Changes apply to a running server within a minute.");
        var passwordStdin = new Option<bool>("--password-stdin") { Description = "Read the password from the first line of standard input instead of prompting." };

        var list = new Command("list", "Show accounts and their roles.");
        list.SetAction((parse, ct) => WithDirectoryAsync(parse, ct, async (directory, ui, token) =>
        {
            var users = (await directory.ListUsersAsync(token).ConfigureAwait(false)).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (ui.ShowHuman)
            {
                if (users.Count == 0)
                {
                    ui.Info("No accounts yet. Create the first admin with `photo-clippy users add <name> --admin`, or with the setup code the server logs on start.");
                }
                else
                {
                    var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
                        .AddColumn("User").AddColumn("Display name").AddColumn("Role").AddColumn("Created").AddColumn("Last sign-in");
                    foreach (var user in users)
                    {
                        table.AddRow(
                            Markup.Escape(user.Name),
                            Markup.Escape(user.DisplayName ?? string.Empty),
                            IsAdmin(user) ? $"[{Ui.Fjord.ToMarkup()}]admin[/]" : "viewer",
                            Date(user.CreatedUtc),
                            user.LastSignInUtc is { } last ? Date(last) : $"[{Ui.Slate.ToMarkup()}]never[/]");
                    }

                    ui.Write(table);
                }
            }

            ui.Emit("users", users.Select(ToJson));
            return ExitCodes.Ok;
        }));

        var add = new Command("add", "Create an account. The first account is always an admin.");
        var addName = new Argument<string>("name") { Description = "User name (letters, digits, . _ @ -)." };
        var admin = new Option<bool>("--admin") { Description = "Give the account the admin role (settings and account management)." };
        var displayName = new Option<string?>("--display-name") { Description = "Name shown in the web UI." };
        add.Arguments.Add(addName);
        AddOptions(add, admin, displayName, passwordStdin);
        add.SetAction((parse, ct) => WithDirectoryAsync(parse, ct, async (directory, ui, token) =>
        {
            var name = parse.GetValue(addName)!;
            var password = await ReadNewPasswordAsync(ui, parse.GetValue(passwordStdin), name, token).ConfigureAwait(false);
            var created = await directory.CreateUserAsync(new NewDirectoryUser
            {
                Name = name,
                Password = password,
                DisplayName = parse.GetValue(displayName),
                Roles = parse.GetValue(admin) ? [AuthRoles.Admin] : [],
            }, token).ConfigureAwait(false);
            ui.Success($"Created {created.Name} ({(IsAdmin(created) ? "admin" : "viewer")}).");
            ui.Emit("user", ToJson(created));
            return ExitCodes.Ok;
        }));

        var passwd = new Command("passwd", "Set a new password for an account. Signs it out everywhere.");
        var passwdName = new Argument<string>("name") { Description = "User name." };
        passwd.Arguments.Add(passwdName);
        passwd.Options.Add(passwordStdin);
        passwd.SetAction((parse, ct) => WithDirectoryAsync(parse, ct, async (directory, ui, token) =>
        {
            var user = await FindUserAsync(directory, parse.GetValue(passwdName)!, token).ConfigureAwait(false);
            var password = await ReadNewPasswordAsync(ui, parse.GetValue(passwordStdin), user.Name, token).ConfigureAwait(false);
            await directory.SetPasswordAsync(user.Name, password, token).ConfigureAwait(false);
            ui.Success($"Password changed for {user.Name}.");
            ui.Emit("user", ToJson(user));
            return ExitCodes.Ok;
        }));

        var role = new Command("admin", "Grant (or with --revoke, remove) the admin role.");
        var roleName = new Argument<string>("name") { Description = "User name." };
        var revoke = new Option<bool>("--revoke") { Description = "Make the account a viewer instead." };
        role.Arguments.Add(roleName);
        role.Options.Add(revoke);
        role.SetAction((parse, ct) => WithDirectoryAsync(parse, ct, async (directory, ui, token) =>
        {
            var user = await FindUserAsync(directory, parse.GetValue(roleName)!, token).ConfigureAwait(false);
            var makeAdmin = !parse.GetValue(revoke);
            IReadOnlyList<string> roles = makeAdmin
                ? [.. user.Roles.Append(AuthRoles.Admin).Distinct(StringComparer.Ordinal)]
                : [.. user.Roles.Where(r => r != AuthRoles.Admin)];
            await directory.SetRolesAsync(user.Name, roles, token).ConfigureAwait(false);
            ui.Success(makeAdmin ? $"{user.Name} is now an admin." : $"{user.Name} is now a viewer.");
            ui.Emit("user", ToJson(user with { Roles = roles }));
            return ExitCodes.Ok;
        }));

        var remove = new Command("remove", "Delete an account. The last admin can't be removed.");
        var removeName = new Argument<string>("name") { Description = "User name." };
        remove.Arguments.Add(removeName);
        remove.SetAction((parse, ct) => WithDirectoryAsync(parse, ct, async (directory, ui, token) =>
        {
            var name = parse.GetValue(removeName)!;
            if (!await directory.DeleteUserAsync(name, token).ConfigureAwait(false))
            {
                throw new CliException(ExitCodes.Usage, $"No account named '{name}'.", "List accounts with `photo-clippy users list`.");
            }

            ui.Success($"Removed {name}.");
            ui.Emit("removed", new { name });
            return ExitCodes.Ok;
        }));

        command.Subcommands.Add(list);
        command.Subcommands.Add(add);
        command.Subcommands.Add(passwd);
        command.Subcommands.Add(role);
        command.Subcommands.Add(remove);
        return command;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Mirrors the (parse, ct) action signature, like ExecuteAsync.")]
    private static Task<int> WithDirectoryAsync(ParseResult parse, CancellationToken cancellationToken, Func<IUserDirectory, Ui, CancellationToken, Task<int>> body) =>
        ExecuteAsync(parse, cancellationToken, async (runtime, ui, token) =>
        {
            await using var providers = AuthProviderSet.Load(runtime);
            if (providers.Directory is not { } directory)
            {
                throw new CliException(
                    ExitCodes.Usage,
                    $"None of the configured sign-in methods ({string.Join(", ", runtime.Options.Auth.Providers)}) stores accounts.",
                    $"Accounts live in your identity provider. Add \"{Core.Configuration.AuthOptions.LocalProvider}\" to auth.providers to manage them here.");
            }

            try
            {
                return await body(directory, ui, token).ConfigureAwait(false);
            }
            catch (UserDirectoryException ex)
            {
                throw new CliException(ExitCodes.Usage, ex.Message);
            }
        });

    private static async Task<DirectoryUser> FindUserAsync(IUserDirectory directory, string name, CancellationToken cancellationToken) =>
        (await directory.ListUsersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new CliException(ExitCodes.Usage, $"No account named '{name}'.", "List accounts with `photo-clippy users list`.");

    private static async Task<string> ReadNewPasswordAsync(Ui ui, bool fromStdin, string name, CancellationToken cancellationToken)
    {
        if (fromStdin)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(line) ? throw new CliException(ExitCodes.Usage, "No password on standard input.") : line;
        }

        if (!ui.CanPrompt)
        {
            throw new CliException(ExitCodes.Usage, "No password given.", "Run in a terminal to be prompted, or pipe it in with --password-stdin.");
        }

        var password = await ui.Console.PromptAsync(new TextPrompt<string>($"New password for [bold]{Markup.Escape(name)}[/]:").Secret(), cancellationToken).ConfigureAwait(false);
        var confirm = await ui.Console.PromptAsync(new TextPrompt<string>("Repeat it:").Secret(), cancellationToken).ConfigureAwait(false);
        return string.Equals(password, confirm, StringComparison.Ordinal)
            ? password
            : throw new CliException(ExitCodes.Usage, "The passwords don't match.");
    }

    private static bool IsAdmin(DirectoryUser user) => user.Roles.Contains(AuthRoles.Admin);

    private static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static object ToJson(DirectoryUser user) => new
    {
        name = user.Name,
        displayName = user.DisplayName,
        roles = user.Roles,
        admin = IsAdmin(user),
        createdUtc = user.CreatedUtc,
        lastSignInUtc = user.LastSignInUtc,
    };
}
