using CaddyManager.Config.Certificates;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

/// <summary>Role checks inside endpoints whose route policy is broader than one of their operations.</summary>
internal static class EndpointSecurity
{
    /// <summary>True when the caller satisfies the Admin policy (as registered by the Ops module).</summary>
    public static async Task<bool> IsAdminAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true) return false;
        var auth = http.RequestServices.GetService<IAuthorizationService>();
        if (auth is null) return false;
        return (await auth.AuthorizeAsync(http.User, Policies.Admin)).Succeeded;
    }

    public static IResult Forbidden(string detail) =>
        Results.Problem(title: "Not allowed for your role", detail: detail, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>A host as non-administrators see it (secrets in advanced routes masked).</summary>
    public static SiteHost ForViewer(SiteHost h)
    {
        var redacted = ConfigRedactor.RedactRoutes(h.AdvancedRoutesJson);
        if (ReferenceEquals(redacted, h.AdvancedRoutesJson)) return h;
        h.AdvancedRoutesJson = redacted;
        return h;
    }

    /// <summary>The guard for the current Caddy and UI settings (UI ports protected only when includeUi — i.e. for non-admins).</summary>
    public static LocalEndpointGuard Guard(IStore store, bool includeUi = true) =>
        LocalEndpointGuard.Create(store.GetSettings<CaddySettings>(), store.GetSettings<UiSettings>(), includeUi);

    /// <summary>Configured certificate store root.</summary>
    public static string CertificateStore(HttpContext http) =>
        http.RequestServices.GetRequiredService<CertificateFileStore>().StoreRoot;

    /// <summary>
    /// Privilege boundaries for a host (after field validation): advanced routes are admin-only, static roots may not
    /// expose protected folders, upstreams and advanced-route dials may not target the Caddy admin API or the manager UI.
    /// </summary>
    public static async Task<IResult?> CheckHostAsync(HttpContext http, SiteHost host, SiteHost? existing, IStore store, string fieldPrefix = "")
    {
        var isAdmin = await IsAdminAsync(http);
        // Non-administrators receive advanced routes with secrets masked; sending that form back means "unchanged".
        if (!isAdmin && existing?.AdvancedRoutesJson is { } stored && host.AdvancedRoutesJson is not null &&
            !AdvancedRoutesChanged(host.AdvancedRoutesJson, ConfigRedactor.RedactRoutes(stored)))
            host.AdvancedRoutesJson = stored;
        if (!isAdmin && AdvancedRoutesChanged(host.AdvancedRoutesJson, existing?.AdvancedRoutesJson))
            return Forbidden("Only administrators can add or change advanced routes (raw Caddy JSON), because they bypass the manager's safety checks.");

        var v = new Validator();
        var paths = http.RequestServices.GetRequiredService<AppPaths>();
        if (host.Kind == HostKind.Static)
        {
            // An administrator's UNC root stays editable for operators as long as they keep it unchanged.
            var rootUnchanged = existing is { Kind: HostKind.Static } &&
                                string.Equals(existing.RootPath?.Trim(), host.RootPath?.Trim(), StringComparison.OrdinalIgnoreCase);
            var problem = PathGuard.CheckStaticRoot(host.RootPath, paths, CertificateStore(http), isAdmin || rootUnchanged);
            if (problem is { Status: 403 }) return Forbidden(problem.Message);
            if (problem is not null) v.Add(fieldPrefix + "rootPath", problem.Message);
        }
        foreach (var (field, message) in TargetProblems(host, Guard(store, includeUi: !isAdmin)))
            v.Add(fieldPrefix + field, message);
        return v.IsValid ? null : v.ToResult();
    }

    /// <summary>Upstream / location / advanced-route targets that point at protected endpoints on this server.</summary>
    public static IEnumerable<(string Field, string Message)> TargetProblems(SiteHost host, LocalEndpointGuard guard)
    {
        if (host.Kind == HostKind.Proxy)
        {
            for (var i = 0; i < host.Upstreams.Count; i++)
                if (guard.Check(host.Upstreams[i].Host, host.Upstreams[i].Port) is { } m) yield return ($"upstreams[{i}].host", m);
            for (var l = 0; l < host.Locations.Count; l++)
                for (var i = 0; i < host.Locations[l].Upstreams.Count; i++)
                    if (guard.Check(host.Locations[l].Upstreams[i].Host, host.Locations[l].Upstreams[i].Port) is { } m)
                        yield return ($"locations[{l}].upstreams[{i}].host", m);
        }
        foreach (var m in guard.CheckRoutesJson(host.AdvancedRoutesJson))
            yield return ("advancedRoutesJson", m);
    }

    /// <summary>Semantic comparison (whitespace/formatting changes are not changes).</summary>
    public static bool AdvancedRoutesChanged(string? next, string? previous)
    {
        if (string.IsNullOrWhiteSpace(next)) next = null;
        if (string.IsNullOrWhiteSpace(previous)) previous = null;
        if (next is null || previous is null) return next != previous;
        try
        {
            return !System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(next), System.Text.Json.Nodes.JsonNode.Parse(previous));
        }
        catch (System.Text.Json.JsonException)
        {
            return !string.Equals(next.Trim(), previous.Trim(), StringComparison.Ordinal);
        }
    }

    /// <summary>Enabled hosts and streams that would target a protected endpoint under the given guard.</summary>
    public static List<string> ExistingTargetProblems(IStore store, LocalEndpointGuard guard)
    {
        var list = new List<string>();
        foreach (var h in store.Col<SiteHost>().FindAll().Where(h => h.Enabled))
        {
            var first = TargetProblems(h, guard).Select(p => p.Message).FirstOrDefault();
            if (first is not null) list.Add($"host '{h.Domains.FirstOrDefault() ?? h.Id}': {first}");
        }
        foreach (var st in store.Col<StreamHost>().FindAll().Where(s => s.Enabled))
        {
            if (guard.Check(st.UpstreamHost, st.UpstreamPort) is { } m)
                list.Add($"stream {st.Protocol.ToString().ToLowerInvariant()}/{st.ListenPort}: {m}");
        }
        return list;
    }
}
