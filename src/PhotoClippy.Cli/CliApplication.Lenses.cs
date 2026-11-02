using System.CommandLine;
using System.Globalization;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Plugins;
using Spectre.Console;

namespace PhotoClippy.Cli;

internal static partial class CliApplication
{
    private static Command BuildLensesCommand()
    {
        var command = new Command("lenses", "Browse the lens database (Lensfun by default) to pick candidates by maker and model instead of typing specs.");

        var update = new Command("update", "Download or refresh the lens database. The only time the lens database touches the network.");
        update.SetAction((parse, ct) => WithCatalogAsync(parse, ct, async (runtime, catalog, ui, token) =>
        {
            var info = ui.ShowHuman && ui.Console.Profile.Capabilities.Interactive
                ? await ui.Console.Status().StartAsync("Downloading lens database…", _ => catalog.RefreshAsync(token)).ConfigureAwait(false)
                : await catalog.RefreshAsync(token).ConfigureAwait(false);
            ui.Success($"{info.LensCount.ToString("N0", CultureInfo.InvariantCulture)} lenses from {info.Makers.Count} makers for {info.Mounts.Count} mounts.");
            if (info.Attribution is not null)
            {
                ui.Info(info.Attribution);
            }

            ui.Emit("lenses", info);
            return ExitCodes.Ok;
        }));

        var info = new Command("info", "Show what the lens database holds: makers, mounts and when it was updated.");
        info.SetAction((parse, ct) => WithCatalogAsync(parse, ct, async (runtime, catalog, ui, token) =>
        {
            var state = await catalog.GetInfoAsync(token).ConfigureAwait(false);
            if (ui.ShowHuman)
            {
                if (!state.Available)
                {
                    ui.Info("The lens database hasn't been downloaded yet. Run `photo-clippy lenses update`.");
                }
                else
                {
                    ui.Heading($"{catalog.Manifest.Name} · {state.LensCount.ToString("N0", CultureInfo.InvariantCulture)} lenses · updated {state.UpdatedUtc:yyyy-MM-dd}");
                    ui.Info($"Makers: {string.Join(", ", state.Makers)}");
                    ui.Info($"Mounts: {string.Join(", ", state.Mounts)}");
                    if (state.Attribution is not null)
                    {
                        ui.Info(state.Attribution);
                    }
                }
            }

            ui.Emit("lenses", state);
            return ExitCodes.Ok;
        }));

        var search = new Command("search", "Find lenses by maker, model, focal length (35, 24-70) or aperture (f/1.4). Prints candidate shorthand you can paste into --candidates.");
        var text = new Argument<string[]>("text") { Description = "Search words; all must match, e.g. `sigma 18-50` or `35 f/1.4`.", Arity = ArgumentArity.ZeroOrMore };
        var maker = new Option<string?>("--maker") { Description = "Only this maker, e.g. Fujifilm, Sigma, Viltrox." };
        var mount = new Option<string?>("--mount") { Description = "Only lenses for this mount, e.g. \"Fujifilm X\", \"Sony E\"." };
        var body = new Option<string?>("--body") { Description = "Only lenses for this camera's mount, e.g. \"Fujifilm X-T5\". Default: the body of the last scan." };
        var anyMount = new Option<bool>("--any-mount") { Description = "Don't filter by mount." };
        var limit = new Option<int>("--limit") { Description = "Maximum results.", DefaultValueFactory = _ => 40 };
        search.Arguments.Add(text);
        AddOptions(search, maker, mount, body, anyMount, limit);
        search.SetAction((parse, ct) => WithCatalogAsync(parse, ct, async (runtime, catalog, ui, token) =>
        {
            var state = await catalog.GetInfoAsync(token).ConfigureAwait(false);
            if (!state.Available)
            {
                throw new CliException(ExitCodes.NoData, "The lens database hasn't been downloaded yet.", "Run `photo-clippy lenses update`.");
            }

            var filterMount = parse.GetValue(anyMount) ? null : parse.GetValue(mount);
            if (filterMount is null && !parse.GetValue(anyMount))
            {
                var bodyName = parse.GetValue(body);
                if (bodyName is null)
                {
                    var snapshot = await runtime.Store.LoadLatestSnapshotAsync(token).ConfigureAwait(false);
                    bodyName = snapshot?.Profile.Target?.BodyDisplay;
                }

                if (bodyName is not null)
                {
                    filterMount = await catalog.MountForBodyAsync(null, bodyName, token).ConfigureAwait(false);
                    if (filterMount is null)
                    {
                        ui.Warn($"No mount known for '{bodyName}'; showing every mount. Use --mount to narrow.");
                    }
                    else if (ui.ShowHuman)
                    {
                        ui.Info($"{bodyName} → {filterMount} mount (--any-mount for all).");
                    }
                }
            }

            var found = await catalog.SearchAsync(new LensCatalogQuery
            {
                Text = string.Join(' ', parse.GetValue(text) ?? []),
                Maker = parse.GetValue(maker),
                Mount = filterMount,
                Limit = Math.Clamp(parse.GetValue(limit), 1, 1000),
            }, token).ConfigureAwait(false);

            if (ui.ShowHuman)
            {
                if (found.Count == 0)
                {
                    ui.Info("No lenses match.");
                }
                else
                {
                    var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
                        .AddColumn("Lens").AddColumn("Mount").AddColumn("Candidate shorthand");
                    foreach (var lens in found)
                    {
                        table.AddRow(Markup.Escape(lens.DisplayName), Markup.Escape(string.Join(", ", lens.Mounts)), $"[{Ui.Slate.ToMarkup()}]{Markup.Escape(lens.ToShorthand())}[/]");
                    }

                    ui.Write(table);
                    ui.Info("Lensfun has no weight or price: append e.g. 560g $1100 to a shorthand to score them.");
                }
            }

            ui.Emit("lenses", found.Select(l => new { l.Id, l.Maker, l.Model, name = l.DisplayName, l.Mounts, l.FocalMinMm, l.FocalMaxMm, l.MaxApertureWide, l.MaxApertureTele, l.Type, shorthand = l.ToShorthand() }));
            return ExitCodes.Ok;
        }));

        command.Subcommands.Add(search);
        command.Subcommands.Add(update);
        command.Subcommands.Add(info);
        return command;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Mirrors the (parse, ct) action signature, like ExecuteAsync.")]
    private static Task<int> WithCatalogAsync(ParseResult parse, CancellationToken ct, Func<AppRuntime, ILensCatalog, Ui, CancellationToken, Task<int>> action) =>
        ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            await using PluginSet plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind> { ComponentKind.LensCatalog }, checkHealth: false, token).ConfigureAwait(false);
            var catalog = plugins.LensCatalog ?? throw new CliException(
                ExitCodes.Usage,
                string.IsNullOrWhiteSpace(runtime.Options.Plugins.LensCatalog)
                    ? "No lens database is selected."
                    : $"Lens database '{runtime.Options.Plugins.LensCatalog}' is not available. {string.Join(" ", plugins.Notes)}",
                "Set plugins.lensCatalog (default builtin.lenses.lensfun); see `photo-clippy plugins`.");
            return await action(runtime, catalog, ui, token).ConfigureAwait(false);
        });
}
