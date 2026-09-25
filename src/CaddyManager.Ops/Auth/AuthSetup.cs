using System.Security.Claims;
using System.Threading.RateLimiting;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops.Auth;

internal static class AuthSetup
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string CookieName = "cpm_session";
    public const string LoginPath = "/api/auth/login";
    public const string SetupPath = "/api/setup";

    public static IServiceCollection AddOpsAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // Persist Data Protection keys under DataDir so sessions survive service restarts.
        // On Windows the key ring is additionally encrypted with machine-scope DPAPI.
        services.AddDataProtection().SetApplicationName("CaddyProxyManager");
        services.AddOptions<KeyManagementOptions>()
            .Configure<AppPaths>((o, paths) =>
            {
                var dir = new DirectoryInfo(Path.Combine(paths.DataDir, "keys"));
                dir.Create();
                if (OperatingSystem.IsWindows()) RestrictToAdministrators(dir);
                o.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(dir,
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
                if (OperatingSystem.IsWindows())
                    o.XmlEncryptor = new Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlEncryptor(
                        protectToLocalMachine: true, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            });

        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Cookie.Path = "/";
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = ctx => WriteProblem(ctx.HttpContext, StatusCodes.Status401Unauthorized,
                    "Not signed in", "Sign in to use this resource."),
                OnRedirectToAccessDenied = ctx => WriteProblem(ctx.HttpContext, StatusCodes.Status403Forbidden,
                    "Forbidden", "Your role does not allow this action."),
                OnRedirectToLogout = ctx => { ctx.Response.StatusCode = StatusCodes.Status204NoContent; return Task.CompletedTask; },
                OnRedirectToReturnUrl = ctx => { ctx.Response.StatusCode = StatusCodes.Status204NoContent; return Task.CompletedTask; },
                OnValidatePrincipal = ValidatePrincipalAsync,
            };
        });
        // Default session length follows UiSettings.SessionHours (per-session lifetime is also set at sign-in).
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<IStore>((o, store) =>
        {
            try { o.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(store.GetSettings<UiSettings>().SessionHours, 1, 720)); }
            catch { /* keep default */ }
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.RequireAuthenticatedUser().RequireRole("viewer", "operator", "admin"))
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().RequireRole("operator", "admin"))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole("admin"))
            // Defence in depth: an endpoint that forgets RequireAuthorization/AllowAnonymous requires a
            // signed-in user instead of being public. (The host serves static files before UseAuthorization.)
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Credential endpoints (login, setup) are throttled per client address by an endpoint filter
        // (LoginThrottle) — bound to the endpoint, so path variants such as "/api/auth/login/" or
        // "/API/AUTH/LOGIN" cannot bypass it the way a path-string check could.
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<SessionRevocations>();

        services.AddTransient<IStartupFilter, OpsStartupFilter>();
        return services;
    }

    /// <summary>
    /// The key ring signs session cookies: only SYSTEM and Administrators may read it (ProgramData is
    /// readable by all local users by default, and machine-scope DPAPI does not protect against them).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToAdministrators(DirectoryInfo dir)
    {
        try
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch
        {
            // Not fatal (e.g. running unelevated in development); keys are still DPAPI-encrypted.
        }
    }

    internal static Task WriteProblem(HttpContext ctx, int status, string title, string detail)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        return Results.Problem(title: title, detail: detail, statusCode: status).ExecuteAsync(ctx);
    }

    /// <summary>
    /// Re-checks that the cookie's user still exists, is enabled and has the same security stamp
    /// (password change / disable / reset-password invalidates other sessions). Role/name changes
    /// are applied to the live session.
    /// </summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var id = principal?.FindFirstValue(CpmClaims.UserId);
        var stamp = principal?.FindFirstValue(CpmClaims.SecurityStamp);
        var cache = ctx.HttpContext.RequestServices.GetRequiredService<UserSnapshotCache>();
        User? user = null;
        if (!string.IsNullOrEmpty(id))
        {
            try { user = cache.Get(id); }
            catch (Exception ex)
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CaddyManager.Ops.Auth")
                    .LogError(ex, "Could not validate session for user {UserId}", id);
            }
        }

        var sid = principal?.FindFirstValue(CpmClaims.SessionId);
        var revoked = false;
        if (!string.IsNullOrEmpty(sid))
        {
            try { revoked = ctx.HttpContext.RequestServices.GetRequiredService<SessionRevocations>().IsRevoked(sid); }
            catch { revoked = true; /* fail closed */ }
        }

        if (revoked || user is null || user.Disabled || stamp != user.SecurityStamp.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(Scheme);
            return;
        }

        if (principal is null) return;
        if (principal.FindFirstValue(CpmClaims.Role) != CpmClaims.RoleValue(user.Role) ||
            principal.FindFirstValue(CpmClaims.Email) != user.Email ||
            principal.FindFirstValue(CpmClaims.Name) != (string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name))
        {
            ctx.ReplacePrincipal(CpmClaims.CreatePrincipal(user, Scheme, sid));
            ctx.ShouldRenew = true;
        }
    }

    /// <summary>Issues the session cookie (with a fresh session id). Lifetime = UiSettings.SessionHours, sliding.</summary>
    internal static async Task SignInAsync(HttpContext ctx, User user)
    {
        // Replacing a session (e.g. after a password change) retires the old session id too.
        if (ctx.User.FindFirstValue(CpmClaims.SessionId) is { Length: > 0 } previous)
            ctx.RequestServices.GetService<SessionRevocations>()?.Revoke(previous, DateTime.UtcNow.AddHours(720));
        var hours = 12;
        try { hours = Math.Clamp(ctx.RequestServices.GetRequiredService<IStore>().GetSettings<UiSettings>().SessionHours, 1, 720); }
        catch { /* default */ }
        var now = DateTimeOffset.UtcNow;
        var principal = CpmClaims.CreatePrincipal(user, Scheme, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        await ctx.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            IssuedUtc = now,
            ExpiresUtc = now.AddHours(hours),
        });
    }
}

/// <summary>Adds the CSRF header check in front of the host's pipeline.</summary>
internal sealed class OpsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<CsrfMiddleware>();
        next(app);
    };
}

/// <summary>
/// Fixed-window limiter for credential endpoints (login, setup): OpsOptions.LoginAttemptsPerMinute per client.
/// IPv6 clients are grouped by /64 (one host usually owns a whole /64, so per-address limits are trivially evaded).
/// </summary>
internal sealed class LoginThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger _logger;

    public LoginThrottle(IOptions<OpsOptions> options, ILoggerFactory loggers)
    {
        var permits = Math.Max(1, options.Value.LoginAttemptsPerMinute);
        _logger = loggers.CreateLogger("CaddyManager.Ops.Auth");
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    internal static string PartitionKey(System.Net.IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !System.Net.IPAddress.IsLoopback(ip))
        {
            var b = ip.GetAddressBytes();
            Array.Clear(b, 8, 8);
            return new System.Net.IPAddress(b) + "/64";
        }
        return ip.ToString();
    }

    public async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        using var lease = _limiter.AttemptAcquire(PartitionKey(http.Connection.RemoteIpAddress));
        if (lease.IsAcquired) return await next(ctx);

        _logger.LogWarning("Too many sign-in attempts from {Ip}; request to {Path} rejected",
            CurrentUser.FormatIp(http.Connection.RemoteIpAddress), http.Request.Path);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(title: "Too many attempts",
            detail: "Too many sign-in attempts from this address. Wait a minute and try again.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    public void Dispose() => _limiter.Dispose();
}

internal static class LoginThrottleExtensions
{
    /// <summary>Applies <see cref="LoginThrottle"/> to a credential endpoint.</summary>
    public static TBuilder RequireLoginThrottle<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter((ctx, next) => ctx.HttpContext.RequestServices.GetRequiredService<LoginThrottle>().FilterAsync(ctx, next));
}

/// <summary>
/// CSRF defence: every state-changing /api request must carry "X-CPM-Request: 1". Browsers cannot add
/// custom headers to cross-site form posts, and cross-origin fetch with custom headers needs CORS (not enabled).
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CPM-Request";

    public Task InvokeAsync(HttpContext ctx)
    {
        var r = ctx.Request;
        if (r.Path.StartsWithSegments("/api") && !IsSafe(r.Method) && !IsExempt(r) &&
            !string.Equals(r.Headers[HeaderName].ToString(), "1", StringComparison.Ordinal))
        {
            return AuthSetup.WriteProblem(ctx, StatusCodes.Status400BadRequest, "Missing request header",
                $"State-changing API requests must include the header '{HeaderName}: 1' (CSRF protection).");
        }
        return next(ctx);
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static bool IsExempt(HttpRequest r) =>
        HttpMethods.IsPost(r.Method) &&
        (r.Path.Equals(AuthSetup.LoginPath, StringComparison.OrdinalIgnoreCase) || r.Path.Equals(AuthSetup.SetupPath, StringComparison.OrdinalIgnoreCase));
}
