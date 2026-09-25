using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Generation;

/// <summary>
/// Pure translation of the manager's model (hosts, streams, access lists, certificates, settings) into a
/// Caddy JSON document. Output is deterministic (stable ordering) so identical input hashes identically.
/// </summary>
public static partial class CaddyConfigGenerator
{
    public const string HttpsServerName = "srv0";
    public const string HttpServerName = "srv1";
    public const string Layer4Module = "layer4";
    public const string Layer4Plugin = "github.com/mholt/caddy-l4";
    public const string CertificateTagPrefix = "cpm-";
    /// <summary>Module ID of the NTLM-aware reverse proxy transport.</summary>
    public const string NtlmModule = "http.reverse_proxy.transport.http_ntlm";
    public const string NtlmPlugin = "github.com/caddyserver/ntlm-transport";
    /// <summary>Apps the manager generates; CaddySettings.ExtraAppsJson may not override them.</summary>
    public static readonly string[] ReservedApps = ["http", "tls", "pki", "layer4"];
    /// <summary>Connection policy keys the manager controls; CaddySettings.TlsConnectionPolicyJson may not set them.</summary>
    public static readonly string[] ReservedConnectionPolicyKeys = ["match", "certificate_selection"];
    public const string AccessLoggerPrefix = "cpm_access_";
    private const string StreamsSkippedWarningMarker = "were not applied";

    /// <summary>True for the warning about streams skipped because the layer4 module is missing.</summary>
    public static bool IsStreamsSkippedWarning(string warning) =>
        warning.Contains(StreamsSkippedWarningMarker, StringComparison.Ordinal) && warning.Contains(Layer4Module, StringComparison.Ordinal);
    private const string IpVar = "cpm_ip";

    public const string LetsEncryptDirectory = "https://acme-v02.api.letsencrypt.org/directory";
    public const string LetsEncryptStagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";
    public const string ZeroSslDirectory = "https://acme.zerossl.com/v2/DV90";

    // Common exploit-probe patterns (SQL injection, path traversal, script injection), minus rules that
    // break ordinary applications (such as "?next=/path"). path_regexp runs on the unescaped, cleaned path, so a ".."
    // SEGMENT only survives there next to a backslash or as a double-encoded "%2e%2e"; names that merely contain dots
    // ("v1.../x", "file..txt") must not match.
    internal const string ExploitPathPattern =
        @"(?i)((^|[/\\])(\.\.|%2e%2e)([/\\]|%2f|%5c|$)|/\.(git|svn|hg|env)(/|$)|/etc/passwd|/proc/self/environ|/wp-config\.php)";
    internal const string ExploitQueryPattern =
        @"(?i)(union.*select.*\(|union.*all.*select|concat.*\(|[a-z0-9_]=http://|[a-z0-9_]=(\.\.//?)+|(<|%3c).*script.*(>|%3e)|globals(=|\[|%[0-9a-z]{0,2})|_request(=|\[|%[0-9a-z]{0,2})|proc/self/environ|mosconfig_[a-z_]{1,21}(=|%3d)|base64_(en|de)code\(.*\))";
    /// <summary>
    /// A ".." path SEGMENT in the RAW request target (before the query), with dots and separators plain, percent-encoded
    /// or double-encoded, and ";" (Tomcat path parameters, "/..;/"). path_regexp only sees the unescaped, cleaned path
    /// (path.Clean removes "../"), so "..%2f" and "%2e%2e/" reached upstreams that decode them later. The segment must
    /// start after a separator, so dotted names such as "/v1.../x" stay allowed.
    /// https://caddyserver.com/docs/caddyfile/matchers#path-regexp
    /// </summary>
    internal const string ExploitRawTraversalPattern =
        @"(?i)^[^?]*(/|\\|%2f|%5c|%252f|%255c)(\.|%2e|%252e){2}(/|\\|%2f|%5c|%252f|%255c|;|\?|$)";
    internal const string ExploitUserAgentPattern =
        @"(?i)(sqlmap|nikto|masscan|wpscan|acunetix|netsparker|zgrab|dirbuster|havij|nessus|openvas)";

    // ------------------------------------------------------------------ boot config

    /// <summary>Minimal bootable config: admin endpoint, storage and process log only.</summary>
    public static JsonObject BuildBootConfig(CaddySettings settings, AppPaths paths) => new()
    {
        ["admin"] = Admin(settings),
        ["logging"] = new JsonObject { ["logs"] = ProcessLoggers(settings, paths, []) },
        ["storage"] = Storage(paths),
    };

    // ------------------------------------------------------------------ Caddyfile mode

    /// <summary>
    /// Completes a config adapted from a user Caddyfile with the parts the manager depends on, when the Caddyfile
    /// does not set them itself: the admin endpoint (otherwise Caddy falls back to localhost:2019 and the manager
    /// loses contact when AdminListen differs), storage (ACME certificates / internal CA) and the process log.
    /// </summary>
    public static string CompleteAdaptedConfig(string adaptedJson, CaddySettings settings, AppPaths paths, List<string> warnings)
    {
        if (JsonNode.Parse(adaptedJson) is not JsonObject root) return adaptedJson;
        var expected = Admin(settings)["listen"]!.GetValue<string>();
        if (root["admin"] is JsonObject admin)
        {
            var listen = admin["listen"]?.GetValue<string>();
            if (listen is null) admin["listen"] = expected;
            else if (!string.Equals(listen, expected, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"The Caddyfile sets the admin endpoint to '{listen}', but the manager uses '{expected}' (Settings > Caddy). The manager cannot control Caddy until both match.");
        }
        else
        {
            root["admin"] = Admin(settings);
        }
        root["storage"] ??= Storage(paths);
        if (root["logging"] is null) root["logging"] = new JsonObject { ["logs"] = ProcessLoggers(settings, paths, []) };
        DisableTrustInstall(root);
        return root.ToJsonString();
    }

    /// <summary>
    /// Sets install_trust=false on EVERY certificate authority of the pki app (the implicit "local" CA included) unless
    /// the config sets it. Caddy's PKI app installs each configured CA's root into the OS trust stores at start unless
    /// install_trust is explicitly false (nil means install). A Caddyfile's `pki { ca corp {...} }` block defines
    /// further CAs, and `tls internal` - or any site name that does not qualify for a public certificate - provisions
    /// "local" implicitly (PKI.GetCA). The service runs headless as LocalSystem, so it must never modify trust stores.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddypki/pki.go (Start, GetCA)
    /// </summary>
    private static void DisableTrustInstall(JsonObject root)
    {
        if (root["apps"] is not JsonObject apps || (apps["http"] is null && apps["tls"] is null && apps["pki"] is null)) return;
        if (apps["pki"] is not JsonObject pki) apps["pki"] = pki = new JsonObject();
        if (pki["certificate_authorities"] is not JsonObject cas) pki["certificate_authorities"] = cas = new JsonObject();
        if (cas["local"] is not JsonObject) cas["local"] = new JsonObject();
        foreach (var (_, ca) in cas)
            if (ca is JsonObject o && o["install_trust"] is null) o["install_trust"] = false;
    }

    // ------------------------------------------------------------------ full config

    public static ConfigGeneratorResult Generate(ConfigGeneratorInput input)
    {
        var ctx = new Ctx(input);
        var s = input.Settings;

        var sites = SelectSites(ctx);

        // ---- servers
        var httpsSites = new List<(Site Site, JsonObject Handler)>();
        var httpSites = new List<(Site Site, JsonObject Handler)>();
        var accessLoggers = new SortedDictionary<string, (string Domain, SiteHost Host)>(StringComparer.Ordinal);
        var loggerNames = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var site in sites)
        {
            if (site.Host.AccessLog)
            {
                var logger = AccessLoggerPrefix + site.Host.Id;
                accessLoggers[logger] = (site.Domains[0], site.Host);
                foreach (var d in site.Domains) loggerNames[d] = logger;
            }

            var handler = HostHandler(site, ctx);
            if (site.Host.Tls == TlsMode.None)
            {
                httpSites.Add((site, handler));
            }
            else
            {
                httpsSites.Add((site, handler));
                // ForceHttps: our own redirect route on the HTTP server. Caddy's automatic redirects are not relied on:
                // they are only inserted before our catch-all when at least one name gets a MANAGED certificate
                // (autohttps.go: `if len(uniqueDomainsForCerts) != 0`), so hosts with only custom certificates got
                // the default site on http://, and their Location never carries a non-443 https_port.
                // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/autohttps.go
                // The plain-HTTP copy omits HSTS (RFC 6797 §8.1: ignored over insecure transport).
                httpSites.Add((site, site.Host.ForceHttps ? HttpsRedirectHandler(s) : HostHandler(site, ctx, plainHttp: true)));
            }
        }
        var httpsRoutes = HostRoutes(httpsSites);
        var httpRoutes = HostRoutes(httpSites);

        var servers = new JsonObject();
        var httpsHosts = sites.Where(x => x.Host.Tls != TlsMode.None).ToList();
        if (httpsHosts.Count > 0)
        {
            httpsRoutes.Add(DefaultSiteRoute(s, ctx));
            var srv0 = new JsonObject
            {
                ["listen"] = Listen(s, s.HttpsPort),
                ["routes"] = httpsRoutes,
            };
            var connPolicies = ConnectionPolicies(httpsHosts, ctx);
            if (connPolicies is not null) srv0["tls_connection_policies"] = connPolicies;
            srv0["automatic_https"] = AutomaticHttps(sites);
            ApplyCommonServerOptions(srv0, s, loggerNames, ctx);
            srv0["protocols"] = s.EnableHttp3 ? new JsonArray("h1", "h2", "h3") : new JsonArray("h1", "h2");
            // QUIC 0-RTT data arrives before the handshake proves the client address, so remote_ip/client_ip matchers
            // answer 425 Too Early (some clients never retry), and early data can be replayed.
            // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/server.go (allow_0rtt)
            if (s.EnableHttp3) srv0["allow_0rtt"] = false;
            var errorRoutes = HostRoutes(httpsSites.Select(x => (x.Site, ErrorHeadersHandler(x.Site.Host, plainHttp: false))).Where(x => x.Item2 is not null)
                .Select(x => (x.Site, x.Item2!)).ToList());
            if (errorRoutes.Count > 0) srv0["errors"] = new JsonObject { ["routes"] = errorRoutes };
            MergeServerOptions(srv0, s, ctx);
            servers[HttpsServerName] = srv0;
        }

        httpRoutes.Add(DefaultSiteRoute(s, ctx));
        var srv1 = new JsonObject
        {
            ["listen"] = Listen(s, s.HttpPort),
            ["routes"] = httpRoutes,
        };
        ApplyCommonServerOptions(srv1, s, loggerNames, ctx);
        // HTTP/2 needs TLS; on a plain-HTTP listener Caddy skips "h2" and logs a warning on every load.
        // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/server.go (protocols)
        srv1["protocols"] = new JsonArray("h1");
        var httpErrorRoutes = HostRoutes(httpSites.Where(x => x.Site.Host.Tls == TlsMode.None || !x.Site.Host.ForceHttps)
            .Select(x => (x.Site, ErrorHeadersHandler(x.Site.Host, plainHttp: true))).Where(x => x.Item2 is not null).Select(x => (x.Site, x.Item2!)).ToList());
        if (httpErrorRoutes.Count > 0) srv1["errors"] = new JsonObject { ["routes"] = httpErrorRoutes };
        MergeServerOptions(srv1, s, ctx);
        servers[HttpServerName] = srv1;

        var httpApp = new JsonObject
        {
            ["http_port"] = s.HttpPort,
            ["https_port"] = s.HttpsPort,
            ["servers"] = servers,
        };

        var apps = new JsonObject { ["http"] = httpApp };
        var tls = TlsApp(sites, ctx);
        if (tls is not null) apps["tls"] = tls;
        if (sites.Any(x => x.Host.Tls == TlsMode.Internal))
        {
            // A headless service must never try to modify OS trust stores; distribute the root via GPO instead.
            apps["pki"] = new JsonObject
            {
                ["certificate_authorities"] = new JsonObject
                {
                    ["local"] = new JsonObject { ["install_trust"] = false },
                },
            };
        }
        var l4 = Layer4App(ctx);
        if (l4 is not null) apps["layer4"] = l4;
        MergeExtraApps(apps, s, ctx);

        // ---- logging
        // Access log lines go to the per-host files only; the process log excludes the whole access namespace so that
        // requests routed to a host whose Host header does not match a logger_names key exactly (e.g. upper case) do
        // not end up in caddy.log.
        var logs = ProcessLoggers(s, input.Paths, accessLoggers.Count > 0 ? ["http.log.access"] : []);
        foreach (var (name, (domain, _)) in accessLoggers)
        {
            logs[name] = new JsonObject
            {
                ["writer"] = new JsonObject
                {
                    ["output"] = "file",
                    ["filename"] = Path.Combine(input.Paths.AccessLogDir, NetUtil.SafeFileName(domain) + ".log"),
                    ["roll_size_mb"] = 20,
                    ["roll_keep"] = 10,
                },
                ["encoder"] = new JsonObject { ["format"] = "json" },
                ["level"] = "INFO",
                ["include"] = new JsonArray("http.log.access." + name),
            };
        }

        var root = new JsonObject
        {
            ["admin"] = Admin(s),
            ["logging"] = new JsonObject { ["logs"] = logs },
            ["storage"] = Storage(input.Paths),
            ["apps"] = apps,
        };
        return new ConfigGeneratorResult(root, ctx.Warnings);
    }

    // ------------------------------------------------------------------ site selection

    internal sealed record Site(SiteHost Host, List<string> Domains, Certificate? Certificate);

    private sealed class Ctx(ConfigGeneratorInput input)
    {
        public ConfigGeneratorInput Input { get; } = input;
        public List<string> Warnings { get; } = new();
        public Dictionary<string, AccessList> AccessLists { get; } =
            input.AccessLists.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        public Dictionary<string, Certificate> Certificates { get; } =
            input.Certificates.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        /// <summary>With trusted proxies configured, client_ip honours X-Forwarded-For from those proxies.</summary>
        public string IpMatcher => Input.Settings.TrustedProxies.Any(p => !string.IsNullOrWhiteSpace(p)) ? "client_ip" : "remote_ip";
        public void Warn(string message) { if (!Warnings.Contains(message)) Warnings.Add(message); }
    }

    private static string Label(SiteHost h) => h.Domains.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))?.Trim() ?? h.Id;

    private static List<Site> SelectSites(Ctx ctx)
    {
        var candidates = new List<Site>();
        foreach (var h in ctx.Input.Hosts.Where(h => h.Enabled))
        {
            var domains = new List<string>();
            foreach (var raw in h.Domains)
            {
                var d = NetUtil.NormalizeDomain(raw);
                if (d is null) continue;
                if (!NetUtil.IsValidDomain(d))
                {
                    ctx.Warn($"Host '{Label(h)}': invalid domain '{raw}' was ignored.");
                    continue;
                }
                if (!domains.Contains(d)) domains.Add(d);
            }
            if (domains.Count == 0)
            {
                ctx.Warn($"Host '{h.Id}' has no valid domains and was skipped.");
                continue;
            }

            Certificate? cert = null;
            if (h.Tls == TlsMode.Custom)
            {
                if (string.IsNullOrEmpty(h.CertificateId) || !ctx.Certificates.TryGetValue(h.CertificateId, out cert))
                {
                    ctx.Warn($"Host '{domains[0]}' uses a custom certificate that no longer exists; the host was skipped.");
                    continue;
                }
                if (ctx.Input.UnavailableCertificateIds.Contains(cert.Id))
                {
                    ctx.Warn($"Host '{domains[0]}' was skipped: the files of certificate '{cert.Name}' are missing or unreadable ({cert.CertPath}).");
                    continue;
                }
            }
            candidates.Add(new Site(h, domains, cert));
        }

        // Most specific first: hosts with only exact names, then those with wildcards; then by first domain.
        candidates = candidates
            .OrderBy(x => x.Domains.All(d => d.StartsWith("*.", StringComparison.Ordinal)) ? 1 : 0)
            .ThenBy(x => x.Domains[0], StringComparer.Ordinal)
            .ThenBy(x => x.Host.Id, StringComparer.Ordinal)
            .ToList();

        // A domain may only be served by one host (duplicate TLS automation subjects are rejected by Caddy).
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<Site>();
        foreach (var c in candidates)
        {
            var own = new List<string>();
            foreach (var d in c.Domains)
            {
                if (claimed.TryGetValue(d, out var other))
                    ctx.Warn($"Domain '{d}' is configured on more than one enabled host; it is served by '{other}' only.");
                else
                {
                    claimed[d] = c.Domains[0];
                    own.Add(d);
                }
            }
            if (own.Count == 0) continue;
            result.Add(c with { Domains = own });
        }
        return result;
    }

    // ------------------------------------------------------------------ per-host route

    /// <summary>
    /// Host routes in matching order. Caddy evaluates routes top to bottom and a host matcher with a wildcard also
    /// matches names another host serves exactly, so every host's exact names are emitted first (one route per host),
    /// then its wildcard names (one route per host and wildcard depth, more specific wildcards first). A host with
    /// both kinds shares one built handler between its routes.
    /// </summary>
    private static JsonArray HostRoutes(List<(Site Site, JsonObject Handler)> items)
    {
        var routes = new JsonArray();
        foreach (var (site, handler) in items)
        {
            var exact = site.Domains.Where(d => !IsWildcard(d)).ToList();
            if (exact.Count > 0) routes.Add(RouteFor(exact, handler));
        }
        var wildcardGroups = items
            .SelectMany(x => x.Site.Domains.Where(IsWildcard)
                .GroupBy(LabelCount)
                .Select(g => (Depth: g.Key, Domains: g.OrderBy(d => d, StringComparer.Ordinal).ToList(), x.Site, x.Handler)))
            .OrderByDescending(g => g.Depth)
            .ThenBy(g => g.Domains[0], StringComparer.Ordinal)
            .ThenBy(g => g.Site.Host.Id, StringComparer.Ordinal);
        foreach (var g in wildcardGroups) routes.Add(RouteFor(g.Domains, g.Handler));
        return routes;

        static JsonObject RouteFor(List<string> domains, JsonObject handler) => new()
        {
            ["match"] = new JsonArray(new JsonObject { ["host"] = StringArray(domains) }),
            ["handle"] = new JsonArray(handler.DeepClone()),
            ["terminal"] = true,
        };
    }

    internal static bool IsWildcard(string domain) => domain.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>True when a wildcard such as *.example.com covers the name (exactly one extra label, never the apex).</summary>
    internal static bool CoveredByWildcard(string name, string wildcard)
    {
        if (!IsWildcard(wildcard) || name == wildcard) return false;
        var dot = name.IndexOf('.');
        return dot > 0 && !name.StartsWith("*.", StringComparison.Ordinal)
            && string.Equals(name[(dot + 1)..], wildcard[2..], StringComparison.OrdinalIgnoreCase);
    }

    private static int LabelCount(string domain) => domain.Count(c => c == '.') + 1;

    /// <summary>The host's subroute handler (access list, headers, compression, advanced routes, kind handler).</summary>
    private static JsonObject HostHandler(Site site, Ctx ctx, bool plainHttp = false)
    {
        var h = site.Host;
        var routes = new JsonArray();
        var stripAuthorization = false;

        // 0. access log: pick the host's logger explicitly. The server's logger_names lookup is an exact,
        // case-sensitive map lookup on the Host header, so "ECHO.TEST" (routed here case-insensitively) was logged to
        // the default log instead of this host's file. https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/logging.go
        if (h.AccessLog)
            routes.Add(new JsonObject
            {
                ["handle"] = new JsonArray(new JsonObject { ["handler"] = "vars", ["access_logger_names"] = new JsonArray(AccessLoggerPrefix + h.Id) }),
            });

        // 1. access list
        if (!string.IsNullOrEmpty(h.AccessListId))
        {
            if (ctx.AccessLists.TryGetValue(h.AccessListId, out var al))
            {
                if (h.Kind == HostKind.Proxy && h.UpstreamNtlm && al.Users.Any(u => !string.IsNullOrWhiteSpace(u.Username)))
                    ctx.Warn($"Host '{site.Domains[0]}': the access list '{al.Name}' asks for a user name and password (basic auth), but NTLM/Negotiate logins also use the Authorization header, so Windows authentication cannot work on this host. Use an access list with IP rules only.");
                stripAuthorization = AddAccessListRoutes(routes, al, ctx);
            }
            else
            {
                // Fail closed: a host that should be protected must never be served openly.
                ctx.Warn($"Host '{site.Domains[0]}' references an access list that no longer exists; all requests are denied.");
                routes.Add(new JsonObject { ["handle"] = new JsonArray(Forbidden()) });
            }
        }

        // 2. block exploits
        if (h.BlockExploits) routes.Add(BlockExploitsRoute());

        // 3. HSTS + response headers
        var headers = ResponseHeadersHandler(h, plainHttp);
        if (headers is not null) routes.Add(new JsonObject { ["handle"] = new JsonArray(headers) });

        // 4. compression
        if (h.Compression)
        {
            routes.Add(new JsonObject
            {
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "encode",
                    ["encodings"] = new JsonObject { ["gzip"] = new JsonObject(), ["zstd"] = new JsonObject() },
                    ["prefer"] = new JsonArray("zstd", "gzip"),
                }),
            });
        }

        // 5. advanced routes
        foreach (var r in AdvancedRoutes(h, site.Domains[0], ctx)) routes.Add(r);

        // 6. kind handler
        switch (h.Kind)
        {
            case HostKind.Proxy:
                AddProxyRoutes(routes, site, stripAuthorization, ctx);
                break;
            case HostKind.Redirect:
                routes.Add(new JsonObject { ["handle"] = new JsonArray(RedirectHandler(h, site.Domains[0], ctx)) });
                break;
            case HostKind.Static:
                AddStaticRoutes(routes, h, site.Domains[0], ctx);
                break;
            case HostKind.Response:
                routes.Add(new JsonObject { ["handle"] = new JsonArray(ResponseHandler(h)) });
                break;
        }

        return new JsonObject { ["handler"] = "subroute", ["routes"] = routes };
    }

    private static JsonObject Forbidden() => new()
    {
        ["handler"] = "static_response",
        ["status_code"] = 403,
        ["body"] = "403 Forbidden",
        ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/plain; charset=utf-8") },
    };

    /// <summary>Adds IP / basic-auth routes. Returns true when the Authorization header must be stripped before proxying.</summary>
    private static bool AddAccessListRoutes(JsonArray routes, AccessList al, Ctx ctx)
    {
        var rules = al.Rules.Where(r => NetUtil.IsValidCidr(r.Cidr)).ToList();
        foreach (var bad in al.Rules.Where(r => !NetUtil.IsValidCidr(r.Cidr)))
            ctx.Warn($"Access list '{al.Name}': invalid address '{bad.Cidr}' was ignored.");
        var users = al.Users
            .Where(u => !string.IsNullOrWhiteSpace(u.Username) && !string.IsNullOrWhiteSpace(u.PasswordHash))
            .GroupBy(u => u.Username, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(u => u.Username, StringComparer.Ordinal)
            .ToList();
        var hasIp = rules.Count > 0;
        var hasAuth = users.Count > 0;

        if (hasIp)
        {
            // First-match-wins evaluation that records the decision in a variable. Caddy's "terminal" would end the
            // whole handler chain, so instead the implicit default is set first and the rules follow in REVERSE order:
            // the last assignment that runs belongs to the first matching rule.
            var implicitDecision = rules.Any(r => r.Action == IpRuleAction.Allow) ? "deny" : "allow";
            var decision = new JsonArray { new JsonObject { ["handle"] = new JsonArray(SetIpVar(implicitDecision)) } };
            for (var i = rules.Count - 1; i >= 0; i--)
            {
                var rule = rules[i];
                decision.Add(new JsonObject
                {
                    ["match"] = new JsonArray(new JsonObject
                    {
                        [ctx.IpMatcher] = new JsonObject { ["ranges"] = StringArray(NetUtil.ExpandCidr(rule.Cidr)) },
                    }),
                    ["handle"] = new JsonArray(SetIpVar(rule.Action == IpRuleAction.Allow ? "allow" : "deny")),
                });
            }
            routes.Add(new JsonObject
            {
                ["handle"] = new JsonArray(new JsonObject { ["handler"] = "subroute", ["routes"] = decision }),
            });
        }

        var auth = hasAuth ? AuthenticationHandler(al, users) : null;
        if (hasIp && hasAuth && al.SatisfyAny)
        {
            // Allowed IPs pass without credentials; everyone else must authenticate.
            routes.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["not"] = new JsonArray(new JsonObject { ["vars"] = new JsonObject { [IpVar] = new JsonArray("allow") } }),
                }),
                ["handle"] = new JsonArray(auth),
            });
        }
        else
        {
            if (hasIp)
            {
                routes.Add(new JsonObject
                {
                    ["match"] = new JsonArray(new JsonObject { ["vars"] = new JsonObject { [IpVar] = new JsonArray("deny") } }),
                    ["handle"] = new JsonArray(Forbidden()),
                });
            }
            if (auth is not null) routes.Add(new JsonObject { ["handle"] = new JsonArray(auth) });
        }
        return hasAuth && !al.PassAuthToUpstream;
    }

    private static JsonObject SetIpVar(string value) => new() { ["handler"] = "vars", [IpVar] = value };

    private static JsonObject AuthenticationHandler(AccessList al, List<AccessUser> users)
    {
        var accounts = new JsonArray();
        foreach (var u in users)
            accounts.Add(new JsonObject { ["username"] = u.Username, ["password"] = u.PasswordHash });
        return new JsonObject
        {
            ["handler"] = "authentication",
            ["providers"] = new JsonObject
            {
                ["http_basic"] = new JsonObject
                {
                    ["accounts"] = accounts,
                    ["hash"] = new JsonObject { ["algorithm"] = "bcrypt" },
                    // bcrypt is deliberately slow; cache successful comparisons.
                    ["hash_cache"] = new JsonObject(),
                    ["realm"] = string.IsNullOrWhiteSpace(al.Name) ? "restricted" : al.Name.Replace("\"", "'"),
                },
            },
        };
    }

    private static JsonObject BlockExploitsRoute() => new()
    {
        ["match"] = new JsonArray(
            new JsonObject { ["path_regexp"] = new JsonObject { ["name"] = "cpm_exploit_path", ["pattern"] = ExploitPathPattern } },
            new JsonObject
            {
                ["vars_regexp"] = new JsonObject
                {
                    ["{http.request.uri.query}"] = new JsonObject { ["name"] = "cpm_exploit_query", ["pattern"] = ExploitQueryPattern },
                },
            },
            new JsonObject
            {
                ["vars_regexp"] = new JsonObject
                {
                    ["{http.request.uri}"] = new JsonObject { ["name"] = "cpm_exploit_raw", ["pattern"] = ExploitRawTraversalPattern },
                },
            },
            new JsonObject
            {
                ["header_regexp"] = new JsonObject
                {
                    ["User-Agent"] = new JsonObject { ["name"] = "cpm_exploit_ua", ["pattern"] = ExploitUserAgentPattern },
                },
            }),
        ["handle"] = new JsonArray(Forbidden()),
    };

    private static HeaderOpSet ResponseHeaderOps(SiteHost h, bool plainHttp)
    {
        var ops = HeaderOps(h.ResponseHeaders);
        if (h.Hsts && h.Tls != TlsMode.None && !plainHttp)
        {
            var value = $"max-age={Math.Max(0, h.HstsMaxAgeSeconds)}" + (h.HstsSubdomains ? "; includeSubDomains" : "");
            ops.Delete.Remove("Strict-Transport-Security");
            ops.Add.Remove("Strict-Transport-Security");
            ops.Set["Strict-Transport-Security"] = [value];
        }
        return ops;
    }

    private static JsonObject? ResponseHeadersHandler(SiteHost h, bool plainHttp)
    {
        var obj = ResponseHeaderOps(h, plainHttp).ToJson();
        if (obj is null) return null;
        // Deferred so the operations also apply to (and override) headers written by upstreams.
        obj["deferred"] = true;
        return new JsonObject { ["handler"] = "headers", ["response"] = obj };
    }

    /// <summary>
    /// The host's response headers for error responses (401 from basic auth, 502/503 from the proxy, 404 from the file
    /// server). Deferred header changes "do not take effect if an error occurs later in the middleware chain", so the
    /// server's error routes apply them again (not deferred); Caddy then writes the error status itself.
    /// https://caddyserver.com/docs/json/apps/http/servers/routes/handle/headers/response/deferred/
    /// </summary>
    private static JsonObject? ErrorHeadersHandler(SiteHost h, bool plainHttp)
    {
        var obj = ResponseHeaderOps(h, plainHttp).ToJson();
        return obj is null ? null : new JsonObject { ["handler"] = "headers", ["response"] = obj };
    }

    /// <summary>
    /// Header operations. Caddy applies a handler's operations in a FIXED order (add, then set, then delete), not in
    /// the order the user listed them, so the list is folded here into the equivalent final state per header:
    /// "delete X, add X: v" becomes "set X: v" and "set X: a, add X: b" becomes "set X: [a, b]", which Caddy sends
    /// as ONE comma-joined field line "X: a,b" (equivalent for list-valued fields, RFC 9110 §5.3; not for Set-Cookie,
    /// which cannot be expressed as set-then-add in one handler).
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/headers/headers.go (ApplyTo)
    /// </summary>
    private sealed class HeaderOpSet
    {
        public SortedDictionary<string, List<string>> Set { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, List<string>> Add { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Delete { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JsonObject? ToJson()
        {
            if (Set.Count == 0 && Add.Count == 0 && Delete.Count == 0) return null;
            var o = new JsonObject();
            if (Set.Count > 0) o["set"] = Map(Set);
            if (Add.Count > 0) o["add"] = Map(Add);
            if (Delete.Count > 0) o["delete"] = StringArray(Delete);
            return o;
        }

        private static JsonObject Map(SortedDictionary<string, List<string>> d)
        {
            var o = new JsonObject();
            foreach (var (k, v) in d) o[k] = StringArray(v);
            return o;
        }
    }

    private static HeaderOpSet HeaderOps(IEnumerable<HeaderOp> ops)
    {
        var set = new HeaderOpSet();
        foreach (var op in ops)
        {
            if (!NetUtil.IsValidHeaderName(op.Name)) continue;
            var name = op.Name.Trim();
            var value = op.Value ?? "";
            switch (op.Action)
            {
                case HeaderAction.Set:
                    set.Add.Remove(name);
                    set.Delete.Remove(name);
                    set.Set[name] = [value];
                    break;
                case HeaderAction.Add:
                    if (set.Set.TryGetValue(name, out var setValues)) setValues.Add(value);
                    else if (set.Delete.Remove(name)) set.Set[name] = [value];
                    else
                    {
                        if (!set.Add.TryGetValue(name, out var list)) set.Add[name] = list = new();
                        list.Add(value);
                    }
                    break;
                case HeaderAction.Delete:
                    set.Set.Remove(name);
                    set.Add.Remove(name);
                    set.Delete.Add(name);
                    break;
            }
        }
        return set;
    }

    private static IEnumerable<JsonNode> AdvancedRoutes(SiteHost h, string label, Ctx ctx)
    {
        if (string.IsNullOrWhiteSpace(h.AdvancedRoutesJson)) return [];
        try
        {
            if (JsonNode.Parse(h.AdvancedRoutesJson) is not JsonArray arr)
            {
                ctx.Warn($"Host '{label}': advanced routes must be a JSON array; they were ignored.");
                return [];
            }
            if (ctx.Input.EndpointGuard is { } guard)
            {
                var problems = new List<string>();
                guard.CheckNode(arr, problems);
                if (problems.Count > 0)
                {
                    ctx.Warn($"Host '{label}': the advanced routes were ignored because a reverse_proxy in them targets a protected endpoint: {problems[0]}");
                    return [];
                }
            }
            var list = new List<JsonNode>();
            foreach (var item in arr)
            {
                if (item is JsonObject o) list.Add(o.DeepClone());
                else ctx.Warn($"Host '{label}': an advanced route entry is not a JSON object and was ignored.");
            }
            WarnUnderscoreHeaderMatchers(arr, label, ctx);
            return list;
        }
        catch (JsonException ex)
        {
            ctx.Warn($"Host '{label}': advanced routes are not valid JSON ({ex.Message}); they were ignored.");
            return [];
        }
    }

    /// <summary>
    /// Caddy v2.11.4 drops every client request header whose name contains "_" before any handler runs
    /// (GHSA-f59h-q822-g45g), so header / header_regexp matchers on such names never match. Headers the host SETS for
    /// the upstream are applied later and still go out.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/server.go ; https://github.com/caddyserver/caddy/releases/tag/v2.11.4
    /// </summary>
    private static void WarnUnderscoreHeaderMatchers(JsonNode node, string label, Ctx ctx)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key is "header" or "header_regexp" && value is JsonObject headers)
                        foreach (var (name, _) in headers)
                            if (name.Contains('_'))
                                ctx.Warn($"Host '{label}': an advanced route matches the request header '{name}', but Caddy drops client request headers whose names contain an underscore, so it never matches. Use the hyphenated name if the client can send it.");
                    if (value is not null) WarnUnderscoreHeaderMatchers(value, label, ctx);
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item is not null) WarnUnderscoreHeaderMatchers(item, label, ctx);
                break;
        }
    }

    // ------------------------------------------------------------------ kind handlers

    private static void AddProxyRoutes(JsonArray routes, Site site, bool stripAuthorization, Ctx ctx)
    {
        var h = site.Host;
        var label = site.Domains[0];
        var locations = h.Locations
            .Select(l => (Location: l, Path: NormalizeLocationPath(l.Path)))
            .OrderByDescending(x => x.Path.Length)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToList();
        foreach (var (loc, path) in locations)
        {
            var ups = ValidUpstreams(loc.Upstreams, $"Host '{label}', location '{path}'", ctx);
            if (ups.Count == 0)
            {
                ctx.Warn($"Host '{label}': location '{path}' has no valid upstream and was skipped.");
                continue;
            }
            var handles = new JsonArray();
            if (loc.StripPrefix && path != "/")
                handles.Add(new JsonObject { ["handler"] = "rewrite", ["strip_path_prefix"] = path });
            handles.Add(ReverseProxy(ups, loc.UpstreamTlsInsecure, h, stripAuthorization, includeHealthCheck: false, site, ctx));
            var match = path == "/" ? StringArray(["/*"]) : StringArray([path, path + "/*"]);
            routes.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject { ["path"] = match }),
                ["handle"] = handles,
            });
        }

        var upstreams = ValidUpstreams(h.Upstreams, $"Host '{label}'", ctx);
        if (upstreams.Count == 0)
        {
            ctx.Warn($"Host '{label}' has no valid upstream; requests receive 502.");
            routes.Add(new JsonObject
            {
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 502,
                    ["body"] = "502 Bad Gateway - no upstream configured",
                }),
            });
            return;
        }
        routes.Add(new JsonObject
        {
            ["handle"] = new JsonArray(ReverseProxy(upstreams, h.UpstreamTlsInsecure, h, stripAuthorization, includeHealthCheck: true, site, ctx)),
        });
    }

    internal static string NormalizeLocationPath(string? path)
    {
        var p = (path ?? "/").Trim();
        if (p.EndsWith('*')) p = p.TrimEnd('*');
        if (!p.StartsWith('/')) p = "/" + p;
        p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    private static List<Upstream> ValidUpstreams(IEnumerable<Upstream> upstreams, string label, Ctx ctx)
    {
        var list = new List<Upstream>();
        foreach (var u in upstreams)
        {
            if (!NetUtil.IsValidHost(u.Host) || !NetUtil.IsValidPort(u.Port))
            {
                ctx.Warn($"{label}: invalid upstream '{u.Host}:{u.Port}' was ignored.");
                continue;
            }
            if (ctx.Input.EndpointGuard?.Check(u.Host, u.Port) is { } problem)
            {
                ctx.Warn($"{label}: upstream ignored: {problem}");
                continue;
            }
            list.Add(u);
        }
        return list;
    }

    /// <summary>request_buffers used on every reverse proxy while HTTP/3 is enabled (see ReverseProxy()).</summary>
    internal const int Http3RequestBufferBytes = 4096;

    private static JsonObject ReverseProxy(List<Upstream> upstreams, bool insecure, SiteHost h, bool stripAuthorization,
        bool includeHealthCheck, Site site, Ctx ctx)
    {
        var label = site.Domains[0];
        var rp = new JsonObject { ["handler"] = "reverse_proxy" };
        var keepClientHost = string.IsNullOrWhiteSpace(h.UpstreamHostHeader);

        var request = HeaderOps(h.RequestHeaders);
        if (!string.IsNullOrWhiteSpace(h.UpstreamHostHeader))
        {
            var v = h.UpstreamHostHeader.Trim();
            request.Set["Host"] = [v == "{upstream}" ? "{http.reverse_proxy.upstream.hostport}" : v];
        }
        else if (upstreams.Any(u => u.Scheme == UpstreamScheme.Https))
        {
            // Caddy sends the upstream's address as Host to HTTPS upstreams unless told otherwise, so "keep the
            // client's Host" has to be explicit there (appliances such as Nutanix Prism build redirects from Host).
            request.Set["Host"] = ["{http.request.hostport}"];
        }
        if (stripAuthorization) request.Delete.Add("Authorization");
        var reqJson = request.ToJson();
        if (reqJson is not null) rp["headers"] = new JsonObject { ["request"] = reqJson };

        if (ctx.Input.Settings.EnableHttp3)
        {
            // Requests that arrive over HTTP/3 have a body stream of unknown length even when empty, so Caddy forwards
            // a bodiless GET to HTTP/2 upstreams as HEADERS without END_STREAM plus an empty DATA frame; strict
            // upstreams reject that (Nutanix Prism's Envoy answers 503 "upstream connect error"). Buffering a few KB
            // lets Caddy see the empty body and send it with Content-Length: 0 and END_STREAM (reverseproxy.go
            // prepareRequest, v2.11.4); larger bodies still stream after the first bytes.
            // https://caddyserver.com/docs/json/apps/http/servers/routes/handle/reverse_proxy/request_buffers/
            rp["request_buffers"] = Http3RequestBufferBytes;
        }

        var https = upstreams.Any(u => u.Scheme == UpstreamScheme.Https);
        var sniNames = https && insecure && keepClientHost ? site.Domains.Where(d => !IsWildcard(d)).ToList() : [];
        var ntlm = h.UpstreamNtlm && NtlmAvailable(label, ctx);
        if (https || ntlm)
        {
            // http_ntlm embeds the standard HTTP transport, so tls options sit at the same level.
            var transport = new JsonObject { ["protocol"] = ntlm ? "http_ntlm" : "http" };
            if (https)
            {
                if (upstreams.Any(u => u.Scheme == UpstreamScheme.Http))
                    ctx.Warn($"Host '{label}': upstreams mix http and https; Caddy uses one transport per proxy, so all are contacted over HTTPS.");
                var tls = new JsonObject();
                if (insecure)
                {
                    tls["insecure_skip_verify"] = true;
                    // SNI defaults to the dial host (none at all for an IP), while Host carries the client's name; IIS
                    // SNI bindings and Apache/nginx name-based vhosts pick the site by SNI and may answer 421 or the
                    // wrong site. With verification off the name only selects the site, so the host's name is sent.
                    // (With verification on the dial name must stay, or an internal certificate would fail to verify.)
                    // A host with ONE exact name sets it here; hosts with several names get one proxy per name (see
                    // PerNameSni below).
                    if (sniNames is [var only] && site.Domains.Count == 1) tls["server_name"] = only;
                }
                transport["tls"] = tls;
            }
            rp["transport"] = transport;
        }

        if (upstreams.Count > 1 || h.LoadBalancing != LoadBalancingPolicy.RoundRobin)
        {
            var lb = new JsonObject
            {
                ["selection_policy"] = new JsonObject { ["policy"] = PolicyName(h.LoadBalancing) },
            };
            if (upstreams.Count > 1) lb["try_duration"] = "5s";
            rp["load_balancing"] = lb;
        }

        // No passive health checks. Caddy counts EVERY proxy error as a passive failure, including a backend dropping
        // one request's connection, and retries a GET on every upstream within try_duration, so a single request that
        // makes the backends drop the connection marked all of them down and the whole host answered 503 for the
        // fail_duration (repeatable by anyone). Unhealthy upstreams are detected by the optional active check instead
        // (then reported; otherwise "not monitored", CaddyAdminClient.GetUpstreamStatusAsync).
        // https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#passive-health-checks ;
        // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/reverseproxy/reverseproxy.go (countFailure)
        var healthChecks = new JsonObject();
        if (includeHealthCheck && upstreams.Count > 1 && !h.HealthCheck.Enabled
            && h.LoadBalancing is LoadBalancingPolicy.First or LoadBalancingPolicy.IpHash or LoadBalancingPolicy.UriHash or LoadBalancingPolicy.Cookie)
            ctx.Warn($"Host '{label}': with the {h.LoadBalancing} load-balancing policy and no active health check, Caddy keeps choosing an upstream that is down, so its share of requests fails. Enable the active health check (Details tab) so a dead upstream is skipped.");
        if (includeHealthCheck && h.HealthCheck.Enabled)
        {
            var hc = h.HealthCheck;
            var uri = string.IsNullOrWhiteSpace(hc.Path) ? "/" : hc.Path.Trim();
            if (!uri.StartsWith('/')) uri = "/" + uri;
            var active = new JsonObject
            {
                ["uri"] = uri,
                ["interval"] = $"{Math.Max(1, hc.IntervalSeconds)}s",
                ["timeout"] = $"{Math.Max(1, hc.TimeoutSeconds)}s",
            };
            // 1-5 = any status of that class (e.g. 3 = 3xx); Caddy's StatusCodeMatches supports classes.
            if (hc.ExpectStatus > 0) active["expect_status"] = hc.ExpectStatus;
            // Active checks send the upstream address as Host and do not apply the proxy's header operations, so a
            // backend that routes by Host (IIS bindings, appliances redirecting IP access) failed its check and the
            // host answered 503. Send the Host that proxied requests carry. Request placeholders are unavailable here.
            // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/reverseproxy/healthchecks.go
            var healthHost = HealthCheckHost(h, site);
            if (healthHost is not null) active["headers"] = new JsonObject { ["Host"] = new JsonArray(healthHost) };
            healthChecks["active"] = active;
        }
        if (healthChecks.Count > 0) rp["health_checks"] = healthChecks;

        var ups = new JsonArray();
        foreach (var u in upstreams) ups.Add(new JsonObject { ["dial"] = NetUtil.HostPort(u.Host, u.Port) });
        rp["upstreams"] = ups;
        return sniNames.Count > 0 && site.Domains.Count > 1 ? PerNameSni(rp, sniNames) : rp;
    }

    /// <summary>
    /// One reverse_proxy per exact name, each sending that name as TLS SNI, and a last one without server_name for
    /// the host's wildcard names. A single proxy cannot do this: a request-time "{http.request.host}" server_name is
    /// only evaluated when a connection is DIALLED, and the transport pools connections per upstream address, not per
    /// SNI, so a connection opened for a.test was reused for b.test and strict upstreams answered 421 Misdirected
    /// Request. Every handler has its own transport, so its own connection pool.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/reverseproxy/httptransport.go (DialTLSContext)
    /// </summary>
    private static JsonObject PerNameSni(JsonObject rp, List<string> names)
    {
        var routes = new JsonArray();
        foreach (var name in names)
        {
            var clone = (JsonObject)rp.DeepClone();
            clone["transport"]!["tls"]!["server_name"] = name;
            routes.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject { ["host"] = new JsonArray(name) }),
                ["handle"] = new JsonArray(clone),
                ["terminal"] = true,
            });
        }
        routes.Add(new JsonObject { ["handle"] = new JsonArray(rp) });
        return new JsonObject { ["handler"] = "subroute", ["routes"] = routes };
    }

    /// <summary>True when the installed binary has the http_ntlm transport; otherwise warns (the flag is skipped).</summary>
    private static bool NtlmAvailable(string label, Ctx ctx)
    {
        var modules = ctx.Input.InstalledModules;
        if (modules is not null && modules.Contains(NtlmModule, StringComparer.Ordinal)) return true;
        ctx.Warn(modules is null
            ? $"Host '{label}': Windows authentication (NTLM) pass-through needs Caddy's http_ntlm transport, but the modules of the Caddy binary are unknown (is Caddy installed?), so the host is proxied with the standard transport and NTLM/Negotiate logins will fail. Install Caddy with the plugin '{NtlmPlugin}' (Caddy > Plugins)."
            : $"Host '{label}': Windows authentication (NTLM) pass-through needs Caddy's http_ntlm transport, which the installed Caddy binary does not include, so the host is proxied with the standard transport and NTLM/Negotiate logins will fail. Add the plugin '{NtlmPlugin}' under Caddy > Plugins and rebuild Caddy.");
        return false;
    }

    /// <summary>The Host header for active health checks, or null to keep Caddy's default (the upstream address).</summary>
    private static string? HealthCheckHost(SiteHost h, Site site)
    {
        var configured = h.UpstreamHostHeader?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return configured.Contains('{') ? null : configured; // "{upstream}" = the default; other placeholders are request-bound
        return site.Domains.FirstOrDefault(d => !IsWildcard(d));
    }

    public static string PolicyName(LoadBalancingPolicy p) => p switch
    {
        LoadBalancingPolicy.RoundRobin => "round_robin",
        LoadBalancingPolicy.Random => "random",
        LoadBalancingPolicy.LeastConn => "least_conn",
        // ip_hash hashes the immediate peer, so behind a load balancer every client landed on one upstream.
        // client_ip_hash uses the client IP from trusted proxies and equals ip_hash without them.
        // https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#load-balancing
        LoadBalancingPolicy.IpHash => "client_ip_hash",
        LoadBalancingPolicy.First => "first",
        LoadBalancingPolicy.Cookie => "cookie",
        LoadBalancingPolicy.UriHash => "uri_hash",
        _ => "round_robin",
    };

    /// <summary>
    /// 308 to the same host and URI over HTTPS on the PUBLIC HTTPS port: CaddySettings.PublicHttpsPort (the port
    /// clients reach, e.g. 443 forwarded by NAT to an internal 8443), or HttpsPort when it is not set. Port 443 is
    /// omitted. Caddy's own redirect cannot express this ("https_port ... is for internal use only", autohttps.go).
    /// The host comes from the Host header with any port removed but IPv6 brackets kept: {http.request.host} strips the
    /// brackets (net.SplitHostPort), which turned "[::1]:8080" into "https://::1:8443/". {http.request.uri} keeps the
    /// path escaped as the client sent it (URL.RequestURI).
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/replacer.go ;
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/autohttps.go
    /// </summary>
    private static JsonObject HttpsRedirectHandler(CaddySettings s)
    {
        static JsonObject To(string location) => new()
        {
            ["handler"] = "static_response",
            ["status_code"] = 308,
            ["headers"] = new JsonObject { ["Location"] = new JsonArray(location) },
            ["close"] = true,
        };
        var port = PublicHttpsPort(s);
        var portPart = port == 443 ? "" : ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["handler"] = "subroute",
            ["routes"] = new JsonArray(
                new JsonObject
                {
                    ["match"] = new JsonArray(new JsonObject
                    {
                        ["vars_regexp"] = new JsonObject
                        {
                            ["{http.request.hostport}"] = new JsonObject { ["name"] = "cpm_redir_host", ["pattern"] = @"^(\[[^\]]+\]|[^:\[\]]+)(:[0-9]*)?$" },
                        },
                    }),
                    ["handle"] = new JsonArray(To("https://{http.regexp.cpm_redir_host.1}" + portPart + "{http.request.uri}")),
                    ["terminal"] = true,
                },
                // Anything else (not a well-formed Host): Caddy's host placeholder.
                new JsonObject { ["handle"] = new JsonArray(To("https://{http.request.host}" + portPart + "{http.request.uri}")) }),
        };
    }

    /// <summary>The HTTPS port clients use: PublicHttpsPort when set (NAT/port forwarding), else HttpsPort.</summary>
    internal static int PublicHttpsPort(CaddySettings s) => s.PublicHttpsPort is int p && NetUtil.IsValidPort(p) ? p : s.HttpsPort;

    /// <summary>
    /// Escapes braces in user text that Caddy would otherwise read as placeholders: static_response header values
    /// go through ReplaceAll, which turns unknown "{...}" into an empty string. Real placeholders ({http.*}, {env.*},
    /// {system.*}, {time.*}, {file.*}) are kept. https://caddyserver.com/docs/conventions#placeholders
    /// </summary>
    internal static string EscapeLiteralBraces(string value) =>
        PlaceholderToken().Replace(value, m => IsCaddyPlaceholder(m.Value) ? m.Value : m.Value.Replace("{", "\\{").Replace("}", "\\}"));

    private static bool IsCaddyPlaceholder(string token) =>
        token.Length > 2 && token[1..^1] is var name && name.Length > 0
        && new[] { "http.", "env.", "system.", "time.", "file." }.Any(p => name.StartsWith(p, StringComparison.Ordinal))
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' || c is '_' || c is '-' || c is ':' || c is '/');

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[^{}]*\}|[{}]", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlaceholderToken();

    private static JsonObject RedirectHandler(SiteHost h, string label, Ctx ctx)
    {
        var target = (h.RedirectTarget ?? "").Trim();
        if (!NetUtil.IsHttpUrl(target)) ctx.Warn($"Host '{label}': redirect target '{target}' is not an absolute http(s) URL.");
        target = EscapeLiteralBraces(target);
        var code = h.RedirectCode is 301 or 302 or 303 or 307 or 308 ? h.RedirectCode : 301;
        JsonObject To(string location) => new()
        {
            ["handler"] = "static_response",
            ["status_code"] = code,
            ["headers"] = new JsonObject { ["Location"] = new JsonArray(location) },
        };
        if (!h.PreservePath) return To(target);
        var q = target.IndexOf('?');
        // {http.request.uri} is the request target as sent (escaped path + query, URL.RequestURI).
        if (q < 0) return To(target.TrimEnd('/') + "{http.request.uri}");
        // The target has its own query: the request path goes before it, the request's query (if any) after it with
        // "&". Placeholders cannot express "only if not empty", so requests with and without a query get their own
        // route. The path must stay ESCAPED: {http.request.uri.path} is the decoded path, so "/x%3Fy%23z" became
        // "/p/x?y#z?a=1"; it is taken from {http.request.uri} (escaped) with a regexp instead.
        // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/replacer.go (http.request.uri, .path)
        var basePath = target[..q].TrimEnd('/');
        var query = target[(q + 1)..];
        var queryPart = query.Length == 0 ? "" : "?" + query;
        JsonObject UriMatch(string name, string pattern) => new()
        {
            ["vars_regexp"] = new JsonObject { ["{http.request.uri}"] = new JsonObject { ["name"] = name, ["pattern"] = pattern } },
        };
        return new JsonObject
        {
            ["handler"] = "subroute",
            ["routes"] = new JsonArray(
                new JsonObject
                {
                    ["match"] = new JsonArray(UriMatch("cpm_redir_q", @"^([^?]*)\?(.+)$")),
                    ["handle"] = new JsonArray(To(basePath + "{http.regexp.cpm_redir_q.1}" + (query.Length == 0 ? "?" : queryPart + "&") + "{http.regexp.cpm_redir_q.2}")),
                    ["terminal"] = true,
                },
                new JsonObject
                {
                    ["match"] = new JsonArray(UriMatch("cpm_redir_p", @"^([^?]*)")),
                    ["handle"] = new JsonArray(To(basePath + "{http.regexp.cpm_redir_p.1}" + queryPart)),
                }),
        };
    }

    /// <summary>
    /// Request paths a static site never serves: dotfiles/dot-folders (.git, .env, .htpasswd) and IIS web.config, also
    /// with trailing dots or spaces, which Windows ignores when it opens a file ("web.config." is web.config). Caddy
    /// itself rejects alternate data streams (":") and 8.3 short names on Windows (fileserver/staticfiles.go ServeHTTP).
    /// </summary>
    internal const string StaticHiddenPathPattern = @"(?i)(/\.|/web\.config[. ]*$)";
    /// <summary>Hidden names inside /.well-known/ (a dotfile or web.config below it).</summary>
    internal const string StaticHiddenWellKnownPattern = @"(?i)^/\.well-known/(.*/)?(\.|web\.config[. ]*$)";

    private static void AddStaticRoutes(JsonArray routes, SiteHost h, string label, Ctx ctx)
    {
        var root = (h.RootPath ?? "").Trim();
        if (root.Length == 0)
        {
            // Fail closed: an empty root makes file_server fall back to {http.vars.root}, i.e. "." - the service's
            // working directory (C:\Windows\System32 for a Windows service).
            // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/fileserver/staticfiles.go
            ctx.Warn($"Host '{label}': static site has no root folder; requests receive 503.");
            routes.Add(new JsonObject
            {
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 503,
                    ["body"] = "503 Service Unavailable - no root folder configured",
                    ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/plain; charset=utf-8") },
                }),
            });
            return;
        }
        // file_server hides nothing by default (only the Caddyfile adapter hides the Caddyfile), so .git/, .env and
        // web.config were downloadable. Answer 404 for them, except under /.well-known/ (RFC 8615: security.txt,
        // apple-app-site-association, ...) where only dotfiles inside are hidden. `hide` alone is not enough: it is
        // case-sensitive, and it matches every component of the absolute path, so ".*" would also hide .well-known.
        // Matcher sets are OR'ed; the path matcher is case-insensitive.
        // https://caddyserver.com/docs/caddyfile/directives/file_server ("should not be treated as a security boundary")
        routes.Add(new JsonObject
        {
            ["match"] = new JsonArray(
                new JsonObject
                {
                    ["path_regexp"] = new JsonObject { ["name"] = "cpm_hidden", ["pattern"] = StaticHiddenPathPattern },
                    ["not"] = new JsonArray(new JsonObject { ["path"] = new JsonArray(WellKnownPath) }),
                },
                new JsonObject
                {
                    ["path_regexp"] = new JsonObject { ["name"] = "cpm_hidden_wk", ["pattern"] = StaticHiddenWellKnownPattern },
                }),
            ["handle"] = new JsonArray(new JsonObject
            {
                ["handler"] = "static_response",
                ["status_code"] = 404,
                ["body"] = "404 Not Found\n",
                ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/plain; charset=utf-8") },
            }),
        });
        if (h.SpaFallback)
        {
            routes.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["root"] = root,
                        // Directories only match a try_files entry that ends in "/", so "/sub/" (with an index.html)
                        // fell through to the SPA index. https://caddyserver.com/docs/caddyfile/matchers#file
                        ["try_files"] = new JsonArray("{http.request.uri.path}", "{http.request.uri.path}/", "/index.html"),
                    },
                }),
                ["handle"] = new JsonArray(new JsonObject { ["handler"] = "rewrite", ["uri"] = "{http.matchers.file.relative}" }),
            });
        }
        // /.well-known/ files are served by a file_server without `hide` (the route above already answers 404 for
        // dotfiles inside it); no directory listing there.
        routes.Add(new JsonObject
        {
            ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray(WellKnownPath) }),
            ["handle"] = new JsonArray(new JsonObject { ["handler"] = "file_server", ["root"] = root }),
            ["terminal"] = true,
        });
        var fs = new JsonObject { ["handler"] = "file_server", ["root"] = root };
        // Also keep dotfiles out of directory listings, unless the root itself lies below a dot-folder (then `hide`
        // would hide the whole site, as it compares every component of the absolute path).
        if (!root.Replace('\\', '/').Split('/').Any(c => c.StartsWith('.') && c is not "." and not ".."))
            fs["hide"] = new JsonArray(".*");
        if (h.Browse) fs["browse"] = new JsonObject();
        routes.Add(new JsonObject { ["handle"] = new JsonArray(fs) });
    }

    private const string WellKnownPath = "/.well-known/*";

    private static JsonObject ResponseHandler(SiteHost h)
    {
        // 1xx codes are informational: Caddy sends them and then an implicit 200 (103 even continues the chain).
        // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/staticresp.go
        var status = h.ResponseStatus is >= 200 and <= 599 ? h.ResponseStatus : 200;
        var o = new JsonObject { ["handler"] = "static_response", ["status_code"] = status };
        if (!string.IsNullOrEmpty(h.ResponseBody)) o["body"] = h.ResponseBody;
        var ct = string.IsNullOrWhiteSpace(h.ResponseContentType) ? "text/plain; charset=utf-8" : h.ResponseContentType.Trim();
        o["headers"] = new JsonObject { ["Content-Type"] = new JsonArray(ct) };
        return o;
    }

    private static JsonObject DefaultSiteRoute(CaddySettings s, Ctx ctx)
    {
        JsonObject handler;
        switch (s.DefaultSite)
        {
            case DefaultSiteBehavior.CloseConnection:
                handler = new JsonObject { ["handler"] = "static_response", ["abort"] = true };
                break;
            case DefaultSiteBehavior.Redirect when NetUtil.IsHttpUrl(s.DefaultRedirectUrl):
                handler = new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 302,
                    ["headers"] = new JsonObject { ["Location"] = new JsonArray(EscapeLiteralBraces(s.DefaultRedirectUrl!.Trim())) },
                };
                break;
            case DefaultSiteBehavior.CaddyWelcome:
                handler = new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 200,
                    ["body"] = "Caddy works! This server is managed by Caddy Proxy Manager, but no site is configured for this host name.\n",
                    ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/plain; charset=utf-8") },
                };
                break;
            default:
                if (s.DefaultSite == DefaultSiteBehavior.Redirect)
                    ctx.Warn("Default site is set to redirect but the redirect URL is not a valid absolute http(s) URL; unknown hosts receive 404.");
                handler = new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 404,
                    ["body"] = "404 Not Found\n",
                    ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/plain; charset=utf-8") },
                };
                break;
        }
        return new JsonObject { ["handle"] = new JsonArray(handler), ["terminal"] = true };
    }

    // ------------------------------------------------------------------ servers

    private static JsonArray Listen(CaddySettings s, int port)
    {
        var binds = s.BindAddresses.Where(NetUtil.IsValidBindAddress).Select(b => b.Trim()).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
        return binds.Count == 0
            ? new JsonArray(NetUtil.ListenAddress(null, port))
            : StringArray(binds.Select(b => NetUtil.ListenAddress(b, port)));
    }

    private static void ApplyCommonServerOptions(JsonObject srv, CaddySettings s, SortedDictionary<string, string> loggerNames, Ctx ctx)
    {
        var proxies = s.TrustedProxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        var valid = new List<string>();
        foreach (var p in proxies)
        {
            if (!p.Equals("all", StringComparison.OrdinalIgnoreCase) && NetUtil.NormalizeCidr(p) is { } n) { if (!valid.Contains(n)) valid.Add(n); }
            else ctx.Warn($"Trusted proxy '{p}' is not a valid IP/CIDR and was ignored.");
        }
        if (valid.Count > 0)
        {
            srv["trusted_proxies"] = new JsonObject { ["source"] = "static", ["ranges"] = StringArray(valid) };
            // Without strict mode Caddy takes the LEFT-most valid X-Forwarded-For entry, which the client controls:
            // "X-Forwarded-For: 10.1.1.1" sent by an attacker through the proxy passed an allow-10.1.1.1 access list.
            // Strict mode reads right to left and uses the first address that is not a trusted proxy.
            // https://caddyserver.com/docs/caddyfile/options#trusted-proxies-strict
            srv["trusted_proxies_strict"] = 1;
        }

        if (loggerNames.Count > 0)
        {
            var names = new JsonObject();
            foreach (var (domain, logger) in loggerNames) names[domain] = new JsonArray(logger);
            srv["logs"] = new JsonObject { ["logger_names"] = names, ["skip_unmapped_hosts"] = true };
        }
    }

    private static void MergeServerOptions(JsonObject srv, CaddySettings s, Ctx ctx)
    {
        if (string.IsNullOrWhiteSpace(s.ServerOptionsJson)) return;
        try
        {
            if (JsonNode.Parse(s.ServerOptionsJson) is not JsonObject extra)
            {
                ctx.Warn("Server options JSON must be a JSON object; it was ignored.");
                return;
            }
            foreach (var (k, v) in extra)
            {
                if (k is "listen" or "routes") { ctx.Warn($"Server option '{k}' is managed by Caddy Proxy Manager and was ignored."); continue; }
                srv[k] = v?.DeepClone();
            }
        }
        catch (JsonException ex)
        {
            ctx.Warn($"Server options JSON is invalid ({ex.Message}); it was ignored.");
        }
    }

    /// <summary>
    /// SNI-specific policies for custom certificates — exact names first, then wildcards (more specific first) — and a
    /// final catch-all. CaddySettings.TlsConnectionPolicyJson is merged into every policy, the catch-all included.
    /// </summary>
    private static JsonArray? ConnectionPolicies(List<Site> httpsSites, Ctx ctx)
    {
        var extra = ConnectionPolicyExtra(ctx);
        var custom = httpsSites.Where(x => x.Host.Tls == TlsMode.Custom && x.Certificate is not null).ToList();
        if (custom.Count == 0 && extra is null) return null;
        var arr = new JsonArray();
        foreach (var site in custom)
        {
            var exact = site.Domains.Where(d => !IsWildcard(d)).ToList();
            if (exact.Count > 0) arr.Add(SniPolicy(exact, site.Certificate!));
        }
        // ACME/Internal names covered by a custom wildcard: policies are first-match-wins and the SNI matcher honours
        // wildcards, so without this policy the custom *.example.com policy would serve them its tagged certificate.
        // https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddytls/connpolicy.go
        var customWildcards = custom.SelectMany(x => x.Domains.Where(IsWildcard)).ToList();
        var managedUnderCustom = httpsSites.Where(x => x.Host.Tls != TlsMode.Custom)
            .SelectMany(x => x.Domains)
            .Where(d => customWildcards.Any(w => CoveredByWildcard(d, w)))
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (managedUnderCustom.Count > 0)
            arr.Add(new JsonObject { ["match"] = new JsonObject { ["sni"] = StringArray(managedUnderCustom) } });
        var wildcardGroups = custom
            .SelectMany(site => site.Domains.Where(IsWildcard).GroupBy(LabelCount)
                .Select(g => (Depth: g.Key, Domains: g.OrderBy(d => d, StringComparer.Ordinal).ToList(), Site: site)))
            .OrderByDescending(g => g.Depth)
            .ThenBy(g => g.Domains[0], StringComparer.Ordinal)
            .ThenBy(g => g.Site.Host.Id, StringComparer.Ordinal);
        foreach (var g in wildcardGroups) arr.Add(SniPolicy(g.Domains, g.Site.Certificate!));
        arr.Add(new JsonObject());
        if (extra is not null)
            foreach (var policy in arr) CaddyJson.DeepMerge(policy!.AsObject(), extra);
        return arr;

        static JsonObject SniPolicy(List<string> domains, Certificate cert) => new()
        {
            ["match"] = new JsonObject { ["sni"] = StringArray(domains) },
            ["certificate_selection"] = new JsonObject { ["any_tag"] = new JsonArray(CertificateTagPrefix + cert.Id) },
        };
    }

    private static JsonObject? ConnectionPolicyExtra(Ctx ctx)
    {
        var extra = CaddyJson.ParseObject(ctx.Input.Settings.TlsConnectionPolicyJson, out var error);
        if (error is not null)
        {
            ctx.Warn($"The TLS connection policy JSON {error}; it was ignored.");
            return null;
        }
        if (extra is null) return null;
        foreach (var key in ReservedConnectionPolicyKeys)
        {
            if (extra.Remove(key))
                ctx.Warn($"TLS connection policy option '{key}' is managed by Caddy Proxy Manager and was ignored.");
        }
        return extra.Count == 0 ? null : extra;
    }

    private static JsonObject AutomaticHttps(List<Site> sites)
    {
        var skip = sites.Where(x => x.Host.Tls == TlsMode.None).SelectMany(x => x.Domains).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var skipCerts = sites.Where(x => x.Host.Tls == TlsMode.Custom).SelectMany(x => x.Domains).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        // HTTP->HTTPS redirects are generated explicitly on the HTTP server (see Generate), so the behaviour does not
        // depend on whether Caddy manages any certificate.
        var o = new JsonObject { ["disable_redirects"] = true };
        if (skip.Count > 0) o["skip"] = StringArray(skip);
        if (skipCerts.Count > 0)
        {
            o["skip_certificates"] = StringArray(skipCerts);
            // Otherwise a loaded custom certificate (e.g. *.example.com) silently replaces the managed certificate of
            // every ACME/Internal host it covers ("skipping automatic certificate management because one or more
            // matching certificates are already loaded"). Custom names stay unmanaged through skip_certificates.
            // https://caddyserver.com/docs/automatic-https ; .../v2.11.4/modules/caddyhttp/autohttps.go
            o["ignore_loaded_certificates"] = true;
        }
        return o;
    }

    // ------------------------------------------------------------------ TLS app

    private static JsonObject? TlsApp(List<Site> sites, Ctx ctx)
    {
        var s = ctx.Input.Settings;
        var tls = new JsonObject();

        var certs = sites.Where(x => x.Host.Tls == TlsMode.Custom && x.Certificate is not null)
            .Select(x => x.Certificate!).DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        if (certs.Count > 0)
        {
            var files = new JsonArray();
            foreach (var c in certs)
            {
                files.Add(new JsonObject
                {
                    ["certificate"] = c.CertPath,
                    ["key"] = c.KeyPath,
                    ["tags"] = new JsonArray(CertificateTagPrefix + c.Id),
                });
            }
            tls["certificates"] = new JsonObject { ["load_files"] = files };
        }

        // Since v2.10 Caddy serves a managed wildcard for covered subdomains instead of obtaining their own
        // certificates, unless the name is listed in the "automate" loader (tls.go Manage: managingWildcardFor looks
        // at the names being managed, not at whether the wildcard was ever issued). So an exact host is forced into
        // its own certificate when the covering wildcard host uses another TLS mode (ACME vs Internal: it would get
        // the other host's certificate and issuer), or when the wildcard is ACME without a DNS challenge (a public
        // wildcard cannot be issued then, and the exact host would never get any certificate).
        // https://github.com/caddyserver/caddy/releases/tag/v2.10.0 ; .../v2.11.4/modules/caddytls/tls.go (Manage)
        DnsProviderName(AcmeIssuerExtra(ctx), out var acmeHasDnsChallenge);
        var managedWildcards = sites.Where(x => x.Host.Tls is TlsMode.Acme or TlsMode.Internal)
            .SelectMany(x => x.Domains.Where(IsWildcard).Select(w => (Wildcard: w, x.Host.Tls))).ToList();
        var automate = sites.Where(x => x.Host.Tls is TlsMode.Acme or TlsMode.Internal)
            .SelectMany(x => x.Domains.Where(d => !IsWildcard(d)).Select(d => (Domain: d, x.Host.Tls)))
            .Where(x => managedWildcards.Any(w => CoveredByWildcard(x.Domain, w.Wildcard)
                && (w.Tls != x.Tls || (w.Tls == TlsMode.Acme && !acmeHasDnsChallenge))))
            .Select(x => x.Domain).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (automate.Count > 0)
        {
            var certsObj = tls["certificates"] as JsonObject ?? new JsonObject();
            certsObj["automate"] = StringArray(automate);
            tls["certificates"] = certsObj;
        }

        var policies = new JsonArray();
        var internalDomains = sites.Where(x => x.Host.Tls == TlsMode.Internal).SelectMany(x => x.Domains)
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var acmeDomains = sites.Where(x => x.Host.Tls == TlsMode.Acme).SelectMany(x => x.Domains)
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();

        if (internalDomains.Count > 0)
        {
            policies.Add(new JsonObject
            {
                ["subjects"] = StringArray(internalDomains),
                ["issuers"] = new JsonArray(new JsonObject { ["module"] = "internal" }),
            });
        }
        if (acmeDomains.Count > 0)
        {
            var issuerExtra = AcmeIssuerExtra(ctx);
            var dnsProvider = DnsProviderName(issuerExtra, out var hasDnsChallenge);
            if (!hasDnsChallenge)
            {
                foreach (var w in acmeDomains.Where(IsWildcard))
                    ctx.Warn($"'{w}': wildcard certificates from a public ACME CA need the DNS challenge, which requires a DNS provider plugin (caddy-dns) and its settings in the ACME issuer JSON under Settings > Caddy. Use Internal or Custom TLS for wildcards, or expect issuance to fail.");
                if (s.DisableHttpChallenge && s.DisableTlsAlpnChallenge)
                    ctx.Warn("Both the HTTP and TLS-ALPN ACME challenges are disabled and no DNS challenge is configured; public certificates cannot be obtained.");
            }
            if (dnsProvider is not null && ctx.Input.InstalledModules is { } modules && !modules.Contains("dns.providers." + dnsProvider, StringComparer.Ordinal))
                ctx.Warn($"The ACME issuer JSON uses the DNS provider '{dnsProvider}', but the installed Caddy binary does not include the module 'dns.providers.{dnsProvider}'. Add the matching caddy-dns plugin (e.g. github.com/caddy-dns/{dnsProvider}) under Caddy > Plugins and rebuild Caddy.");
            var issuers = AcmeIssuers(s, ctx);
            if (issuerExtra is not null)
                foreach (var iss in issuers) CaddyJson.DeepMerge(iss!.AsObject(), issuerExtra);
            policies.Add(new JsonObject
            {
                ["subjects"] = StringArray(acmeDomains),
                ["issuers"] = issuers,
            });
        }
        if (policies.Count > 0) tls["automation"] = new JsonObject { ["policies"] = policies };

        return tls.Count == 0 ? null : tls;
    }

    private static JsonObject? AcmeIssuerExtra(Ctx ctx)
    {
        var extra = CaddyJson.ParseObject(ctx.Input.AcmeIssuerJson, out var error);
        if (error is not null)
        {
            ctx.Warn($"The ACME issuer JSON {error}; it was ignored.");
            return null;
        }
        if (extra is null) return null;
        if (extra.Remove("module")) ctx.Warn("ACME issuer option 'module' is managed by Caddy Proxy Manager and was ignored.");
        return extra.Count == 0 ? null : extra;
    }

    /// <summary>The DNS provider module name configured in the ACME issuer JSON (challenges.dns.provider.name).</summary>
    internal static string? DnsProviderName(JsonObject? issuerExtra, out bool hasDnsChallenge)
    {
        hasDnsChallenge = issuerExtra?["challenges"]?["dns"] is JsonObject;
        return issuerExtra?["challenges"]?["dns"]?["provider"]?["name"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>().Trim()
            : null;
    }

    private static JsonArray AcmeIssuers(CaddySettings s, Ctx ctx)
    {
        var eabKeyId = s.EabKeyId?.Trim();
        var eabMac = ctx.Input.EabMacKey?.Trim();
        var hasEab = !string.IsNullOrEmpty(eabKeyId) && !string.IsNullOrEmpty(eabMac);

        var issuers = new JsonArray();
        switch (s.AcmeCa)
        {
            case AcmeCa.LetsEncrypt:
                issuers.Add(AcmeIssuer(s, LetsEncryptDirectory, null, null));
                // Fallback to ZeroSSL only when EAB credentials were configured for it.
                if (hasEab) issuers.Add(AcmeIssuer(s, ZeroSslDirectory, eabKeyId, eabMac));
                break;
            case AcmeCa.LetsEncryptStaging:
                issuers.Add(AcmeIssuer(s, LetsEncryptStagingDirectory, null, null));
                break;
            case AcmeCa.ZeroSsl:
                if (!hasEab && string.IsNullOrWhiteSpace(s.AcmeEmail))
                    ctx.Warn("ZeroSSL needs either EAB credentials or an ACME e-mail address.");
                issuers.Add(AcmeIssuer(s, ZeroSslDirectory, hasEab ? eabKeyId : null, hasEab ? eabMac : null));
                break;
            case AcmeCa.Custom:
                var dir = s.CustomAcmeDirectory?.Trim();
                if (!NetUtil.IsHttpUrl(dir))
                {
                    ctx.Warn("Custom ACME CA selected but the directory URL is not valid; falling back to Let's Encrypt.");
                    dir = LetsEncryptDirectory;
                }
                issuers.Add(AcmeIssuer(s, dir!, hasEab ? eabKeyId : null, hasEab ? eabMac : null));
                break;
        }
        return issuers;
    }

    private static JsonObject AcmeIssuer(CaddySettings s, string directory, string? eabKeyId, string? eabMac)
    {
        var iss = new JsonObject { ["module"] = "acme", ["ca"] = directory };
        if (!string.IsNullOrWhiteSpace(s.AcmeEmail)) iss["email"] = s.AcmeEmail.Trim();
        if (eabKeyId is not null && eabMac is not null)
            iss["external_account"] = new JsonObject { ["key_id"] = eabKeyId, ["mac_key"] = eabMac };
        if (s.AcmeCa == AcmeCa.Custom && !string.IsNullOrWhiteSpace(s.CustomAcmeRootPath))
            iss["trusted_roots_pem_files"] = new JsonArray(s.CustomAcmeRootPath.Trim());

        var challenges = new JsonObject();
        var http = new JsonObject();
        if (s.DisableHttpChallenge) http["disabled"] = true;
        else if (s.HttpPort != 80) http["alternate_port"] = s.HttpPort;
        var alpn = new JsonObject();
        if (s.DisableTlsAlpnChallenge) alpn["disabled"] = true;
        else if (s.HttpsPort != 443) alpn["alternate_port"] = s.HttpsPort;
        if (http.Count > 0) challenges["http"] = http;
        if (alpn.Count > 0) challenges["tls-alpn"] = alpn;
        if (challenges.Count > 0) iss["challenges"] = challenges;
        return iss;
    }

    // ------------------------------------------------------------------ layer4

    private static JsonObject? Layer4App(Ctx ctx)
    {
        var streams = ctx.Input.Streams.Where(x => x.Enabled).ToList();
        if (streams.Count == 0) return null;
        var modules = ctx.Input.InstalledModules;
        if (modules is null || !modules.Contains(Layer4Module, StringComparer.Ordinal))
        {
            ctx.Warn($"{streams.Count} stream(s) {StreamsSkippedWarningMarker}: the installed Caddy binary does not include the layer4 module. Add the plugin '{Layer4Plugin}' under Caddy > Plugins and rebuild.");
            return null;
        }

        var servers = new JsonObject();
        var binds = ctx.Input.Settings.BindAddresses.Where(NetUtil.IsValidBindAddress).Select(b => b.Trim()).Distinct()
            .OrderBy(b => b, StringComparer.Ordinal).ToList();
        foreach (var st in streams.OrderBy(x => x.Protocol).ThenBy(x => x.ListenPort).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!NetUtil.IsValidPort(st.ListenPort) || !NetUtil.IsValidHost(st.UpstreamHost) || !NetUtil.IsValidPort(st.UpstreamPort))
            {
                ctx.Warn($"Stream {st.Protocol.ToString().ToLowerInvariant()}/{st.ListenPort} is invalid and was skipped.");
                continue;
            }
            var proto = st.Protocol == StreamProtocol.Udp ? "udp" : "tcp";
            if (ctx.Input.EndpointGuard?.Check(st.UpstreamHost, st.UpstreamPort) is { } problem)
            {
                ctx.Warn($"Stream {proto}/{st.ListenPort} was skipped: {problem}");
                continue;
            }
            var listen = binds.Count == 0
                ? new JsonArray($"{proto}/:{st.ListenPort}")
                : StringArray(binds.Select(b => $"{proto}/{NetUtil.ListenAddress(b, st.ListenPort)}"));
            servers[st.Id] = new JsonObject
            {
                ["listen"] = listen,
                ["routes"] = new JsonArray(new JsonObject
                {
                    ["handle"] = new JsonArray(new JsonObject
                    {
                        ["handler"] = "proxy",
                        ["upstreams"] = new JsonArray(new JsonObject
                        {
                            ["dial"] = new JsonArray($"{proto}/{NetUtil.HostPort(st.UpstreamHost, st.UpstreamPort)}"),
                        }),
                    }),
                }),
            };
        }
        return servers.Count == 0 ? null : new JsonObject { ["servers"] = servers };
    }

    // ------------------------------------------------------------------ extra apps

    private static void MergeExtraApps(JsonObject apps, CaddySettings s, Ctx ctx)
    {
        var extra = CaddyJson.ParseObject(s.ExtraAppsJson, out var error);
        if (error is not null)
        {
            ctx.Warn($"The extra apps JSON {error}; it was ignored.");
            return;
        }
        if (extra is null) return;
        foreach (var (name, value) in extra)
        {
            if (ReservedApps.Contains(name, StringComparer.Ordinal))
            {
                ctx.Warn($"Extra app '{name}' is generated by Caddy Proxy Manager and cannot be overridden; it was ignored.");
                continue;
            }
            if (value is not JsonObject)
            {
                ctx.Warn($"Extra app '{name}' must be a JSON object; it was ignored.");
                continue;
            }
            apps[name] = value.DeepClone();
        }
    }

    // ------------------------------------------------------------------ shared pieces

    private static JsonObject Admin(CaddySettings s) => new()
    {
        ["listen"] = string.IsNullOrWhiteSpace(s.AdminListen) ? "127.0.0.1:2019" : s.AdminListen.Trim(),
        ["config"] = new JsonObject { ["persist"] = false },
    };

    private static JsonObject Storage(AppPaths paths) => new()
    {
        ["module"] = "file_system",
        ["root"] = paths.CaddyStorageDir,
    };

    /// <summary>Caddy's logger for admin API requests. The manager polls the admin API every few seconds.</summary>
    public const string AdminApiLogger = "admin.api";
    /// <summary>Name of the log that receives admin API messages at WARN or above.</summary>
    public const string AdminApiLogName = "cpm_admin_api";

    /// <summary>
    /// The process log (caddy.log): the default logger (level from settings) minus the admin API and
    /// per-host access loggers, plus a separate WARN+ logger for the admin API into the same file.
    /// Without this, every status/config poll from the manager lands in caddy.log at INFO and floods it.
    /// Both logs share the file: Caddy pools writers by file name, so they write through one roller.
    /// </summary>
    private static JsonObject ProcessLoggers(CaddySettings s, AppPaths paths, List<string> accessLoggers)
    {
        var level = (s.LogLevel ?? "info").Trim().ToUpperInvariant();
        if (level is not ("DEBUG" or "INFO" or "WARN" or "ERROR")) level = "INFO";
        var def = new JsonObject
        {
            ["writer"] = ProcessLogWriter(paths),
            ["level"] = level,
            ["exclude"] = StringArray(new[] { AdminApiLogger }.Concat(accessLoggers)),
        };
        var admin = new JsonObject
        {
            ["writer"] = ProcessLogWriter(paths),
            ["level"] = level == "ERROR" ? "ERROR" : "WARN",
            ["include"] = new JsonArray(AdminApiLogger),
        };
        return new JsonObject { ["default"] = def, [AdminApiLogName] = admin };
    }

    private static JsonObject ProcessLogWriter(AppPaths paths) => new()
    {
        ["output"] = "file",
        ["filename"] = paths.CaddyProcessLog,
        ["roll_size_mb"] = 20,
        ["roll_keep"] = 10,
    };

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }
}
