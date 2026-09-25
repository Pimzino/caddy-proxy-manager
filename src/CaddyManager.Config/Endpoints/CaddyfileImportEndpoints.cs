using CaddyManager.Config.Import;
using CaddyManager.Config.Services;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

public sealed record CaddyfileImportCommitInput(List<SiteHost>? Hosts);

/// <summary>Caddyfile → host drafts (nothing saved) and transactional creation of the reviewed drafts. Administrators only.</summary>
internal static class CaddyfileImportEndpoints
{
    public const int MaxHosts = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/config/caddyfile/import").RequireAuthorization(Policies.Admin).RejectOnManagedNode();

        g.MapPost("/", async (CaddyfileAdaptInput? body, CaddyConfigService config, IStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Caddyfile)) return ApiResults.BadRequest("caddyfile is required.");
            (string Json, List<string> Warnings)? adapted;
            try
            {
                adapted = await config.AdaptCaddyfileAsync(body.Caddyfile, ct);
            }
            catch (CaddyAdminException ex)
            {
                return ApiResults.Failed("The Caddyfile is invalid", ex.Message);
            }
            if (adapted is null)
                return ApiResults.Failed("Cannot adapt the Caddyfile",
                    "Caddy is not running and its binary is not installed, so the Caddyfile cannot be converted. Install Caddy first.");

            var result = CaddyfileImporter.Import(adapted.Value.Json, store.Col<SiteHost>().FindAll().ToList());
            return Results.Ok(new
            {
                drafts = result.Drafts,
                unmapped = result.Unmapped,
                warnings = adapted.Value.Warnings.Concat(result.Warnings).ToList(),
            });
        });

        g.MapPost("/commit", async (CaddyfileImportCommitInput? body, IStore store, HttpContext http) =>
        {
            var hosts = body?.Hosts?.Where(h => h is not null).ToList() ?? [];
            if (hosts.Count == 0) return ApiResults.BadRequest("hosts is required (the reviewed drafts to create).");
            if (hosts.Count > MaxHosts) return ApiResults.BadRequest($"At most {MaxHosts} hosts can be imported at once.");

            var v = new Validator();
            var paths = http.RequestServices.GetRequiredService<AppPaths>();
            var storeRoot = EndpointSecurity.CertificateStore(http);
            var guard = EndpointSecurity.Guard(store, includeUi: false); // admin-only endpoint
            var now = DateTime.UtcNow;
            for (var i = 0; i < hosts.Count; i++)
            {
                var h = hosts[i];
                h.Id = Entity.NewId();
                h.CreatedAt = h.UpdatedAt = now;
                ModelValidation.Normalize(h);
                var prefix = $"hosts[{i}].";
                ModelValidation.ValidateFields(h, store, v, prefix, http.RequestServices.GetService<ISecretProtector>());
                if (h.Kind == HostKind.Static && PathGuard.CheckStaticRoot(h.RootPath, paths, storeRoot, isAdmin: true, EndpointSecurity.SharedStorage(http)) is { } rootProblem)
                    v.Add(prefix + "rootPath", rootProblem.Message);
                foreach (var (field, message) in EndpointSecurity.TargetProblems(h, guard))
                    v.Add(prefix + field, message);
            }
            if (!v.IsValid) return v.ToResult("One or more imported hosts are invalid; nothing was created.");

            var conflicts = Conflicts(hosts, store);
            if (conflicts.Count > 0)
                return ApiResults.Conflict("Nothing was created because these domains would be served twice: " + string.Join("; ", conflicts) + ". A domain can belong to only one enabled host.");

            var col = store.Col<SiteHost>();
            return await ConfigTransaction.RunAsync(http, $"Caddyfile import: {hosts.Count} host(s)",
                persist: () =>
                {
                    foreach (var h in hosts) col.Insert(h);
                },
                rollback: () =>
                {
                    foreach (var h in hosts) col.Delete(h.Id);
                },
                onSuccess: apply =>
                {
                    foreach (var h in hosts)
                        ConfigTransaction.Audit(http, "created", "host", h.Id, h.Domains.FirstOrDefault() ?? h.Id,
                            $"{h.Kind.ToString().ToLowerInvariant()}; imported from a Caddyfile");
                    return Results.Ok(new { created = hosts.Count, apply });
                });
        });
    }

    /// <summary>Domains used by more than one enabled host (among the imported hosts or with existing hosts).</summary>
    private static List<string> Conflicts(List<SiteHost> hosts, IStore store)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in store.Col<SiteHost>().FindAll().Where(h => h.Enabled))
            foreach (var d in h.Domains)
                if (NetUtil.NormalizeDomain(d) is { } n) owners.TryAdd(n, $"the existing host '{h.Domains.FirstOrDefault() ?? h.Id}'");

        var list = new List<string>();
        for (var i = 0; i < hosts.Count; i++)
        {
            if (!hosts[i].Enabled) continue;
            foreach (var d in hosts[i].Domains)
            {
                if (owners.TryGetValue(d, out var owner)) list.Add($"{d} (already used by {owner})");
                else owners[d] = $"imported host #{i + 1}";
            }
        }
        return list.Distinct().ToList();
    }
}
