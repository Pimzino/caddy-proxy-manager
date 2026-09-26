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
        if (h.Tls != TlsMode.Acme) h.AcmeChallenge = HostAcmeChallenge.Default;
        // Like the challenge choice, delegation only exists for ACME hosts: other TLS modes drop it silently. For ACME hosts
        // whose challenge is not DNS, a non-default choice is a field error instead (see ValidateFields), because the
        // effective challenge also depends on the settings and the user should notice the delegation is not used.
        if (h.Tls != TlsMode.Acme) h.DnsDelegation = HostDnsDelegation.Default;
        h.DnsOverrideDomain = h.DnsDelegation == HostDnsDelegation.Custom ? NetUtil.NormalizeDomain(h.DnsOverrideDomain) : null;
    }

    /// <summary>The field error text for an invalid delegation record name.</summary>
    private static string DelegationNameError(string name) => name.Contains('*')
        ? $"'{name}' contains a wildcard; the delegation name is a single DNS record, e.g. _acme-challenge.validation.example.net."
        : $"'{name}' is not a valid DNS name. Enter the record the CNAMEs point to, e.g. _acme-challenge.validation.example.net.";

    /// <summary>Validator that prefixes field names (e.g. "hosts[2]." for bulk imports).</summary>
    internal sealed class FieldErrors(Validator target, string prefix)
    {
        public void Add(string field, string message) => target.Add(prefix + field, message);
    }

    /// <param name="modules">Modules of the installed Caddy binary (null = unknown: the DNS provider plugin is not checked).</param>
    public static IResult? Validate(SiteHost h, IStore store, ISecretProtector? secrets = null, IReadOnlyCollection<string>? modules = null)
    {
        var v = new Validator();
        ValidateFields(h, store, v, "", secrets, modules);
        if (!v.IsValid) return v.ToResult();
        return DomainConflict(h, store);
    }

    /// <summary>Field validation of a host (no conflict check); errors are added to <paramref name="target"/> with the prefix.</summary>
    /// <param name="secrets">When given, the DNS provider's required secret fields are checked for hosts using the DNS challenge.</param>
    internal static void ValidateFields(SiteHost h, IStore store, Validator target, string prefix, ISecretProtector? secrets = null,
        IReadOnlyCollection<string>? modules = null)
    {
        var v = new FieldErrors(target, prefix);
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
                    // 1-5 is a status class (3 = any 3xx), as Caddy's expect_status supports.
                    if (h.HealthCheck.ExpectStatus is not (>= 0 and <= 5) and not (>= 100 and <= 599))
                        v.Add("healthCheck.expectStatus", "Expected status must be 0 (any 2xx), a class 1-5 (e.g. 3 = any 3xx) or a code 100-599.");
                }
                if (HasCrLf(h.UpstreamHostHeader)) v.Add("upstreamHostHeader", "Host header may not contain line breaks.");
                if (h.UpstreamNtlm && h.AccessListId is not null
                    && store.Col<AccessList>().FindById(h.AccessListId) is { } ntlmList && ntlmList.Users.Count > 0)
                    v.Add("accessListId", "This access list asks for a user name and password (basic auth). NTLM/Negotiate logins use the same Authorization header, so Windows authentication cannot work behind it. Use an access list with IP rules only.");
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
                // 1xx codes are informational; Caddy would send them and then an implicit 200.
                if (h.ResponseStatus is < 200 or > 599) v.Add("responseStatus", "Status must be 200-599.");
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
        if (h.Tls == TlsMode.Acme && h.AcmeChallenge == HostAcmeChallenge.Dns)
        {
            var settings = store.GetSettings<CaddySettings>();
            if (!CaddyConfigGenerator.DnsProviderConfigured(settings))
                v.Add("acmeChallenge", "The DNS challenge needs a DNS provider: configure it under Settings > Caddy (ACME challenge) first.");
            else if (DnsProviders.DnsProviderCatalog.Find(settings.DnsProvider) is { } provider)
            {
                var missing = MissingProviderFields(settings, secrets is null ? null : Services.SettingsSecrets.Read(settings, secrets).DnsProviderSecrets);
                if (missing.Count > 0)
                    v.Add("acmeChallenge", $"The {provider.Label} DNS provider settings are incomplete (missing: {string.Join(", ", missing)}). Complete them under Settings > Caddy.");
            }
        }
        // Caddy rejects the whole configuration when a DNS policy names a provider module the binary does not include.
        if (modules is not null && CaddyConfigGenerator.UsesDnsChallenge(h, store.GetSettings<CaddySettings>())
            && DnsProviderModuleProblem(store.GetSettings<CaddySettings>().DnsProvider, modules) is { } moduleProblem)
            v.Add("acmeChallenge", moduleProblem);
        if (h.DnsDelegation != HostDnsDelegation.Default && !CaddyConfigGenerator.UsesDnsChallenge(h, store.GetSettings<CaddySettings>()))
            v.Add("dnsDelegation", "Challenge delegation only applies to the DNS challenge, and this host uses the HTTP challenge. Select the DNS challenge, or set delegation back to 'Use default'.");
        if (h.DnsDelegation == HostDnsDelegation.Custom)
        {
            if (h.DnsOverrideDomain is null)
                v.Add("dnsOverrideDomain", "Enter the delegation name the _acme-challenge CNAME records point to, e.g. _acme-challenge.validation.example.net.");
            else if (!NetUtil.IsValidDnsName(h.DnsOverrideDomain))
                v.Add("dnsOverrideDomain", DelegationNameError(h.DnsOverrideDomain));
        }

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

    private static void ValidateUpstreams(FieldErrors v, string field, List<Upstream> ups, bool required)
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

    private static void ValidateHeaders(FieldErrors v, string field, List<HeaderOp> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            if (!NetUtil.IsValidHeaderName(ops[i].Name))
                v.Add($"{field}[{i}].name", ops[i].Name.Contains('*')
                    ? $"'{ops[i].Name}' is not allowed: Caddy treats '*' in a header name as a wildcard (\"*\" would delete every header)."
                    : $"'{ops[i].Name}' is not a valid header name.");
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

        var settings = store.GetSettings<CaddySettings>();
        var ui = store.GetSettings<UiSettings>();
        if (LocalEndpointGuard.Create(settings, ui).Check(s.UpstreamHost, s.UpstreamPort) is { } targetProblem)
        {
            v.Add("upstreamHost", targetProblem);
            return v.ToResult();
        }
        if (!s.Enabled) return null;

        if (s.Protocol == StreamProtocol.Tcp && (s.ListenPort == settings.HttpPort || s.ListenPort == settings.HttpsPort))
            return ApiResults.Conflict($"TCP port {s.ListenPort} is used by Caddy's HTTP/HTTPS listener.");
        if (s.Protocol == StreamProtocol.Tcp)
        {
            foreach (var (port, what) in LocalEndpointGuard.ProtectedPortsFor(settings, ui))
                if (s.ListenPort == port) return ApiResults.Conflict($"TCP port {s.ListenPort} is used by {what}. Choose another listen port.");
        }
        if (s.Protocol == StreamProtocol.Udp && settings.EnableHttp3 && s.ListenPort == settings.HttpsPort)
            return ApiResults.Conflict($"UDP port {s.ListenPort} is used by HTTP/3. Disable HTTP/3 or choose another port.");
        var other = store.Col<StreamHost>().FindAll().FirstOrDefault(x => x.Enabled && x.Id != s.Id && x.Protocol == s.Protocol && x.ListenPort == s.ListenPort);
        if (other is not null)
            return ApiResults.Conflict($"{s.Protocol.ToString().ToUpperInvariant()} port {s.ListenPort} is already used by another enabled stream (id {other.Id}).");
        return null;
    }

    // ------------------------------------------------------------------ settings

    public static IResult? Validate(CaddySettings s, string? acmeIssuerJson = null, UiSettings? ui = null)
    {
        var v = new Validator();
        JsonObject? issuerExtra = null;
        if (!string.IsNullOrWhiteSpace(acmeIssuerJson))
        {
            issuerExtra = CaddyJson.ParseObject(acmeIssuerJson, out var issuerError);
            if (issuerError is not null) v.Add("acmeIssuerJson", "The ACME issuer JSON " + issuerError + ".");
            else if (issuerExtra!.ContainsKey("module")) v.Add("acmeIssuerJson", "The ACME issuer JSON may not set 'module' (the manager always uses the acme issuer).");
        }
        CaddyConfigGenerator.DnsProviderName(issuerExtra, out var hasDnsChallenge);
        if (!NetUtil.IsValidPort(s.HttpPort)) v.Add("httpPort", "HTTP port must be 1-65535.");
        if (!NetUtil.IsValidPort(s.HttpsPort)) v.Add("httpsPort", "HTTPS port must be 1-65535.");
        if (s.HttpPort == s.HttpsPort) v.Add("httpsPort", "HTTP and HTTPS ports must differ.");
        if (s.PublicHttpsPort is int publicPort && !NetUtil.IsValidPort(publicPort))
            v.Add("publicHttpsPort", "Public HTTPS port must be 1-65535, or empty to use the HTTPS port.");
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
        if (s.DisableHttpChallenge && s.DisableTlsAlpnChallenge && !hasDnsChallenge
            && !(s.DefaultAcmeChallenge == AcmeChallengeType.Dns && CaddyConfigGenerator.DnsProviderConfigured(s)))
            v.Add("disableTlsAlpnChallenge", "At least one ACME challenge (HTTP or TLS-ALPN) must stay enabled unless the DNS challenge is the default (Settings > Caddy > ACME challenge) or configured in the ACME issuer JSON.");
        if (ui is not null)
        {
            if (s.HttpPort == ui.Port || (ui.HttpsEnabled && s.HttpPort == ui.HttpsPort))
                v.Add("httpPort", $"Port {s.HttpPort} is used by the Caddy Proxy Manager web UI (Settings > UI).");
            if (s.HttpsPort == ui.Port || (ui.HttpsEnabled && s.HttpsPort == ui.HttpsPort))
                v.Add("httpsPort", $"Port {s.HttpsPort} is used by the Caddy Proxy Manager web UI (Settings > UI).");
            if (LocalEndpointGuard.TryParseListenPort(s.AdminListen, out var adminPort) && (adminPort == ui.Port || (ui.HttpsEnabled && adminPort == ui.HttpsPort)))
                v.Add("adminListen", $"Port {adminPort} is used by the Caddy Proxy Manager web UI (Settings > UI).");
        }
        if (LocalEndpointGuard.TryParseListenPort(s.AdminListen, out var admin) && (admin == s.HttpPort || admin == s.HttpsPort))
            v.Add("adminListen", $"The admin API port {admin} is also used for HTTP/HTTPS sites.");
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
        if (!string.IsNullOrWhiteSpace(s.ExtraAppsJson))
        {
            var apps = CaddyJson.ParseObject(s.ExtraAppsJson, out var appsError);
            if (appsError is not null) v.Add("extraAppsJson", "The extra apps JSON " + appsError + ", keyed by app name.");
            else
            {
                foreach (var (name, value) in apps!)
                {
                    if (CaddyConfigGenerator.ReservedApps.Contains(name, StringComparer.Ordinal))
                        v.Add("extraAppsJson", $"The app '{name}' is generated by Caddy Proxy Manager and cannot be set here (reserved: {string.Join(", ", CaddyConfigGenerator.ReservedApps)}).");
                    else if (value is not JsonObject)
                        v.Add("extraAppsJson", $"The app '{name}' must be a JSON object.");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(s.TlsConnectionPolicyJson))
        {
            var policy = CaddyJson.ParseObject(s.TlsConnectionPolicyJson, out var policyError);
            if (policyError is not null) v.Add("tlsConnectionPolicyJson", "The TLS connection policy JSON " + policyError + ".");
            else
            {
                foreach (var key in CaddyConfigGenerator.ReservedConnectionPolicyKeys.Where(policy!.ContainsKey))
                    v.Add("tlsConnectionPolicyJson", $"'{key}' is managed by Caddy Proxy Manager and cannot be set in the TLS connection policy JSON.");
            }
        }
        return v.IsValid ? null : v.ToResult();
    }

    // ------------------------------------------------------------------ Round 3: DNS challenge + storage

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex("^[a-z0-9_.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageModuleRegex();

    /// <summary>Plugins that provide common storage modules (docs/research/round3-cluster.md §2).</summary>
    private static readonly Dictionary<string, string> StoragePlugins = new(StringComparer.Ordinal)
    {
        ["redis"] = CaddyConfigGenerator.RedisStoragePlugin,
        ["postgres"] = "github.com/yroc92/postgres-storage",
        ["consul"] = "github.com/pteich/caddy-tlsconsul",
        ["s3"] = "github.com/ss098/certmagic-s3",
    };

    /// <summary>Missing required fields of the configured catalog provider (options or secrets), e.g. ["api_token"].</summary>
    /// <param name="secrets">Plain secrets; null = unknown (secret fields are not checked).</param>
    public static List<string> MissingProviderFields(CaddySettings s, IReadOnlyDictionary<string, string>? secrets)
    {
        if (DnsProviders.DnsProviderCatalog.Find(s.DnsProvider) is not { } info) return [];
        return info.Fields.Where(f => f.Required && (f.Secret
                ? secrets is not null && (!secrets.TryGetValue(f.Name, out var sv) || string.IsNullOrWhiteSpace(sv))
                : !(s.DnsProviderOptions ?? new()).TryGetValue(f.Name, out var ov) || string.IsNullOrWhiteSpace(ov)))
            .Select(f => f.Name).ToList();
    }

    /// <summary>
    /// Validates the DNS challenge (provider name, typed fields, required fields when DNS is used by default or by an
    /// enabled ACME host, resolvers, durations) and the storage backend (FileSystem: absolute path the manager can write
    /// — tested only when <paramref name="storageChanged"/>; Redis: addresses; Custom: object with a "module"; plugin
    /// modules present when the installed binary is known).
    /// </summary>
    public static IResult? ValidateDnsAndStorage(CaddySettings s, Services.SettingsSecrets plain, IStore store,
        IReadOnlyCollection<string>? modules, bool storageChanged)
    {
        var v = new Validator();
        var provider = DnsProviders.DnsProviderCatalog.Normalize(s.DnsProvider);
        var info = DnsProviders.DnsProviderCatalog.Find(provider);
        if (provider is not null && !DnsProviders.DnsProviderCatalog.IsValidName(provider))
            v.Add("dnsProvider", "Enter the provider's module name (lower-case letters, digits and '_'), e.g. cloudflare for dns.providers.cloudflare.");
        foreach (var (key, value) in s.DnsProviderOptions ?? new())
        {
            if (!FieldNameRegex().IsMatch(key)) { v.Add($"dnsProviderOptions.{key}", $"'{key}' is not a valid provider field name."); continue; }
            if (key == "name") { v.Add("dnsProviderOptions.name", "'name' is set by the provider selection."); continue; }
            if (info is null) continue;
            var field = info.Fields.FirstOrDefault(f => f.Name == key);
            if (field is null) v.Add($"dnsProviderOptions.{key}", $"'{key}' is not a field of the {info.Label} provider.");
            else if (field.Secret) v.Add($"dnsProviderOptions.{key}", $"'{key}' is a secret; send it in dnsProviderSecrets so it is stored encrypted.");
            else
            {
                DnsProviders.DnsProviderCatalog.Convert(field.Type, value, out var error);
                if (error is not null) v.Add($"dnsProviderOptions.{key}", $"{field.Label} {error}.");
            }
        }
        foreach (var key in plain.DnsProviderSecrets.Keys)
        {
            if (!FieldNameRegex().IsMatch(key) || key == "name") v.Add($"dnsProviderSecrets.{key}", $"'{key}' is not a valid provider field name.");
            else if (info is not null && info.Fields.FirstOrDefault(f => f.Name == key) is not { Secret: true })
                v.Add($"dnsProviderSecrets.{key}", $"'{key}' is not a secret field of the {info.Label} provider.");
        }

        var acmeHosts = store.Col<SiteHost>().FindAll().Where(h => h.Enabled && h.Tls == TlsMode.Acme).ToList();
        var dnsHost = acmeHosts.FirstOrDefault(h => h.AcmeChallenge == HostAcmeChallenge.Dns);
        var dnsUsed = s.DefaultAcmeChallenge == AcmeChallengeType.Dns || dnsHost is not null;
        if (dnsUsed && provider is null)
            v.Add("dnsProvider", s.DefaultAcmeChallenge == AcmeChallengeType.Dns
                ? "The DNS challenge is the default challenge: select the DNS provider that manages your zones."
                : $"The host '{dnsHost!.Domains.FirstOrDefault() ?? dnsHost.Id}' uses the DNS challenge: select the DNS provider that manages your zones.");
        // Wildcard ACME hosts use the provider as soon as one is selected.
        var providerUsed = provider is not null && (dnsUsed || acmeHosts.Any(h => h.Domains.Any(d => d.StartsWith("*.", StringComparison.Ordinal))));
        if (providerUsed)
        {
            foreach (var missing in MissingProviderFields(s, plain.DnsProviderSecrets))
            {
                var field = info!.Fields.First(f => f.Name == missing);
                v.Add(field.Secret ? $"dnsProviderSecrets.{missing}" : $"dnsProviderOptions.{missing}", $"{info.Label}: '{field.Label}' is required.");
            }
            // Caddy rejects the whole configuration (not just DNS-01 issuance) when the provider module is missing.
            if (DnsProviders.DnsProviderCatalog.IsValidName(provider) && DnsProviderModuleProblem(provider, modules) is { } moduleProblem)
                v.Add("dnsProvider", moduleProblem);
        }
        // A legacy DNS provider in the ACME issuer JSON would be deep-merged into the selected one (mixing two providers'
        // fields, which no plugin accepts); the generator drops it, but the user has to remove it.
        if (provider is not null && CaddyJson.ParseObject(plain.AcmeIssuerJson, out _)?["challenges"]?["dns"] is JsonObject legacyDns
            && legacyDns.ContainsKey("provider"))
            v.Add("acmeIssuerJson", "Remove challenges.dns.provider from the ACME issuer JSON: the DNS provider is now configured under ACME challenge (Settings > Caddy).");

        for (var i = 0; i < s.DnsResolvers.Count; i++)
            if (!IsHostPort(s.DnsResolvers[i]))
                v.Add($"dnsResolvers[{i}]", $"'{s.DnsResolvers[i]}' is not host:port (e.g. 1.1.1.1:53).");
        if (s.DnsPropagationDelaySeconds is < 0 or > 86400) v.Add("dnsPropagationDelaySeconds", "Propagation delay must be 0-86400 seconds.");
        if (s.DnsPropagationTimeoutSeconds is not null and (< -1 or > 86400))
            v.Add("dnsPropagationTimeoutSeconds", "Propagation timeout must be 1-86400 seconds, or -1 to skip the propagation check.");
        if (s.DnsTtlSeconds is < 0 or > 604800) v.Add("dnsTtlSeconds", "TTL must be 0-604800 seconds.");
        if (s.DnsOverrideDomain is { } od && !NetUtil.IsValidDnsName(od))
            v.Add("dnsOverrideDomain", DelegationNameError(od));

        switch (s.StorageBackend)
        {
            case StorageBackend.FileSystem:
                if (string.IsNullOrWhiteSpace(s.StoragePath))
                    v.Add("storagePath", "Enter the shared folder (local path or UNC share, e.g. \\\\fileserver\\caddy).");
                else if (!IsAbsolutePath(s.StoragePath) || !Path.IsPathFullyQualified(s.StoragePath))
                    v.Add("storagePath", "The storage folder must be an absolute path on this server or a UNC share (\\\\server\\share\\caddy). Mapped drive letters are not visible to services.");
                else if (storageChanged && Services.CaddyStorage.CheckWritable(s.StoragePath) is { } writeError)
                    v.Add("storagePath", $"The manager cannot write to '{s.StoragePath}': {writeError} On a share, grant the computer accounts (DOMAIN\\SERVER$) modify rights.");
                break;
            case StorageBackend.Redis:
                if (s.RedisAddresses.Count == 0) v.Add("redisAddresses", "Enter the Redis server address (host:port).");
                // caddy-storage-redis v1.8 creates a Redis Cluster client whenever it has more than one address (even with
                // client_type "simple"), which a normal primary/replica Redis rejects ("cluster support disabled").
                else if (s.RedisAddresses.Count > 1)
                    v.Add("redisAddresses", "Enter a single Redis address (host:port). Several addresses make the Redis storage plugin use a Redis Cluster client, which a normal (primary/replica) Redis rejects; Redis Cluster and Sentinel are not supported yet.");
                for (var i = 0; i < s.RedisAddresses.Count; i++)
                    if (!IsHostPort(s.RedisAddresses[i])) v.Add($"redisAddresses[{i}]", $"'{s.RedisAddresses[i]}' is not host:port (e.g. redis.corp.local:6379).");
                if (s.RedisDb < 0) v.Add("redisDb", "The Redis database index cannot be negative.");
                if (modules is not null && !modules.Contains(CaddyConfigGenerator.RedisStorageModule, StringComparer.Ordinal))
                    v.Add("storageBackend", $"The installed Caddy binary does not include Redis storage. Add the plugin '{CaddyConfigGenerator.RedisStoragePlugin}' under Caddy > Plugins and rebuild Caddy first.");
                break;
            case StorageBackend.Custom:
                var custom = CaddyJson.ParseObject(plain.StorageJson, out var customError);
                if (custom is null)
                    v.Add("storageJson", customError is null ? "Enter the storage JSON object, including its \"module\"." : "The storage JSON " + customError + ".");
                else if (custom["module"] is not JsonValue mv || mv.GetValueKind() != JsonValueKind.String || mv.GetValue<string>().Trim().Length == 0)
                    v.Add("storageJson", "The storage JSON needs a string \"module\" (e.g. \"redis\", \"consul\", \"s3\").");
                else
                {
                    var module = mv.GetValue<string>().Trim();
                    if (!StorageModuleRegex().IsMatch(module))
                        v.Add("storageJson", $"'{module}' is not a storage module name (the part after caddy.storage.).");
                    else if (modules is not null && !modules.Contains("caddy.storage." + module, StringComparer.Ordinal))
                        v.Add("storageBackend", StoragePlugins.TryGetValue(module, out var plugin)
                            ? $"The installed Caddy binary does not include the storage module 'caddy.storage.{module}'. Add the plugin '{plugin}' under Caddy > Plugins and rebuild Caddy first."
                            : $"The installed Caddy binary does not include the storage module 'caddy.storage.{module}'. Add the plugin that provides it under Caddy > Plugins and rebuild Caddy first.");
                }
                break;
        }
        return v.IsValid ? null : v.ToResult();
    }

    /// <summary>The field error when the installed Caddy binary is known and lacks the DNS provider module; null otherwise.</summary>
    internal static string? DnsProviderModuleProblem(string? provider, IReadOnlyCollection<string>? modules)
    {
        var name = DnsProviders.DnsProviderCatalog.Normalize(provider);
        if (name is null || modules is null) return null;
        var module = DnsProviders.DnsProviderCatalog.ModulePrefix + name;
        if (modules.Contains(module, StringComparer.Ordinal)) return null;
        var plugin = DnsProviders.DnsProviderCatalog.Find(name)?.Package;
        return plugin is not null
            ? $"The installed Caddy binary does not include {module}. Add the plugin {plugin} under Caddy > Plugins and rebuild Caddy first (Settings > Caddy offers this), then save again."
            : $"The installed Caddy binary does not include {module}. Add the plugin that provides it under Caddy > Plugins and rebuild Caddy first, then save again.";
    }

    /// <summary>host:port with a valid host (IPv6 in brackets) and port.</summary>
    internal static bool IsHostPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        var colon = t.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(t[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || !NetUtil.IsValidPort(port))
            return false;
        var host = t[..colon];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        else if (host.Contains(':')) return false;
        return NetUtil.IsValidHost(host);
    }
}
