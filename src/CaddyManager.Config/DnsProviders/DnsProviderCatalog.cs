using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaddyManager.Config.DnsProviders;

/// <summary>Field of a caddy-dns provider. "duration" values are entered in seconds and written as integer nanoseconds.</summary>
public sealed record DnsProviderField
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Secret { get; init; }
    public bool Required { get; init; }
    /// <summary>"string" | "number" | "boolean" | "duration"</summary>
    public string Type { get; init; } = "string";
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
}

/// <summary>One entry of GET /api/settings/caddy/dns-providers.</summary>
public sealed record DnsProviderInfo
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Package { get; init; } = "";
    public string Module { get; init; } = "";
    public string DocsUrl { get; init; } = "";
    /// <summary>The provider module is compiled into the installed Caddy binary.</summary>
    public bool Installed { get; init; }
    public string? Notes { get; init; }
    public List<DnsProviderField> Fields { get; init; } = new();
}

/// <summary>
/// The caddy-dns providers the settings UI offers with typed fields (docs/research/round3-dns01.md §2, verified against
/// the provider structs of github.com/caddy-dns/* at the time of Caddy v2.11.4). Always the github.com/caddy-dns
/// packages: third-party packages register duplicate module ids (e.g. caddy-dns/he vs hetzner).
/// Hetzner is deliberately absent: caddyserver.com only builds github.com/caddy-dns/hetzner v1 (libdns/hetzner v1, field
/// auth_api_token, the dns.hetzner.com API that Hetzner shut down in May 2026); the Cloud DNS provider is the separate
/// module path github.com/caddy-dns/hetzner/v2, which caddyserver.com does not offer (checked 2026-09-26). v2 registers
/// the same module id, so add it back only with a version check once it can be built.
/// Other providers can still be used by name (any module dns.providers.&lt;name&gt;) with untyped string options.
/// </summary>
public static partial class DnsProviderCatalog
{
    public const string PackagePrefix = "github.com/caddy-dns/";
    public const string ModulePrefix = "dns.providers.";

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    /// <summary>A provider name Caddy can resolve as module dns.providers.&lt;name&gt;.</summary>
    public static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && NameRegex().IsMatch(name);

    /// <summary>Trimmed, lower-case provider name; null for empty.</summary>
    public static string? Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();

    private static DnsProviderField S(string name, string label, bool required = false, string? placeholder = null, string? help = null) =>
        new() { Name = name, Label = label, Secret = true, Required = required, Placeholder = placeholder, Help = help };

    private static DnsProviderField T(string name, string label, bool required = false, string? placeholder = null, string? help = null, string type = "string") =>
        new() { Name = name, Label = label, Required = required, Placeholder = placeholder, Help = help, Type = type };

    private static DnsProviderInfo P(string name, string label, string? notes, params DnsProviderField[] fields) => new()
    {
        Name = name,
        Label = label,
        Package = PackagePrefix + name,
        Module = ModulePrefix + name,
        DocsUrl = "https://github.com/caddy-dns/" + name,
        Notes = notes,
        Fields = fields.ToList(),
    };

    /// <summary>Ordered by popularity.</summary>
    public static readonly IReadOnlyList<DnsProviderInfo> Providers =
    [
        P("cloudflare", "Cloudflare",
            "Use one API token with the permissions Zone.Zone:Read and Zone.DNS:Edit for the zones concerned. The optional zone token is only needed when zone read access is granted by a separate token.",
            S("api_token", "API token", required: true, help: "Cloudflare API token (not the global API key)."),
            S("zone_token", "Zone token", help: "Optional separate token with Zone.Zone:Read.")),
        P("route53", "Amazon Route 53",
            "Credentials are optional: without them the AWS default credential chain is used (environment, shared profile, instance role).",
            T("access_key_id", "Access key ID"),
            S("secret_access_key", "Secret access key"),
            S("session_token", "Session token"),
            T("region", "Region", placeholder: "us-east-1"),
            T("profile", "Profile", help: "Named profile from the shared AWS configuration."),
            T("hosted_zone_id", "Hosted zone ID", help: "Skips the zone lookup."),
            T("max_retries", "Max retries", type: "number"),
            T("route53_max_wait", "Max wait for Route 53 sync", type: "duration", help: "Seconds."),
            T("wait_for_route53_sync", "Wait for Route 53 sync", type: "boolean"),
            T("skip_route53_sync_on_delete", "Skip sync on delete", type: "boolean"),
            T("debug_logging", "Debug logging", type: "boolean")),
        P("azure", "Azure DNS",
            "Leave tenant ID, client ID and client secret empty to use the managed identity of the server.",
            T("subscription_id", "Subscription ID", required: true),
            T("resource_group_name", "Resource group", required: true),
            T("tenant_id", "Tenant ID"),
            T("client_id", "Client ID"),
            S("client_secret", "Client secret")),
        P("digitalocean", "DigitalOcean", null,
            S("auth_token", "API token", required: true)),
        P("googleclouddns", "Google Cloud DNS",
            "Without a service-account file the Application Default Credentials of the server are used.",
            T("gcp_project", "Project ID", required: true),
            T("gcp_application_default", "Service-account JSON file", help: "Absolute path of the service-account key file on the server.")),
        P("ovh", "OVHcloud", null,
            T("endpoint", "Endpoint", required: true, placeholder: "ovh-eu"),
            T("application_key", "Application key", required: true),
            S("application_secret", "Application secret", required: true),
            S("consumer_key", "Consumer key", required: true)),
        P("godaddy", "GoDaddy", "The token has the form <key>:<secret>.",
            S("api_token", "API token", required: true, placeholder: "key:secret")),
        P("porkbun", "Porkbun", null,
            S("api_key", "API key", required: true),
            S("api_secret_key", "Secret API key", required: true)),
        P("namecheap", "Namecheap", "Namecheap only accepts API calls from allow-listed client IP addresses.",
            S("api_key", "API key", required: true),
            T("user", "User name", required: true),
            T("api_endpoint", "API endpoint"),
            T("client_ip", "Client IP")),
        P("gandi", "Gandi", null,
            S("bearer_token", "Personal access token", required: true)),
        P("duckdns", "Duck DNS", null,
            S("api_token", "Token", required: true),
            T("override_domain", "Override domain"),
            T("resolver", "Resolver", placeholder: "1.1.1.1:53")),
        P("ionos", "IONOS", null,
            S("auth_api_token", "API token", required: true, placeholder: "prefix.secret")),
        P("desec", "deSEC", null,
            S("token", "Token", required: true)),
        P("linode", "Linode (Akamai)", null,
            S("api_token", "API token", required: true),
            T("api_url", "API URL"),
            T("api_version", "API version")),
        P("vultr", "Vultr", null,
            S("api_token", "API token", required: true)),
        P("netlify", "Netlify", null,
            S("personal_access_token", "Personal access token", required: true)),
        P("dnsimple", "DNSimple", "The account ID is required with a user token.",
            S("api_access_token", "API access token", required: true),
            T("account_id", "Account ID"),
            T("api_url", "API URL")),
        P("bunny", "Bunny DNS", null,
            S("access_key", "API access key", required: true)),
        P("namesilo", "NameSilo", null,
            S("api_token", "API key", required: true)),
        P("alidns", "Alibaba Cloud DNS", null,
            T("access_key_id", "Access key ID", required: true),
            S("access_key_secret", "Access key secret", required: true),
            T("region_id", "Region ID"),
            S("security_token", "Security token")),
        P("powerdns", "PowerDNS", null,
            T("server_url", "Server URL", required: true, placeholder: "https://pdns.example.com"),
            S("api_token", "API key", required: true),
            T("server_id", "Server ID", placeholder: "localhost"),
            T("debug", "Debug")),
        P("acmedns", "ACME-DNS", "Point a CNAME _acme-challenge.<domain> at the full domain of the acme-dns registration.",
            T("username", "User name", required: true),
            S("password", "Password", required: true),
            T("subdomain", "Subdomain", required: true),
            T("server_url", "Server URL", required: true, placeholder: "https://auth.acme-dns.io")),
        P("rfc2136", "RFC 2136 (BIND, Knot, PowerDNS, ...)",
            "Dynamic DNS updates signed with a TSIG key. The server must allow updates for the zone with this key. " +
            "Windows DNS (Active Directory) accepts only Kerberos-signed (GSS-TSIG) secure updates, which this provider cannot send: " +
            "for a zone on Windows DNS, delegate _acme-challenge with a CNAME to a zone on a TSIG-capable server or a supported DNS provider (see Challenge delegation).",
            T("server", "Server", required: true, placeholder: "10.0.0.53:53"),
            T("key_name", "TSIG key name", required: true),
            T("key_alg", "TSIG algorithm", required: true, placeholder: "hmac-sha256"),
            S("key", "TSIG secret (base64)", required: true)),
    ];

    private static readonly Dictionary<string, DnsProviderInfo> ByName = Providers.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static DnsProviderInfo? Find(string? name) =>
        Normalize(name) is { } n && ByName.TryGetValue(n, out var p) ? p : null;

    /// <summary>The catalog with Installed set from the installed binary's modules (all false when they are unknown).</summary>
    public static List<DnsProviderInfo> WithInstalled(IReadOnlyCollection<string>? modules) =>
        Providers.Select(p => p with { Installed = modules?.Contains(p.Module, StringComparer.Ordinal) ?? false }).ToList();

    /// <summary>
    /// The provider object for challenges.dns.provider: name, typed options and secrets. Unknown providers pass options
    /// as strings. Values that do not parse for their field type are reported and skipped.
    /// </summary>
    public static JsonObject BuildProvider(string name, IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<string, string> secrets, List<string> problems)
    {
        var o = new JsonObject { ["name"] = name };
        var info = Find(name);
        foreach (var (key, raw) in options.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(key) || key == "name" || raw is null) continue;
            var value = raw.Trim();
            if (value.Length == 0) continue;
            var field = info?.Fields.FirstOrDefault(f => f.Name == key);
            if (field is { Secret: true }) continue; // secrets only come from the protected store
            var node = Convert(field?.Type ?? "string", value, out var error);
            if (error is not null) { problems.Add($"DNS provider option '{key}' {error}; it was ignored."); continue; }
            o[key] = node;
        }
        foreach (var (key, value) in secrets.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(key) || key == "name" || string.IsNullOrEmpty(value)) continue;
            o[key] = value;
        }
        return o;
    }

    /// <summary>Converts a UI value to the JSON the provider struct expects.</summary>
    internal static JsonNode? Convert(string type, string value, out string? error)
    {
        error = null;
        switch (type)
        {
            case "number":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return JsonValue.Create(n);
                error = "must be a whole number";
                return null;
            case "boolean":
                if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
                error = "must be true or false";
                return null;
            case "duration":
                // A provider's own durations are plain Go time.Duration fields: JSON integer nanoseconds
                // (e.g. route53 route53_max_wait), not caddy.Duration strings.
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0 && secs <= 365 * 86400)
                    return JsonValue.Create((long)Math.Round(secs * 1_000_000_000d));
                error = "must be a number of seconds";
                return null;
            default:
                return JsonValue.Create(value);
        }
    }
}
