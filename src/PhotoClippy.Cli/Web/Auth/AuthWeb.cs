using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Configuration;

namespace PhotoClippy.Cli.Web.Auth;

/// <summary>A one-shot message for the sign-in page, carried across a redirect in a short-lived cookie.</summary>
public sealed record Flash(string? Error = null, string? Notice = null, string? User = null);

/// <summary>Cookie sessions, forwarded headers, per-request providers and the <c>/auth/*</c> endpoints.</summary>
internal static class AuthWeb
{
    public const string LoginPath = "/login";
    public const string SessionCookie = "clippy_session";
    public const string FlashCookie = "clippy_flash";
    public const string SecondFactorCookie = "clippy_2fa";
    public const string ExternalCookie = "clippy_ext";
    private const string CallbackPath = "/auth/callback";
    private const string PeerAddressKey = "pa:peer";
    private const string ExpiredForm = "The form expired. Try again.";

    public static void AddPhotoClippyAuth(this IServiceCollection services, AuthProviderSet providers, AuthOptions options, ILogger logger)
    {
        services.AddSingleton(sp => new AuthService(providers, options, sp.GetRequiredService<IDataProtectionProvider>(), new LoginThrottle(), logger));
        services.AddAuthentication(AuthService.Scheme).AddCookie(AuthService.Scheme, o =>
        {
            o.Cookie.Name = SessionCookie;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(options.SessionHours, 0.25, 24 * 365));
            o.SlidingExpiration = true;
            o.LoginPath = LoginPath;
            o.AccessDeniedPath = "/access";
            o.ReturnUrlParameter = "returnUrl";
            o.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    if (context.Principal is null)
                    {
                        return;
                    }

                    var auth = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                    var current = await auth.ValidateAsync(context.Principal, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (current is null)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(AuthService.Scheme).ConfigureAwait(false);
                    }
                    else if (Signature(current) != Signature(context.Principal))
                    {
                        context.ReplacePrincipal(current);
                        context.ShouldRenew = true;
                    }
                },
                OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden),
            };
        });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();
    }

    /// <summary>Forwarded headers from trusted proxies, the session cookie, then per-request providers.</summary>
    public static void UsePhotoClippyAuth(this WebApplication app, ILogger logger)
    {
        var auth = app.Services.GetRequiredService<AuthService>();
        app.Use((context, next) =>
        {
            context.Items[PeerAddressKey] = context.Connection.RemoteIpAddress?.ToString();
            return next(context);
        });
        app.UseForwardedHeaders(ForwardedOptions(auth.Options.TrustedProxies, logger));
        app.UseAuthentication();
        if (auth.HasRequestProviders)
        {
            app.Use(async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated != true
                    && await auth.AuthenticateRequestAsync(ToIncomingRequest(context), context.RequestAborted).ConfigureAwait(false) is { } principal)
                {
                    context.User = principal;
                }

                await next(context).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Challenges anonymous requests under <paramref name="path"/> (for middleware such as static files that run before endpoint authorization).</summary>
    public static void RequireSignIn(this WebApplication app, PathString path) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(path) && context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(AuthService.Scheme).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.ServiceProvider.GetRequiredService<AuthService>();
        var group = app.MapGroup("/auth").AllowAnonymous();

        group.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
            var returnUrl = SafeReturnUrl(form?["returnUrl"].ToString());
            if (form is null)
            {
                return BackToLogin(context, returnUrl, new Flash(Error: ExpiredForm));
            }

            var user = form["username"].ToString();
            var outcome = await auth.SignInAsync(user, form["password"].ToString(), RemoteAddress(context), context.RequestAborted).ConfigureAwait(false);
            return await FinishAsync(context, outcome, returnUrl, user).ConfigureAwait(false);
        });

        group.MapPost("/second-factor", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
            var returnUrl = SafeReturnUrl(form?["returnUrl"].ToString());
            if (form is null)
            {
                return BackToLogin(context, returnUrl, new Flash(Error: ExpiredForm));
            }

            var token = context.Request.Cookies[SecondFactorCookie];
            var outcome = await auth.VerifySecondFactorAsync(token, form["code"].ToString(), RemoteAddress(context), context.RequestAborted).ConfigureAwait(false);
            if (outcome.Status == SignInStatus.Failed && auth.ReadPendingSecondFactor(token) is null)
            {
                context.Response.Cookies.Delete(SecondFactorCookie);
            }

            return await FinishAsync(context, outcome, returnUrl, null).ConfigureAwait(false);
        });

        group.MapGet("/restart", (HttpContext context, string? returnUrl) =>
        {
            context.Response.Cookies.Delete(SecondFactorCookie);
            return Results.LocalRedirect(LoginUrl(SafeReturnUrl(returnUrl)));
        });

        group.MapPost("/setup", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
            if (form is null)
            {
                return BackToLogin(context, "/", new Flash(Error: ExpiredForm));
            }

            var user = form["username"].ToString();
            var outcome = await auth.CompleteSetupAsync(
                form["code"].ToString(), user, form["password"].ToString(), form["confirm"].ToString(), RemoteAddress(context), context.RequestAborted).ConfigureAwait(false);
            return await FinishAsync(context, outcome, "/", user).ConfigureAwait(false);
        });

        group.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (await ReadFormAsync(context, antiforgery).ConfigureAwait(false) is null)
            {
                return Results.LocalRedirect("/access");
            }

            var user = context.User;
            await context.SignOutAsync(AuthService.Scheme).ConfigureAwait(false);
            context.Response.Cookies.Delete(SecondFactorCookie);
            var signOut = await auth.GetSignOutUriAsync(user, new Uri(BaseUri(context), LoginPath.TrimStart('/')), context.RequestAborted).ConfigureAwait(false);
            if (signOut is not null)
            {
                return Results.Redirect(signOut.ToString());
            }

            return BackToLogin(context, "/", new Flash(Notice: "You're signed out."));
        });

        group.MapGet("/external/{id}", async (string id, string? returnUrl, HttpContext context) =>
        {
            var callback = new Uri(BaseUri(context), $"{CallbackPath.TrimStart('/')}/{Uri.EscapeDataString(id)}");
            ExternalChallenge? challenge;
            try
            {
                challenge = await auth.BeginExternalAsync(id, callback, SafeReturnUrl(returnUrl), context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PhotoClippy.Auth").LogError(ex, "Auth plugin {Provider} could not start a sign-in", id);
                return BackToLogin(context, SafeReturnUrl(returnUrl), new Flash(Error: "That sign-in method is unavailable right now."));
            }

            if (challenge is null)
            {
                return Results.NotFound();
            }

            context.Response.Cookies.Append(ExternalCookie, challenge.ProtectedState, ShortCookie(context, TimeSpan.FromMinutes(10), CallbackPath));
            return Results.Redirect(challenge.RedirectUri.ToString());
        });

        group.MapGet("/callback/{id}", async (string id, HttpContext context) =>
        {
            var state = context.Request.Cookies[ExternalCookie];
            context.Response.Cookies.Delete(ExternalCookie, new CookieOptions { Path = CallbackPath });
            var parameters = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
            var callback = new Uri(BaseUri(context), $"{CallbackPath.TrimStart('/')}/{Uri.EscapeDataString(id)}");
            var (outcome, returnUrl) = await auth.CompleteExternalAsync(id, state, callback, parameters, RemoteAddress(context), context.RequestAborted).ConfigureAwait(false);
            return await FinishAsync(context, outcome, SafeReturnUrl(returnUrl), null).ConfigureAwait(false);
        });
    }

    /// <summary>Client address after trusted proxies are unwrapped.</summary>
    public static string? RemoteAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    /// <summary>A same-site path to return to after signing in; anything else becomes <c>/</c>.</summary>
    public static string SafeReturnUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/' || (url.Length > 1 && url[1] is '/' or '\\') || url.Any(char.IsControl)
            || url.StartsWith(LoginPath, StringComparison.OrdinalIgnoreCase) || url.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return url;
    }

    public static string LoginUrl(string returnUrl) =>
        returnUrl == "/" ? LoginPath : $"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";

    /// <summary>Reads and deletes the flash cookie.</summary>
    public static Flash? TakeFlash(HttpContext context)
    {
        if (context.Request.Cookies[FlashCookie] is not { Length: > 0 } value)
        {
            return null;
        }

        context.Response.Cookies.Delete(FlashCookie);
        try
        {
            return JsonSerializer.Deserialize<Flash>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IResult> FinishAsync(HttpContext context, SignInOutcome outcome, string returnUrl, string? user)
    {
        switch (outcome.Status)
        {
            case SignInStatus.SignedIn:
                context.Response.Cookies.Delete(SecondFactorCookie);
                await context.SignInAsync(AuthService.Scheme, outcome.Principal!, new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);
                return Results.LocalRedirect(returnUrl);
            case SignInStatus.SecondFactor:
                context.Response.Cookies.Append(SecondFactorCookie, outcome.PendingToken!, ShortCookie(context, TimeSpan.FromMinutes(5), "/"));
                return Results.LocalRedirect(LoginUrl(returnUrl));
            default:
                return BackToLogin(context, returnUrl, new Flash(Error: outcome.Message, User: user));
        }
    }

    private static IResult BackToLogin(HttpContext context, string returnUrl, Flash flash)
    {
        context.Response.Cookies.Append(FlashCookie, JsonSerializer.Serialize(flash), ShortCookie(context, TimeSpan.FromMinutes(1), "/"));
        return Results.LocalRedirect(LoginUrl(returnUrl));
    }

    private static CookieOptions ShortCookie(HttpContext context, TimeSpan maxAge, string path) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        MaxAge = maxAge,
        Path = path,
        IsEssential = true,
    };

    private static async Task<IFormCollection?> ReadFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            return null;
        }

        return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static Uri BaseUri(HttpContext context) =>
        new($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/");

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = status;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }

    /// <summary>Order-independent fingerprint of a principal's claims, to detect role or profile changes.</summary>
    public static string Signature(ClaimsPrincipal principal) =>
        string.Join('\n', principal.Claims.Select(c => c.Type + "=" + c.Value).Order(StringComparer.Ordinal));

    private static IncomingRequest ToIncomingRequest(HttpContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in context.Request.Headers)
        {
            headers[name] = value.ToString();
        }

        return new IncomingRequest
        {
            Headers = headers,
            PeerAddress = context.Items[PeerAddressKey] as string,
            RemoteAddress = RemoteAddress(context),
        };
    }

    private static ForwardedHeadersOptions ForwardedOptions(IEnumerable<string> trustedProxies, ILogger logger)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in trustedProxies.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (entry.Contains('/', StringComparison.Ordinal) && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                logger.LogWarning("Ignoring auth.trustedProxies entry '{Entry}': not an IP address or CIDR range", entry);
            }
        }

        return options;
    }
}
