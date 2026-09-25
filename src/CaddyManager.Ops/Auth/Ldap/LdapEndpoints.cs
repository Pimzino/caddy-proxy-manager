using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Ops.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Ops.Auth.Ldap;

public sealed record LdapTestRequest(string? Username, string? Password);

/// <summary>GET/PUT /api/settings/ldap and POST /api/settings/ldap/test (admin).</summary>
internal static class LdapEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/ldap").RequireAuthorization(Policies.Admin);

        g.MapGet("/", (IStore store) => Results.Json(SettingsWire.ToWire(store.GetSettings<LdapSettings>())));

        g.MapPut("/", (JsonObject body, IStore store, ISecretProtector secrets, IAuditLog audit) =>
        {
            var current = store.GetSettings<LdapSettings>();
            LdapSettings next;
            try { next = SettingsWire.Apply(current, body, secrets); }
            catch (SettingsInputException ex) { return ApiResults.BadRequest(ex.Message); }

            Normalize(next);
            var v = Validate(next);
            if (!v.IsValid) return v.ToResult();

            store.SaveSettings(next);
            var changes = new List<string>();
            if (current.Enabled != next.Enabled) changes.Add(next.Enabled ? "LDAP sign-in enabled" : "LDAP sign-in disabled");
            if (SettingsWire.TouchesSecret(body, "bindPassword")) changes.Add("bind password changed");
            audit.Record("updated", "settings", "ldap", "LDAP settings", changes.Count > 0 ? string.Join("; ", changes) : null);
            return Results.Json(SettingsWire.ToWire(next));
        });

        g.MapPost("/test", async (LdapTestRequest req, LdapSignIn ldap, IAuditLog audit, CancellationToken ct) =>
        {
            var s = ldap.Settings();
            if (string.IsNullOrWhiteSpace(s.Server) || string.IsNullOrWhiteSpace(s.BaseDn))
                return ApiResults.BadRequest("Configure and save the server and base DN before testing.");
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return ApiResults.BadRequest("Enter the user name and password of a directory account to test with.",
                    new Dictionary<string, string[]>
                    {
                        [string.IsNullOrWhiteSpace(req.Username) ? "username" : "password"] = ["Required."],
                    });

            var r = await ldap.AuthenticateAsync(s, req.Username, req.Password, ct);
            var warnings = new List<string>();
            if (s.Security == LdapSecurity.None)
                warnings.Add("Security is 'none': passwords are sent to the directory in clear text. Use startTls or ldaps in production.");
            if (s.AllowInvalidCertificate && s.Security != LdapSecurity.None)
                warnings.Add("The directory's TLS certificate is not validated ('Allow invalid certificate').");
            if (!s.Enabled) warnings.Add("LDAP sign-in is currently disabled; enable it to let directory users sign in.");

            var error = r.Outcome switch
            {
                LdapOutcome.Success => null,
                LdapOutcome.InvalidCredentials => $"Invalid credentials: {r.Message}.",
                _ => r.Message,
            };
            audit.Record("test", "settings", "ldap", "LDAP settings",
                $"Test sign-in as '{req.Username!.Trim()}': {(error is null ? $"ok, role {CpmClaims.RoleValue(r.Role!.Value)}" : error)}");
            return Results.Ok(new
            {
                ok = r.Outcome == LdapOutcome.Success,
                role = r.Role is { } role ? CpmClaims.RoleValue(role) : null,
                displayName = r.DisplayName,
                email = r.Email,
                dn = r.Dn,
                groups = r.Dn is null ? null : r.Groups,
                error,
                warnings,
            });
        }).RequireLoginThrottle();
    }

    private static void Normalize(LdapSettings s)
    {
        s.Server = (s.Server ?? "").Trim();
        s.BaseDn = (s.BaseDn ?? "").Trim();
        s.UserFilter = string.IsNullOrWhiteSpace(s.UserFilter) ? LdapSettings.DefaultUserFilter : s.UserFilter.Trim();
        s.BindDn = Blank(s.BindDn);
        s.AdminGroupDn = Blank(s.AdminGroupDn);
        s.OperatorGroupDn = Blank(s.OperatorGroupDn);
        s.ViewerGroupDn = Blank(s.ViewerGroupDn);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    internal static Validator Validate(LdapSettings s)
    {
        var v = new Validator();
        v.Require(s.Port is >= 1 and <= 65535, "port", "Port must be between 1 and 65535.");
        v.Require(s.TimeoutSeconds is >= 1 and <= 120, "timeoutSeconds", "Timeout must be between 1 and 120 seconds.");
        if (s.Server.Contains("://", StringComparison.Ordinal))
            v.Add("server", "Enter the host name only (for example dc01.corp.example.com), without ldap:// or ldaps://; choose the security mode separately.");
        else if (s.Server.Length > 0 && (s.Server.Any(char.IsWhiteSpace) || s.Server.Contains('/')))
            v.Add("server", "The server must be a host name or IP address.");
        if (s.Security == LdapSecurity.Ldaps && s.Port is 389 or 3268)
            v.Add("port", $"Port {s.Port} is plain LDAP; LDAPS uses 636 (or 3269 for the global catalog).");
        if (s.Security != LdapSecurity.Ldaps && s.Port is 636 or 3269)
            v.Add("port", $"Port {s.Port} is LDAPS; set security to 'ldaps' or use port 389 with 'startTls'.");

        if (!s.UserFilter.Contains("{0}", StringComparison.Ordinal))
            v.Add("userFilter", "The user filter must contain {0} where the user name is inserted.");
        else if (!s.UserFilter.StartsWith('(') || !s.UserFilter.EndsWith(')') || !Balanced(s.UserFilter))
            v.Add("userFilter", "The user filter must be a parenthesised LDAP filter with balanced parentheses.");
        if (s.BaseDn.Length > 0 && !s.BaseDn.Contains('='))
            v.Add("baseDn", "The base DN must be a distinguished name such as DC=corp,DC=example,DC=com.");
        foreach (var (field, dn) in new[] { ("adminGroupDn", s.AdminGroupDn), ("operatorGroupDn", s.OperatorGroupDn), ("viewerGroupDn", s.ViewerGroupDn) })
            if (dn is not null && !dn.Contains('='))
                v.Add(field, "Enter the group's distinguished name, e.g. CN=CPM Admins,OU=Groups,DC=corp,DC=example,DC=com.");
        if (s.BindDn is not null && string.IsNullOrEmpty(s.BindPasswordProtected))
            v.Add("bindPassword", "Enter the password of the bind account.");

        if (s.Enabled)
        {
            v.Require(s.Server.Length > 0, "server", "The LDAP server is required when LDAP sign-in is enabled.");
            v.Require(s.BaseDn.Length > 0, "baseDn", "The base DN is required when LDAP sign-in is enabled.");
            v.Require(s.AdminGroupDn is not null || s.OperatorGroupDn is not null || s.ViewerGroupDn is not null, "adminGroupDn",
                "Map at least one group to a role; directory users without a mapped group cannot sign in.");
        }
        return v;
    }

    private static bool Balanced(string filter)
    {
        var depth = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            if (filter[i] == '\\') { i++; continue; }
            if (filter[i] == '(') depth++;
            else if (filter[i] == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
