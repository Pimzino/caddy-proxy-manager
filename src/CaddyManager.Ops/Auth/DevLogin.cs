#if DEBUG
using System.Net;
using CaddyManager.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Ops.Auth;

/// <summary>
/// DEBUG builds only: GET /api/dev/login?email=... signs in as an existing user without a password,
/// for UI development/automation. Requires env CM_DEV_AUTOLOGIN=1 and a loopback client. Not compiled into Release.
/// </summary>
internal static class DevLogin
{
    public static void Map(IEndpointRouteBuilder app)
    {
        if (Environment.GetEnvironmentVariable("CM_DEV_AUTOLOGIN") != "1") return;
        app.MapGet("/api/dev/login", async (HttpContext ctx, IStore store, string email) =>
        {
            if (ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return Results.NotFound();
            var user = UserRules.FindByEmail(store, email);
            if (user is null || user.Disabled) return ApiResults.NotFound("User");
            user.LastLoginAt = DateTime.UtcNow;
            store.Col<CaddyManager.Core.Models.User>().Update(user);
            await AuthSetup.SignInAsync(ctx, user);
            return Results.Redirect("/");
        }).AllowAnonymous();
    }
}
#endif
