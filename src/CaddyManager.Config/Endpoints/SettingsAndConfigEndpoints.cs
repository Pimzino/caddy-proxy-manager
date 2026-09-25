using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Generation;
using CaddyManager.Config.Services;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Config.Endpoints;

public sealed record CaddyfileAdaptInput(string? Caddyfile);

internal static class ConfigEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/config").RequireAuthorization(Policies.Viewer);

        // What an apply would load: the generated config in Managed mode, the adapted Caddyfile in Caddyfile mode.
        g.MapGet("/preview", async (CaddyConfigService config, IStore store, AppPaths paths, ISecretProtector secrets, HttpContext http, CancellationToken ct) =>
        {
            var isAdmin = await EndpointSecurity.IsAdminAsync(http);
            var settings = store.GetSettings<CaddySettings>();
            if (settings.Mode == ConfigMode.Caddyfile)
            {
                if (string.IsNullOrWhiteSpace(settings.RawCaddyfile))
                    return ApiResults.Failed("The Caddyfile is empty", "Caddyfile mode is enabled but no Caddyfile has been saved.");
                try
                {
                    var adapted = await config.AdaptCaddyfileAsync(settings.RawCaddyfile, ct);
                    if (adapted is null)
                        return Results.Problem(title: "Cannot adapt the Caddyfile",
                            detail: "Caddy is not running and its binary is not installed, so the Caddyfile cannot be converted.",
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    var warnings = adapted.Value.Warnings.ToList();
                    var json = CaddyConfigGenerator.CompleteAdaptedConfig(adapted.Value.Json, settings, paths, warnings,
                        SettingsSecrets.Read(settings, secrets).Storage);
                    return Results.Ok(new { json = Visible(json, isAdmin), warnings, mode = "caddyfile" });
                }
                catch (CaddyAdminException ex)
                {
                    return ApiResults.Failed("The Caddyfile is invalid", ex.Message);
                }
            }
            var result = config.Generate();
            return Results.Ok(new { json = Visible(result.ToJson(), isAdmin), warnings = result.Warnings, mode = "managed" });
        });

        g.MapGet("/running", async (ICaddyAdminClient admin, HttpContext http, CancellationToken ct) =>
        {
            string? json;
            try
            {
                json = await admin.GetConfigAsync(ct);
            }
            catch (CaddyAdminException ex)
            {
                return Results.Problem(title: "Caddy admin API error", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return json is null
                ? Results.Problem(title: "Caddy is not reachable",
                    detail: $"The Caddy admin API at {SafeBaseUrl(admin)} did not respond. Is Caddy running?",
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { json = Visible(json, await EndpointSecurity.IsAdminAsync(http)) });
        });

        g.MapPost("/apply", async (ICaddyConfigService config, HttpContext http) =>
        {
            var gate = http.RequestServices.GetService(typeof(ConfigMutationGate)) as ConfigMutationGate;
            if (gate is not null) await gate.Lock.WaitAsync(http.RequestAborted);
            try
            {
                CaddyConfigService.AmbientUser.Value = ConfigTransaction.UserName(http);
                var result = await config.ApplyAsync("Manual apply", CancellationToken.None);
                ConfigTransaction.Audit(http, "applied", "caddy", result.RevisionId, "configuration",
                    result.Success ? (result.WrittenOnly ? "written only (Caddy not running)" : "applied") : "failed: " + result.Error);
                return Results.Ok(result);
            }
            finally
            {
                gate?.Lock.Release();
            }
        }).RequireAuthorization(Policies.Operator);

        g.MapGet("/revisions", (int? take, IStore store) =>
        {
            var n = Math.Clamp(take ?? 50, 1, CaddyConfigService.RevisionsToKeep);
            var list = store.Col<ConfigRevision>().Query()
                .OrderByDescending(r => r.CreatedAt)
                .Limit(n)
                .ToList()
                .Select(r => new
                {
                    id = r.Id,
                    createdAt = r.CreatedAt,
                    reason = r.Reason,
                    appliedBy = r.AppliedBy,
                    success = r.Success,
                    error = r.Error,
                    hash = r.Hash,
                })
                .ToList();
            return Results.Ok(list);
        });

        g.MapGet("/revisions/{id}", async (string id, IStore store, HttpContext http) =>
        {
            if (store.Col<ConfigRevision>().FindById(id) is not { } r) return ApiResults.NotFound("Revision");
            if (!await EndpointSecurity.IsAdminAsync(http)) r.Json = ConfigRedactor.Redact(r.Json);
            return Results.Ok(r);
        });

        g.MapPost("/caddyfile/adapt", async (CaddyfileAdaptInput? body, CaddyConfigService config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Caddyfile)) return ApiResults.BadRequest("caddyfile is required.");
            try
            {
                var result = await config.AdaptCaddyfileAsync(body.Caddyfile, ct);
                if (result is null)
                    return ApiResults.Failed("Cannot adapt the Caddyfile",
                        "Caddy is not running and its binary is not installed, so the Caddyfile cannot be converted. Install Caddy first.");
                return Results.Ok(new { json = result.Value.Json, warnings = result.Value.Warnings });
            }
            catch (CaddyAdminException ex)
            {
                return ApiResults.Failed("The Caddyfile is invalid", ex.Message);
            }
        }).RequireAuthorization(Policies.Admin);

        app.MapGet("/api/caddy/upstreams", async (ICaddyAdminClient admin, CancellationToken ct) =>
        {
            try
            {
                // All upstreams, each flagged whether a health check monitors it (unmonitored = health unknown).
                if (admin is CaddyAdminClient client) return Results.Ok(await client.GetUpstreamStatusAsync(ct));
                return Results.Ok((await admin.GetUpstreamsAsync(ct)).Select(u => new UpstreamStatus(u.Address, u.NumRequests, u.Fails, u.Healthy, true)));
            }
            catch (CaddyAdminException ex)
            {
                return Results.Problem(title: "Caddy admin API error", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(Policies.Viewer);
    }

    /// <summary>Pretty JSON; secrets replaced with "***" unless the caller is an administrator.</summary>
    private static string Visible(string json, bool isAdmin) => isAdmin ? CaddyJson.Reformat(json) : ConfigRedactor.Redact(json);

    private static string SafeBaseUrl(ICaddyAdminClient admin)
    {
        try
        {
            return admin.BaseUrl;
        }
        catch (CaddyAdminException)
        {
            return "(invalid address)";
        }
    }
}
