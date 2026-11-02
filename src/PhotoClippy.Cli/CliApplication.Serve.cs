using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhotoClippy.Cli.Serve;
using PhotoClippy.Cli.Web;
using PhotoClippy.Cli.Web.Auth;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli;

internal static partial class CliApplication
{
    private static Command BuildServeCommand()
    {
        var command = new Command("serve", "Run as a daemon with a web UI: browse statistics, view the report, rank candidate lenses and edit settings at /. Analyses on start and on a schedule; JSON API under /api/v1.");
        var urls = new Option<string?>("--urls") { Description = "Listen address(es), ';'-separated (default: serve.urls, http://0.0.0.0:8080)." };
        var rescan = new Option<double?>("--rescan-hours") { Description = "Rescan every N hours; 0 = only on start and on demand." };
        var noInitialScan = new Option<bool>("--no-initial-scan") { Description = "Do not scan at start-up." };
        command.Arguments.Add(PathsArgument);
        AddOptions(command, urls, rescan, noInitialScan);
        AddOptions(command, LibraryOptionSet);
        AddOptions(command, CandidatesOption, BodyOption, PresetOption, BudgetOption, MaxWeightOption, TopOption, OutOption, TitleOption);
        command.SetAction((parse, ct) => ExecuteAsync(
            parse,
            ct,
            (runtime, ui, token) => ServeAsync(runtime, ui, parse.GetValue(PathsArgument) ?? [], token),
            options =>
            {
                // Folders passed to serve only seed the library (see ServeAsync) so the web UI can own library.roots.
                var configuredRoots = options.Library.Roots;
                ApplyLibrary(parse, options);
                options.Library.Roots = configuredRoots;
                ApplyRecommendation(parse, options);
                ApplyReport(parse, options);
                options.Recommendation.Prompt = false;
                options.Serve.Urls = parse.GetValue(urls) ?? options.Serve.Urls;
                options.Serve.RescanHours = parse.GetValue(rescan) ?? options.Serve.RescanHours;
                if (parse.GetValue(noInitialScan))
                {
                    options.Serve.RunOnStart = false;
                }

                if (parse.GetValue(OutOption) is null && options.Report.Output == new ReportOptions().Output)
                {
                    options.Report.Output = Path.Combine(AppPaths.Resolve(options).Home, "report");
                }
            },
            logLevel: parse.GetValue(VerboseOption) ? LogLevel.Debug : parse.GetValue(QuietOption) ? LogLevel.Warning : LogLevel.Information));
        return command;
    }

    private static async Task<int> ServeAsync(AppRuntime runtime, Ui ui, IReadOnlyList<string> seedFolders, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> seedRoots = [.. seedFolders.Select(Path.GetFullPath)];
        if (runtime.Options.Library.Roots.Count == 0)
        {
            runtime.Options.Library.Roots = [.. seedRoots];
        }

        var serve = runtime.Options.Serve;
        await using var authProviders = AuthProviderSet.Load(runtime);
        var authLogger = runtime.LoggerFactory.CreateLogger("PhotoClippy.Auth");
        var contentRoot = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot"),
        });
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls(serve.Urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(runtime.LoggerFactory);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(runtime.Paths.Home, "keys")));
        builder.Services.AddPhotoClippyAuth(authProviders, runtime.Options.Auth, authLogger);
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(o => o.DetailedErrors = runtime.LoggerFactory.CreateLogger("PhotoClippy.Web").IsEnabled(LogLevel.Debug));

        using var coordinator = new ScanCoordinator(runtime, runtime.LoggerFactory.CreateLogger<ScanCoordinator>());
        using var scheduler = new ScanScheduler(runtime, coordinator, runtime.LoggerFactory.CreateLogger<ScanScheduler>());
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(scheduler);
        builder.Services.AddSingleton(sp => new SettingsService(
            runtime,
            coordinator,
            scheduler,
            seedRoots,
            runtime.LoggerFactory.CreateLogger<SettingsService>(),
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new FolderBrowser(() => settings.BrowseRoots);
        });
        builder.Services.AddSingleton(new ModelService(runtime, coordinator, runtime.LoggerFactory.CreateLogger<ModelService>()));
        builder.Services.AddSingleton(_ => new LensCatalogService(runtime, coordinator, runtime.LoggerFactory.CreateLogger<LensCatalogService>()));

        await using var app = builder.Build();
        var stopping = app.Lifetime.ApplicationStopping;
        Directory.CreateDirectory(coordinator.ReportDirectory);

        app.UsePhotoClippyAuth(authLogger);
        app.RequireSignIn("/report");
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(coordinator.ReportDirectory),
            RequestPath = "/report",
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache, private",
        });
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets().AllowAnonymous();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapAuthEndpoints();

        app.MapGet("/healthz", () => Json(new { status = "ok", version = AppRuntime.ToolVersion, scan = coordinator.State.State })).AllowAnonymous();

        var api = app.MapGroup("/api/v1");
        api.MapGet("/snapshot", async (CancellationToken ct) =>
        {
            var snapshot = await runtime.Store.LoadLatestSnapshotAsync(ct).ConfigureAwait(false);
            return snapshot is null
                ? Problem(HttpStatusCode.NotFound, "No analysis yet.", "POST /api/v1/scan to start one.")
                : Json(new { snapshot.Run, snapshot.Profile, snapshot.Systems, snapshot.Owned, snapshot.Insights, snapshot.Recommendation });
        });
        api.MapGet("/scan", () => Json(coordinator.State));
        api.MapPost("/scan", () => coordinator.TryStartScan(stopping)
            ? Json(coordinator.State, StatusCodes.Status202Accepted)
            : Problem(HttpStatusCode.Conflict, "A scan is already running.", "GET /api/v1/scan or /api/v1/events for progress."));
        api.MapPost("/recommendations", async (HttpRequest request, CancellationToken ct) =>
        {
            RecommendRequest body;
            try
            {
                body = request.ContentLength is 0 or null && !request.HasJsonContentType()
                    ? new RecommendRequest()
                    : await request.ReadFromJsonAsync<RecommendRequest>(ContractJson.Options, ct).ConfigureAwait(false) ?? new RecommendRequest();
            }
            catch (JsonException ex)
            {
                return Problem(HttpStatusCode.BadRequest, $"Invalid JSON: {ex.Message}", "Body: {\"candidates\": [\"24/1.4\", \"14-24/2.8\"], \"body\": null, \"preset\": \"balanced\"}");
            }

            try
            {
                var set = await coordinator.RecommendAsync(body, ct).ConfigureAwait(false);
                return set is null
                    ? Problem(HttpStatusCode.UnprocessableEntity, "No interchangeable-lens body in the library, so there is nothing to recommend lenses for.")
                    : Json(set);
            }
            catch (BusyException ex)
            {
                return Problem(HttpStatusCode.Conflict, ex.Message);
            }
            catch (CliException ex)
            {
                return Problem(ex.ExitCode == ExitCodes.NoData ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, ex.Message, ex.Hint);
            }
        });
        api.MapGet("/plugins", async (bool? health, CancellationToken ct) =>
        {
            var (rows, notes) = await PluginInventory.CollectAsync(runtime, health ?? false, ct).ConfigureAwait(false);
            return Json(new { plugins = rows, notes });
        });
        api.MapGet("/lenses", async (string? q, string? maker, string? mount, string? body, int? limit, LensCatalogService lenses, CancellationToken ct) =>
        {
            var catalog = await lenses.GetAsync(ct).ConfigureAwait(false);
            if (catalog is null)
            {
                return Problem(HttpStatusCode.NotFound, lenses.Problem ?? "No lens database.");
            }

            var info = await catalog.GetInfoAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(mount) && !string.IsNullOrWhiteSpace(body))
            {
                mount = await catalog.MountForBodyAsync(null, body, ct).ConfigureAwait(false);
            }

            var found = await catalog.SearchAsync(new LensCatalogQuery { Text = q, Maker = maker, Mount = mount, Limit = Math.Clamp(limit ?? 50, 1, 500) }, ct).ConfigureAwait(false);
            return Json(new { info, mount, lenses = found.Select(l => new { l.Id, l.Maker, l.Model, name = l.DisplayName, l.Mounts, l.FocalMinMm, l.FocalMaxMm, l.MaxApertureWide, l.MaxApertureTele, l.Type, shorthand = l.ToShorthand() }) });
        });
        api.MapPost("/lenses/update", (LensCatalogService lenses) => lenses.TryStartUpdate(stopping)
            ? Json(new { updating = true }, StatusCodes.Status202Accepted)
            : Problem(HttpStatusCode.Conflict, "An update is already running."));
        api.MapGet("/events", (HttpContext context, CancellationToken ct) => StreamEventsAsync(context, coordinator, ct));

        var auth = app.Services.GetRequiredService<AuthService>();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        if (auth.SignInDisabled)
        {
            ui.Warn($"Sign-in is off ({AuthOptions.NoneProvider}): everyone who can reach this server is an admin.");
        }
        else if (await auth.NeedsSetupAsync(cancellationToken).ConfigureAwait(false))
        {
            auth.EnsureSetupCode();
        }

        if (!ui.Json)
        {
            ui.Success($"photo-clippy {AppRuntime.ToolVersion}: web UI on {string.Join(", ", app.Urls.Select(BrowsableUrl))}");
            ui.Info($"Library: {(runtime.Options.Library.Roots.Count == 0 ? "(none yet; add folders under Settings)" : string.Join(", ", runtime.Options.Library.Roots))}");
            ui.Info($"Config: {runtime.WritableConfigFile} · cache: {runtime.Paths.Database}");
            ui.Info(serve.RescanHours > 0 ? $"Rescanning every {serve.RescanHours:0.##} h. Ctrl+C to stop." : "Rescans on demand from the web UI. Ctrl+C to stop.");
        }

        ui.Emit("listening", new { urls = app.Urls, report = coordinator.ReportFile, config = runtime.WritableConfigFile });

        if (serve.RunOnStart && runtime.Options.Library.Roots.Count > 0)
        {
            coordinator.TryStartScan(stopping);
        }

        var schedule = scheduler.RunAsync(stopping);
        await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(schedule, coordinator.Current), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    private static string BrowsableUrl(string url) =>
        url.Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal).Replace("://[::]", "://localhost", StringComparison.Ordinal);

    private static async Task StreamEventsAsync(HttpContext context, ScanCoordinator coordinator, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        var reader = coordinator.Subscribe(out var unsubscribe);
        try
        {
            await WriteEventAsync(response, "scan.state", coordinator.State, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(heartbeat.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!more)
                {
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    await WriteEventAsync(response, item.Type, item.Data, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            unsubscribe();
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string type, object? data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data, data?.GetType() ?? typeof(object), ContractJson.Options);
        await response.WriteAsync($"event: {type}\ndata: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IResult Json(object? value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, ContractJson.Options, statusCode: statusCode);

    private static IResult Problem(HttpStatusCode status, string message, string? hint = null) =>
        Results.Json(new { error = message, hint }, ContractJson.Options, statusCode: (int)status);
}
