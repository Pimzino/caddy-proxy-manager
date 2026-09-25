using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace CaddyManager.Ops.Settings;

/// <summary>Pipeline pieces the host composes (kept here so they are covered by the Ops tests).</summary>
public static class OpsHosting
{
    /// <summary>
    /// Honour X-Forwarded-For / X-Forwarded-Proto only when the direct peer is a loopback proxy (the manager published
    /// through Caddy on the same server). Headers from any other peer are ignored, so the sign-in rate limiter, the audit
    /// log and the Secure cookie flag see the real client address/scheme only when a local proxy vouches for it.
    /// Must run before authentication and the credential endpoints' rate limiter.
    /// </summary>
    public static IApplicationBuilder UseLoopbackForwardedHeaders(this IApplicationBuilder app) =>
        app.UseForwardedHeaders(LoopbackForwardedHeadersOptions());

    public static ForwardedHeadersOptions LoopbackForwardedHeadersOptions()
    {
        var o = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);
        o.KnownProxies.Add(IPAddress.IPv6Loopback);
        return o;
    }

    /// <summary>
    /// Redirects plain-HTTP UI and API requests to the HTTPS listener (307, so the method and body are kept and
    /// browsers do not cache the redirect). GET /api/health stays available over HTTP for load balancers and monitoring.
    /// Place after <see cref="UseLoopbackForwardedHeaders"/> so requests Caddy received over HTTPS are not redirected.
    /// </summary>
    public static IApplicationBuilder UseUiHttpsRedirect(this IApplicationBuilder app, int httpsPort) =>
        app.Use((ctx, next) =>
        {
            var r = ctx.Request;
            if (r.IsHttps || r.Path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(r.Host.Host))
                return next(ctx);
            var target = $"https://{new HostString(r.Host.Host, httpsPort)}{r.PathBase}{r.Path}{r.QueryString}";
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = target;
            return Task.CompletedTask;
        });
}
