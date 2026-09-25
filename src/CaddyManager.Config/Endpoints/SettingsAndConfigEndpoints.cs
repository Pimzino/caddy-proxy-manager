using System.Text.Json;
using System.Text.Json.Nodes;
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

internal static class SettingsEndpoints
{
    private const string MacInput = "eabMacKey";
    private const string MacOutput = "hasEabMacKey";
    private const string MacProtected = "eabMacKeyProtected";

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/caddy").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", (IStore store) => Results.Ok(ToWire(store.GetSettings<CaddySettings>())));

        g.MapPut("/", async (JsonObject? body, IStore store, ISecretProtector secrets, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A settings object is required.");
            var previous = store.GetSettings<CaddySettings>();
            CaddySettings next;
            try
            {
                next = Merge(previous, body, secrets);
            }
            catch (JsonException ex)
            {
                return ApiResults.BadRequest("The settings could not be read: " + ex.Message);
            }
            if (ModelValidation.Validate(next) is { } problem) return problem;

            var changed = ChangedFields(previous, next);
            return await ConfigTransaction.RunAsync(http, "Caddy settings updated",
                persist: () => store.SaveSettings(next),
                rollback: () => store.SaveSettings(previous),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "updated", "settings", "caddy", "Caddy settings",
                        changed.Count == 0 ? null : "changed: " + string.Join(", ", changed));
                    return Results.Ok(new { item = ToWire(next), apply });
                },
                affectsCaddyfileMode: true);
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>Settings camelCased, minus *Protected, plus hasEabMacKey.</summary>
    internal static JsonObject ToWire(CaddySettings s)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(s, JsonDefaults.Api)!;
        node.Remove(MacProtected);
        node[MacOutput] = !string.IsNullOrEmpty(s.EabMacKeyProtected);
        return node;
    }

    /// <summary>Overlays the request on the current settings. eabMacKey: absent/null = unchanged, "" = clear, other = set.</summary>
    internal static CaddySettings Merge(CaddySettings current, JsonObject body, ISecretProtector secrets)
    {
        var merged = (JsonObject)JsonSerializer.SerializeToNode(current, JsonDefaults.Storage)!;
        string? macAction = null;
        var macPresent = false;
        foreach (var (key, value) in body)
        {
            if (key.Equals(MacInput, StringComparison.OrdinalIgnoreCase))
            {
                if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                {
                    macPresent = true;
                    macAction = v.GetValue<string>();
                }
                else if (value is not null)
                {
                    throw new JsonException("eabMacKey must be a string.");
                }
                continue;
            }
            if (key.Equals(MacOutput, StringComparison.OrdinalIgnoreCase) || key.Equals(MacProtected, StringComparison.OrdinalIgnoreCase))
                continue;
            var existingKey = merged.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            merged[existingKey] = value?.DeepClone();
        }

        var next = merged.Deserialize<CaddySettings>(JsonDefaults.Storage) ?? new CaddySettings();
        next.EabMacKeyProtected = current.EabMacKeyProtected;
        if (macPresent)
            next.EabMacKeyProtected = string.IsNullOrEmpty(macAction) ? null : secrets.Protect(macAction.Trim());

        next.BindAddresses = (next.BindAddresses ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        next.TrustedProxies = (next.TrustedProxies ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        next.AcmeEmail = next.AcmeEmail?.Trim() ?? "";
        next.AdminListen = next.AdminListen?.Trim() ?? "";
        next.LogLevel = next.LogLevel?.Trim().ToLowerInvariant() ?? "info";
        next.RawCaddyfile ??= "";
        next.EabKeyId = string.IsNullOrWhiteSpace(next.EabKeyId) ? null : next.EabKeyId.Trim();
        next.CustomAcmeDirectory = string.IsNullOrWhiteSpace(next.CustomAcmeDirectory) ? null : next.CustomAcmeDirectory.Trim();
        next.CustomAcmeRootPath = string.IsNullOrWhiteSpace(next.CustomAcmeRootPath) ? null : next.CustomAcmeRootPath.Trim();
        next.CertificateStorePath = string.IsNullOrWhiteSpace(next.CertificateStorePath) ? null : next.CertificateStorePath.Trim();
        next.DefaultRedirectUrl = string.IsNullOrWhiteSpace(next.DefaultRedirectUrl) ? null : next.DefaultRedirectUrl.Trim();
        next.ServerOptionsJson = string.IsNullOrWhiteSpace(next.ServerOptionsJson) ? null : next.ServerOptionsJson.Trim();
        return next;
    }

    private static List<string> ChangedFields(CaddySettings a, CaddySettings b)
    {
        var ja = (JsonObject)JsonSerializer.SerializeToNode(a, JsonDefaults.Storage)!;
        var jb = (JsonObject)JsonSerializer.SerializeToNode(b, JsonDefaults.Storage)!;
        var list = new List<string>();
        foreach (var (k, v) in jb)
        {
            if (!JsonNode.DeepEquals(v, ja[k]))
                list.Add(k == MacProtected ? MacInput : k);
        }
        return list;
    }
}

internal static class ConfigEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/config").RequireAuthorization(Policies.Viewer);

        g.MapGet("/preview", (CaddyConfigService config) =>
        {
            var result = config.Generate();
            return Results.Ok(new { json = result.ToJson(), warnings = result.Warnings });
        });

        g.MapGet("/running", async (ICaddyAdminClient admin, CancellationToken ct) =>
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
                : Results.Ok(new { json = CaddyJson.Reformat(json) });
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

        g.MapGet("/revisions/{id}", (string id, IStore store) =>
            store.Col<ConfigRevision>().FindById(id) is { } r ? Results.Ok(r) : ApiResults.NotFound("Revision"));

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
                return Results.Ok(await admin.GetUpstreamsAsync(ct));
            }
            catch (CaddyAdminException ex)
            {
                return Results.Problem(title: "Caddy admin API error", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(Policies.Viewer);
    }

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
