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
public static class CaddyConfigGenerator
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
    // break ordinary applications (such as "?next=/path").
    internal const string ExploitPathPattern =
        @"(?i)(\.\./|\.\.\\|%2e%2e(%2f|%5c|/)|/\.(git|svn|hg|env)(/|$)|/etc/passwd|/proc/self/environ|/wp-config\.php)";
    internal const string ExploitQueryPattern =
        @"(?i)(union.*select.*\(|union.*all.*select|concat.*\(|[a-z0-9_]=http://|[a-z0-9_]=(\.\.//?)+|(<|%3c).*script.*(>|%3e)|globals(=|\[|%[0-9a-z]{0,2})|_request(=|\[|%[0-9a-z]{0,2})|proc/self/environ|mosconfig_[a-z_]{1,21}(=|%3d)|base64_(en|de)code\(.*\))";
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
        return root.ToJsonString();
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
                if (!site.Host.ForceHttps) httpSites.Add((site, handler));
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
            var autoHttps = AutomaticHttps(sites);
            if (autoHttps is not null) srv0["automatic_https"] = autoHttps;
            ApplyCommonServerOptions(srv0, s, loggerNames, ctx);
            srv0["protocols"] = s.EnableHttp3 ? new JsonArray("h1", "h2", "h3") : new JsonArray("h1", "h2");
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
        srv1["protocols"] = new JsonArray("h1", "h2");
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
        var logs = ProcessLoggers(s, input.Paths, accessLoggers.Keys.Select(k => "http.log.access." + k).ToList());
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

    private static int LabelCount(string domain) => domain.Count(c => c == '.') + 1;

    /// <summary>The host's subroute handler (access list, headers, compression, advanced routes, kind handler).</summary>
    private static JsonObject HostHandler(Site site, Ctx ctx)
    {
        var h = site.Host;
        var routes = new JsonArray();
        var stripAuthorization = false;

        // 1. access list
        if (!string.IsNullOrEmpty(h.AccessListId))
        {
            if (ctx.AccessLists.TryGetValue(h.AccessListId, out var al))
                stripAuthorization = AddAccessListRoutes(routes, al, ctx);
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
        var headers = ResponseHeadersHandler(h);
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
                AddProxyRoutes(routes, h, site.Domains[0], stripAuthorization, ctx);
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
                ["header_regexp"] = new JsonObject
                {
                    ["User-Agent"] = new JsonObject { ["name"] = "cpm_exploit_ua", ["pattern"] = ExploitUserAgentPattern },
                },
            }),
        ["handle"] = new JsonArray(Forbidden()),
    };

    private static JsonObject? ResponseHeadersHandler(SiteHost h)
    {
        var ops = HeaderOps(h.ResponseHeaders);
        if (h.Hsts && h.Tls != TlsMode.None)
        {
            var value = $"max-age={Math.Max(0, h.HstsMaxAgeSeconds)}" + (h.HstsSubdomains ? "; includeSubDomains" : "");
            ops.Set["Strict-Transport-Security"] = [value];
        }
        var obj = ops.ToJson();
        if (obj is null) return null;
        // Deferred so the operations also apply to (and override) headers written by upstreams.
        obj["deferred"] = true;
        return new JsonObject { ["handler"] = "headers", ["response"] = obj };
    }

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
            switch (op.Action)
            {
                case HeaderAction.Set:
                    set.Set[name] = [op.Value ?? ""];
                    break;
                case HeaderAction.Add:
                    if (!set.Add.TryGetValue(name, out var list)) set.Add[name] = list = new();
                    list.Add(op.Value ?? "");
                    break;
                case HeaderAction.Delete:
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
            return list;
        }
        catch (JsonException ex)
        {
            ctx.Warn($"Host '{label}': advanced routes are not valid JSON ({ex.Message}); they were ignored.");
            return [];
        }
    }

    // ------------------------------------------------------------------ kind handlers

    private static void AddProxyRoutes(JsonArray routes, SiteHost h, string label, bool stripAuthorization, Ctx ctx)
    {
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
            handles.Add(ReverseProxy(ups, loc.UpstreamTlsInsecure, h, stripAuthorization, includeHealthCheck: false, label, ctx));
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
            ["handle"] = new JsonArray(ReverseProxy(upstreams, h.UpstreamTlsInsecure, h, stripAuthorization, includeHealthCheck: true, label, ctx)),
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

    private static JsonObject ReverseProxy(List<Upstream> upstreams, bool insecure, SiteHost h, bool stripAuthorization,
        bool includeHealthCheck, string label, Ctx ctx)
    {
        var rp = new JsonObject { ["handler"] = "reverse_proxy" };

        var request = HeaderOps(h.RequestHeaders);
        if (!string.IsNullOrWhiteSpace(h.UpstreamHostHeader))
        {
            var v = h.UpstreamHostHeader.Trim();
            request.Set["Host"] = [v == "{upstream}" ? "{http.reverse_proxy.upstream.hostport}" : v];
        }
        if (stripAuthorization) request.Delete.Add("Authorization");
        var reqJson = request.ToJson();
        if (reqJson is not null) rp["headers"] = new JsonObject { ["request"] = reqJson };

        var https = upstreams.Any(u => u.Scheme == UpstreamScheme.Https);
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
                if (insecure) tls["insecure_skip_verify"] = true;
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
            if (hc.ExpectStatus > 0) active["expect_status"] = hc.ExpectStatus;
            rp["health_checks"] = new JsonObject { ["active"] = active };
        }

        var ups = new JsonArray();
        foreach (var u in upstreams) ups.Add(new JsonObject { ["dial"] = NetUtil.HostPort(u.Host, u.Port) });
        rp["upstreams"] = ups;
        return rp;
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

    public static string PolicyName(LoadBalancingPolicy p) => p switch
    {
        LoadBalancingPolicy.RoundRobin => "round_robin",
        LoadBalancingPolicy.Random => "random",
        LoadBalancingPolicy.LeastConn => "least_conn",
        LoadBalancingPolicy.IpHash => "ip_hash",
        LoadBalancingPolicy.First => "first",
        LoadBalancingPolicy.Cookie => "cookie",
        LoadBalancingPolicy.UriHash => "uri_hash",
        _ => "round_robin",
    };

    private static JsonObject RedirectHandler(SiteHost h, string label, Ctx ctx)
    {
        var target = (h.RedirectTarget ?? "").Trim();
        if (!NetUtil.IsHttpUrl(target)) ctx.Warn($"Host '{label}': redirect target '{target}' is not an absolute http(s) URL.");
        if (h.PreservePath) target = target.TrimEnd('/') + "{http.request.uri}";
        var code = h.RedirectCode is 301 or 302 or 303 or 307 or 308 ? h.RedirectCode : 301;
        return new JsonObject
        {
            ["handler"] = "static_response",
            ["status_code"] = code,
            ["headers"] = new JsonObject { ["Location"] = new JsonArray(target) },
        };
    }

    private static void AddStaticRoutes(JsonArray routes, SiteHost h, string label, Ctx ctx)
    {
        var root = (h.RootPath ?? "").Trim();
        if (root.Length == 0) ctx.Warn($"Host '{label}': static site has no root folder.");
        if (h.SpaFallback)
        {
            routes.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["root"] = root,
                        ["try_files"] = new JsonArray("{http.request.uri.path}", "/index.html"),
                    },
                }),
                ["handle"] = new JsonArray(new JsonObject { ["handler"] = "rewrite", ["uri"] = "{http.matchers.file.relative}" }),
            });
        }
        var fs = new JsonObject { ["handler"] = "file_server", ["root"] = root };
        if (h.Browse) fs["browse"] = new JsonObject();
        routes.Add(new JsonObject { ["handle"] = new JsonArray(fs) });
    }

    private static JsonObject ResponseHandler(SiteHost h)
    {
        var status = h.ResponseStatus is >= 100 and <= 999 ? h.ResponseStatus : 200;
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
                    ["headers"] = new JsonObject { ["Location"] = new JsonArray(s.DefaultRedirectUrl!.Trim()) },
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
        var valid = proxies.Where(p => NetUtil.IsValidCidr(p) && !p.Equals("all", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        foreach (var bad in proxies.Except(valid)) ctx.Warn($"Trusted proxy '{bad}' is not a valid IP/CIDR and was ignored.");
        if (valid.Count > 0)
            srv["trusted_proxies"] = new JsonObject { ["source"] = "static", ["ranges"] = StringArray(valid) };

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

    private static JsonObject? AutomaticHttps(List<Site> sites)
    {
        var skip = sites.Where(x => x.Host.Tls == TlsMode.None).SelectMany(x => x.Domains).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var skipCerts = sites.Where(x => x.Host.Tls == TlsMode.Custom).SelectMany(x => x.Domains).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (skip.Count == 0 && skipCerts.Count == 0) return null;
        var o = new JsonObject();
        if (skip.Count > 0) o["skip"] = StringArray(skip);
        if (skipCerts.Count > 0) o["skip_certificates"] = StringArray(skipCerts);
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
