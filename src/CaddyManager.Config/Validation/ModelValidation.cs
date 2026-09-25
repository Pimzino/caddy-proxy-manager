using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;

namespace CaddyManager.Config.Validation;

/// <summary>Normalisation + validation of Config entities. Returns null when valid, otherwise a problem result.</summary>
public static partial class ModelValidation
{
    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\|/)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();

    private static bool HasCrLf(string? v) => v is not null && (v.Contains('\r') || v.Contains('\n'));

    /// <summary>Absolute path in Windows (C:\..., \\server\share) or Unix form — accepted regardless of the OS the manager runs on.</summary>
    public static bool IsAbsolutePath(string? p) => !string.IsNullOrWhiteSpace(p) && AbsolutePathRegex().IsMatch(p.Trim());

    // ------------------------------------------------------------------ hosts

    public static void Normalize(SiteHost h)
    {
        var domains = new List<string>();
        foreach (var d in h.Domains ?? [])
        {
            var n = NetUtil.NormalizeDomain(d);
            if (n is not null && !domains.Contains(n)) domains.Add(n);
        }
        h.Domains = domains;
        h.Upstreams ??= new();
        h.Locations ??= new();
        h.RequestHeaders ??= new();
        h.ResponseHeaders ??= new();
        h.HealthCheck ??= new();
        foreach (var u in h.Upstreams) u.Host = (u.Host ?? "").Trim();
        foreach (var l in h.Locations)
        {
            l.Upstreams ??= new();
            foreach (var u in l.Upstreams) u.Host = (u.Host ?? "").Trim();
            l.Path = CaddyConfigGenerator.NormalizeLocationPath(l.Path);
        }
        foreach (var op in h.RequestHeaders.Concat(h.ResponseHeaders)) { op.Name = (op.Name ?? "").Trim(); op.Value ??= ""; }
        h.CertificateId = string.IsNullOrWhiteSpace(h.CertificateId) ? null : h.CertificateId.Trim();
        h.AccessListId = string.IsNullOrWhiteSpace(h.AccessListId) ? null : h.AccessListId.Trim();
        h.AdvancedRoutesJson = string.IsNullOrWhiteSpace(h.AdvancedRoutesJson) ? null : h.AdvancedRoutesJson.Trim();
        h.UpstreamHostHeader = string.IsNullOrWhiteSpace(h.UpstreamHostHeader) ? null : h.UpstreamHostHeader.Trim();
        h.RedirectTarget = h.RedirectTarget?.Trim();
        h.RootPath = h.RootPath?.Trim();
        h.ResponseContentType = string.IsNullOrWhiteSpace(h.ResponseContentType) ? "text/plain; charset=utf-8" : h.ResponseContentType.Trim();
        if (h.Tls != TlsMode.Custom) h.CertificateId = null;
    }

    public static IResult? Validate(SiteHost h, IStore store)
    {
        var v = new Validator();
        if (h.Domains.Count == 0) v.Add("domains", "At least one domain is required.");
        foreach (var d in h.Domains)
            if (!NetUtil.IsValidDomain(d)) v.Add("domains", $"'{d}' is not a valid host name (letters, digits, hyphens; a leading '*.' wildcard is allowed).");

        switch (h.Kind)
        {
            case HostKind.Proxy:
                ValidateUpstreams(v, "upstreams", h.Upstreams, required: true);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < h.Locations.Count; i++)
                {
                    var l = h.Locations[i];
                    if (!paths.Add(l.Path)) v.Add($"locations[{i}].path", $"Location '{l.Path}' is defined more than once.");
                    if (l.Path.Contains('*') || l.Path.Contains(' ')) v.Add($"locations[{i}].path", "Location paths may not contain '*' or spaces.");
                    ValidateUpstreams(v, $"locations[{i}].upstreams", l.Upstreams, required: true);
                }
                if (h.HealthCheck.Enabled)
                {
                    if (string.IsNullOrWhiteSpace(h.HealthCheck.Path) || !h.HealthCheck.Path.Trim().StartsWith('/'))
                        v.Add("healthCheck.path", "Health check path must start with '/'.");
                    if (h.HealthCheck.IntervalSeconds is < 1 or > 86400) v.Add("healthCheck.intervalSeconds", "Interval must be 1-86400 seconds.");
                    if (h.HealthCheck.TimeoutSeconds is < 1 or > 3600) v.Add("healthCheck.timeoutSeconds", "Timeout must be 1-3600 seconds.");
                    if (h.HealthCheck.ExpectStatus != 0 && h.HealthCheck.ExpectStatus is < 100 or > 599)
                        v.Add("healthCheck.expectStatus", "Expected status must be 0 (any 2xx) or 100-599.");
                }
                if (HasCrLf(h.UpstreamHostHeader)) v.Add("upstreamHostHeader", "Host header may not contain line breaks.");
                ValidateHeaders(v, "requestHeaders", h.RequestHeaders);
                break;
            case HostKind.Redirect:
                if (!NetUtil.IsHttpUrl(h.RedirectTarget)) v.Add("redirectTarget", "Redirect target must be an absolute http:// or https:// URL.");
                if (h.RedirectCode is not (301 or 302 or 303 or 307 or 308)) v.Add("redirectCode", "Redirect code must be 301, 302, 303, 307 or 308.");
                break;
            case HostKind.Static:
                if (string.IsNullOrWhiteSpace(h.RootPath)) v.Add("rootPath", "Root folder is required.");
                else if (!IsAbsolutePath(h.RootPath)) v.Add("rootPath", "Root folder must be an absolute path, e.g. C:\\sites\\example.");
                break;
            case HostKind.Response:
                if (h.ResponseStatus is < 100 or > 599) v.Add("responseStatus", "Status must be 100-599.");
                if (HasCrLf(h.ResponseContentType)) v.Add("responseContentType", "Content type may not contain line breaks.");
                break;
        }

        ValidateHeaders(v, "responseHeaders", h.ResponseHeaders);
        if (h.HstsMaxAgeSeconds < 0) v.Add("hstsMaxAgeSeconds", "HSTS max-age cannot be negative.");

        if (h.Tls == TlsMode.Custom)
        {
            if (h.CertificateId is null) v.Add("certificateId", "Select a certificate for custom TLS.");
            else if (store.Col<Certificate>().FindById(h.CertificateId) is null) v.Add("certificateId", "The selected certificate does not exist.");
        }
        if (h.AccessListId is not null && store.Col<AccessList>().FindById(h.AccessListId) is null)
            v.Add("accessListId", "The selected access list does not exist.");

        if (h.AdvancedRoutesJson is not null)
        {
            try
            {
                if (JsonNode.Parse(h.AdvancedRoutesJson) is not JsonArray arr) v.Add("advancedRoutesJson", "Advanced routes must be a JSON array of Caddy route objects.");
                else if (arr.Any(x => x is not JsonObject)) v.Add("advancedRoutesJson", "Every entry of the advanced routes array must be a JSON object (a Caddy route).");
            }
            catch (JsonException ex)
            {
                v.Add("advancedRoutesJson", "Invalid JSON: " + ex.Message);
            }
        }

        if (!v.IsValid) return v.ToResult();
        return DomainConflict(h, store);
    }

    /// <summary>409 when an enabled host already serves one of the domains.</summary>
    public static IResult? DomainConflict(SiteHost h, IStore store)
    {
        if (!h.Enabled) return null;
        var mine = h.Domains.Select(d => NetUtil.NormalizeDomain(d)).Where(d => d is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var other in store.Col<SiteHost>().FindAll().Where(x => x.Enabled && x.Id != h.Id))
        {
            var dup = other.Domains.Select(d => NetUtil.NormalizeDomain(d)).FirstOrDefault(d => d is not null && mine.Contains(d));
            if (dup is not null)
            {
                var name = other.Domains.FirstOrDefault() ?? other.Id;
                return ApiResults.Conflict($"The domain '{dup}' is already used by the enabled host '{name}' (id {other.Id}). A domain can belong to only one enabled host.");
            }
        }
        return null;
    }

    private static void ValidateUpstreams(Validator v, string field, List<Upstream> ups, bool required)
    {
        if (required && ups.Count == 0) { v.Add(field, "At least one upstream (host and port) is required."); return; }
        for (var i = 0; i < ups.Count; i++)
        {
            var u = ups[i];
            if (u.Host.Contains("://")) v.Add($"{field}[{i}].host", "Enter only the host name or IP; choose the scheme separately.");
            else if (!NetUtil.IsValidHost(u.Host)) v.Add($"{field}[{i}].host", $"'{u.Host}' is not a valid host name or IP address.");
            if (!NetUtil.IsValidPort(u.Port)) v.Add($"{field}[{i}].port", "Port must be 1-65535.");
        }
        if (ups.Select(u => u.Scheme).Distinct().Count() > 1)
            v.Add(field, "All upstreams must use the same scheme (http or https): Caddy uses one transport per proxy.");
    }

    private static void ValidateHeaders(Validator v, string field, List<HeaderOp> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            if (!NetUtil.IsValidHeaderName(ops[i].Name)) v.Add($"{field}[{i}].name", $"'{ops[i].Name}' is not a valid header name.");
            if (HasCrLf(ops[i].Value)) v.Add($"{field}[{i}].value", "Header values may not contain line breaks.");
        }
    }

    // ------------------------------------------------------------------ streams

    public static void Normalize(StreamHost s)
    {
        s.UpstreamHost = (s.UpstreamHost ?? "").Trim();
        s.Notes = string.IsNullOrWhiteSpace(s.Notes) ? null : s.Notes.Trim();
    }

    public static IResult? Validate(StreamHost s, IStore store)
    {
        var v = new Validator();
        if (!NetUtil.IsValidPort(s.ListenPort)) v.Add("listenPort", "Listen port must be 1-65535.");
        if (!NetUtil.IsValidHost(s.UpstreamHost)) v.Add("upstreamHost", "Upstream host must be a host name or IP address.");
        if (!NetUtil.IsValidPort(s.UpstreamPort)) v.Add("upstreamPort", "Upstream port must be 1-65535.");
        if (!v.IsValid) return v.ToResult();
        if (!s.Enabled) return null;

        var settings = store.GetSettings<CaddySettings>();
        if (s.Protocol == StreamProtocol.Tcp && (s.ListenPort == settings.HttpPort || s.ListenPort == settings.HttpsPort))
            return ApiResults.Conflict($"TCP port {s.ListenPort} is used by Caddy's HTTP/HTTPS listener.");
        if (s.Protocol == StreamProtocol.Udp && settings.EnableHttp3 && s.ListenPort == settings.HttpsPort)
            return ApiResults.Conflict($"UDP port {s.ListenPort} is used by HTTP/3. Disable HTTP/3 or choose another port.");
        var other = store.Col<StreamHost>().FindAll().FirstOrDefault(x => x.Enabled && x.Id != s.Id && x.Protocol == s.Protocol && x.ListenPort == s.ListenPort);
        if (other is not null)
            return ApiResults.Conflict($"{s.Protocol.ToString().ToUpperInvariant()} port {s.ListenPort} is already used by another enabled stream (id {other.Id}).");
        return null;
    }

    // ------------------------------------------------------------------ settings

    public static IResult? Validate(CaddySettings s)
    {
        var v = new Validator();
        if (!NetUtil.IsValidPort(s.HttpPort)) v.Add("httpPort", "HTTP port must be 1-65535.");
        if (!NetUtil.IsValidPort(s.HttpsPort)) v.Add("httpsPort", "HTTPS port must be 1-65535.");
        if (s.HttpPort == s.HttpsPort) v.Add("httpsPort", "HTTP and HTTPS ports must differ.");
        if (!NetUtil.IsLoopbackListen(s.AdminListen, out var adminErr)) v.Add("adminListen", adminErr!);
        if (s.LogLevel is null || s.LogLevel.Trim().ToLowerInvariant() is not ("debug" or "info" or "warn" or "error"))
            v.Add("logLevel", "Log level must be debug, info, warn or error.");
        if (!string.IsNullOrWhiteSpace(s.AcmeEmail) && !System.Net.Mail.MailAddress.TryCreate(s.AcmeEmail.Trim(), out _))
            v.Add("acmeEmail", "Enter a valid e-mail address.");
        if (s.AcmeCa == AcmeCa.Custom && !NetUtil.IsHttpUrl(s.CustomAcmeDirectory))
            v.Add("customAcmeDirectory", "A custom ACME CA needs its directory URL (https://.../directory).");
        if (!string.IsNullOrWhiteSpace(s.CustomAcmeRootPath) && !IsAbsolutePath(s.CustomAcmeRootPath))
            v.Add("customAcmeRootPath", "Root certificate path must be absolute.");
        if (!string.IsNullOrWhiteSpace(s.EabKeyId) != !string.IsNullOrWhiteSpace(s.EabMacKeyProtected))
            v.Add("eabKeyId", "External account binding needs both the key ID and the MAC key.");
        if (s.AcmeCa == AcmeCa.ZeroSsl && string.IsNullOrWhiteSpace(s.AcmeEmail) && string.IsNullOrWhiteSpace(s.EabKeyId))
            v.Add("acmeEmail", "ZeroSSL needs an e-mail address (or EAB credentials).");
        if (s.DisableHttpChallenge && s.DisableTlsAlpnChallenge)
            v.Add("disableTlsAlpnChallenge", "At least one ACME challenge (HTTP or TLS-ALPN) must stay enabled.");
        for (var i = 0; i < s.BindAddresses.Count; i++)
            if (!NetUtil.IsValidBindAddress(s.BindAddresses[i])) v.Add($"bindAddresses[{i}]", $"'{s.BindAddresses[i]}' is not an IP address.");
        for (var i = 0; i < s.TrustedProxies.Count; i++)
            if (!NetUtil.IsValidCidr(s.TrustedProxies[i]) || s.TrustedProxies[i].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                v.Add($"trustedProxies[{i}]", $"'{s.TrustedProxies[i]}' is not an IP address or CIDR range.");
        if (s.DefaultSite == DefaultSiteBehavior.Redirect && !NetUtil.IsHttpUrl(s.DefaultRedirectUrl))
            v.Add("defaultRedirectUrl", "Enter the absolute http(s) URL unknown hosts are redirected to.");
        if (!string.IsNullOrWhiteSpace(s.CertificateStorePath) && !IsAbsolutePath(s.CertificateStorePath))
            v.Add("certificateStorePath", "Certificate store must be an absolute path or UNC share (\\\\server\\share\\certs).");
        if (s.Mode == ConfigMode.Caddyfile && string.IsNullOrWhiteSpace(s.RawCaddyfile))
            v.Add("rawCaddyfile", "Caddyfile mode needs a Caddyfile.");
        if (!string.IsNullOrWhiteSpace(s.ServerOptionsJson))
        {
            try
            {
                if (JsonNode.Parse(s.ServerOptionsJson) is not JsonObject) v.Add("serverOptionsJson", "Server options must be a JSON object.");
            }
            catch (JsonException ex)
            {
                v.Add("serverOptionsJson", "Invalid JSON: " + ex.Message);
            }
        }
        return v.IsValid ? null : v.ToResult();
    }
}
