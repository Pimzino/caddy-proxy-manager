using CaddyManager.Config.Generation;
using CaddyManager.Config.Services;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

internal static class HostEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hosts").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", async (string? kind, IStore store, HttpContext http) =>
        {
            var isAdmin = await EndpointSecurity.IsAdminAsync(http);
            var hosts = store.Col<SiteHost>().FindAll();
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (!Enum.TryParse<HostKind>(kind, ignoreCase: true, out var k))
                    return ApiResults.BadRequest($"Unknown host kind '{kind}'. Use proxy, redirect, static or response.");
                hosts = hosts.Where(h => h.Kind == k);
            }
            return Results.Ok(hosts.OrderBy(h => h.Domains.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(h => isAdmin ? h : EndpointSecurity.ForViewer(h)).ToList());
        });

        g.MapGet("/{id}", async (string id, IStore store, HttpContext http) =>
        {
            if (store.Col<SiteHost>().FindById(id) is not { } h) return ApiResults.NotFound("Host");
            return Results.Ok(await EndpointSecurity.IsAdminAsync(http) ? h : EndpointSecurity.ForViewer(h));
        });

        g.MapPost("/", async (SiteHost? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A host object is required.");
            body.Id = Entity.NewId();
            body.CreatedAt = body.UpdatedAt = DateTime.UtcNow;
            ModelValidation.Normalize(body);
            if (await EndpointSecurity.CheckHostAsync(http, body, null, store) is { } denied) return denied;
            if (ModelValidation.Validate(body, store, http.RequestServices.GetService<ISecretProtector>()) is { } problem) return problem;

            var col = store.Col<SiteHost>();
            var isAdmin = await EndpointSecurity.IsAdminAsync(http);
            return await ConfigTransaction.RunAsync(http, $"Host created: {Name(body)}",
                persist: () => col.Insert(body),
                rollback: () => col.Delete(body.Id),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "created", "host", body.Id, Name(body), body.Kind.ToString().ToLowerInvariant());
                    return Results.Ok(new { item = isAdmin ? body : EndpointSecurity.ForViewer(body), apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPut("/{id}", async (string id, SiteHost? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A host object is required.");
            var col = store.Col<SiteHost>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Host");
            body.Id = id;
            body.CreatedAt = existing.CreatedAt;
            body.UpdatedAt = DateTime.UtcNow;
            ModelValidation.Normalize(body);
            if (await EndpointSecurity.CheckHostAsync(http, body, existing, store) is { } denied) return denied;
            if (ModelValidation.Validate(body, store, http.RequestServices.GetService<ISecretProtector>()) is { } problem) return problem;

            var isAdmin = await EndpointSecurity.IsAdminAsync(http);
            return await ConfigTransaction.RunAsync(http, $"Host updated: {Name(body)}",
                persist: () => col.Update(body),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "updated", "host", id, Name(body));
                    return Results.Ok(new { item = isAdmin ? body : EndpointSecurity.ForViewer(body), apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapDelete("/{id}", async (string id, IStore store, HttpContext http) =>
        {
            var col = store.Col<SiteHost>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Host");
            return await ConfigTransaction.RunAsync(http, $"Host deleted: {Name(existing)}",
                persist: () => col.Delete(id),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "deleted", "host", id, Name(existing));
                    return Results.Ok(new { apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPost("/{id}/enable", (string id, IStore store, HttpContext http) => SetEnabled(id, true, store, http))
            .RequireAuthorization(Policies.Operator).RejectOnManagedNode();
        g.MapPost("/{id}/disable", (string id, IStore store, HttpContext http) => SetEnabled(id, false, store, http))
            .RequireAuthorization(Policies.Operator).RejectOnManagedNode();
    }

    private static string Name(SiteHost h) => h.Domains.FirstOrDefault() ?? h.Id;

    private static async Task<IResult> SetEnabled(string id, bool enabled, IStore store, HttpContext http)
    {
        var col = store.Col<SiteHost>();
        var existing = col.FindById(id);
        if (existing is null) return ApiResults.NotFound("Host");
        var updated = col.FindById(id);
        updated.Enabled = enabled;
        updated.UpdatedAt = DateTime.UtcNow;
        if (enabled && ModelValidation.DomainConflict(updated, store) is { } conflict) return conflict;
        if (enabled && EndpointSecurity.TargetProblems(updated, EndpointSecurity.Guard(store, includeUi: false)).FirstOrDefault() is { Message: not null } target)
            return ApiResults.BadRequest($"The host cannot be enabled: {target.Message}");
        if (enabled && updated.Kind == HostKind.Static &&
            PathGuard.CheckStaticRoot(updated.RootPath, http.RequestServices.GetRequiredService<AppPaths>(), EndpointSecurity.CertificateStore(http), isAdmin: true, EndpointSecurity.SharedStorage(http)) is { } rootProblem)
            return ApiResults.BadRequest($"The host cannot be enabled: {rootProblem.Message}");

        var verb = enabled ? "enabled" : "disabled";
        var isAdmin = await EndpointSecurity.IsAdminAsync(http);
        return await ConfigTransaction.RunAsync(http, $"Host {verb}: {Name(updated)}",
            persist: () => col.Update(updated),
            rollback: () => col.Upsert(existing),
            onSuccess: apply =>
            {
                ConfigTransaction.Audit(http, verb, "host", id, Name(updated));
                return Results.Ok(new { item = isAdmin ? updated : EndpointSecurity.ForViewer(updated), apply });
            });
    }
}

internal static class StreamEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/streams").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", (IStore store) =>
            Results.Ok(store.Col<StreamHost>().FindAll().OrderBy(s => s.ListenPort).ThenBy(s => s.Protocol).ToList()));

        g.MapGet("/support", async (CaddyConfigService config, CancellationToken ct) =>
        {
            var modules = await config.RefreshModulesAsync(ct);
            return Results.Ok(new
            {
                supported = modules?.Contains(CaddyConfigGenerator.Layer4Module) ?? false,
                plugin = CaddyConfigGenerator.Layer4Plugin,
            });
        });

        g.MapGet("/{id}", (string id, IStore store) =>
            store.Col<StreamHost>().FindById(id) is { } s ? Results.Ok(s) : ApiResults.NotFound("Stream"));

        g.MapPost("/", async (StreamHost? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A stream object is required.");
            body.Id = Entity.NewId();
            body.CreatedAt = body.UpdatedAt = DateTime.UtcNow;
            ModelValidation.Normalize(body);
            if (ModelValidation.Validate(body, store) is { } problem) return problem;
            var col = store.Col<StreamHost>();
            return await ConfigTransaction.RunAsync(http, $"Stream created: {Name(body)}",
                persist: () => col.Insert(body),
                rollback: () => col.Delete(body.Id),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "created", "stream", body.Id, Name(body));
                    return Results.Ok(new { item = body, apply });
                },
                concernsStreams: true);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPut("/{id}", async (string id, StreamHost? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A stream object is required.");
            var col = store.Col<StreamHost>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Stream");
            body.Id = id;
            body.CreatedAt = existing.CreatedAt;
            body.UpdatedAt = DateTime.UtcNow;
            ModelValidation.Normalize(body);
            if (ModelValidation.Validate(body, store) is { } problem) return problem;
            return await ConfigTransaction.RunAsync(http, $"Stream updated: {Name(body)}",
                persist: () => col.Update(body),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "updated", "stream", id, Name(body));
                    return Results.Ok(new { item = body, apply });
                },
                concernsStreams: true);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapDelete("/{id}", async (string id, IStore store, HttpContext http) =>
        {
            var col = store.Col<StreamHost>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Stream");
            return await ConfigTransaction.RunAsync(http, $"Stream deleted: {Name(existing)}",
                persist: () => col.Delete(id),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "deleted", "stream", id, Name(existing));
                    return Results.Ok(new { apply });
                },
                concernsStreams: true);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();
    }

    private static string Name(StreamHost s) =>
        $"{s.Protocol.ToString().ToLowerInvariant()}/{s.ListenPort} → {s.UpstreamHost}:{s.UpstreamPort}";
}
