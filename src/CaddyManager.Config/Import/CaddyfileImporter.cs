using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Config.Validation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Import;

/// <summary>Result of mapping an adapted Caddyfile to host drafts. Nothing is saved.</summary>
public sealed record CaddyfileImportResult(List<SiteHost> Drafts, List<string> Unmapped, List<string> Warnings);

/// <summary>
/// Maps the JSON of an adapted Caddyfile (caddy adapt / admin /adapt) to SiteHost drafts: site blocks with
/// reverse_proxy (upstreams, load balancing, active health checks, header_up incl. Host, transport tls /
/// tls_insecure_skip_verify, NTLM transport), handle_path/handle + reverse_proxy (→ locations), redir, file_server
/// (root, browse, try_files SPA fallback), respond, encode, header, log, tls internal. Anything else is preserved
/// verbatim in the draft's advanced routes (and listed in <c>Unmapped</c>) with a warning; basic_auth is preserved
/// that way too so imported sites stay protected.
/// </summary>
public static class CaddyfileImporter
{
    private const string Note = "Imported from a Caddyfile.";

    public static CaddyfileImportResult Import(string adaptedJson, IReadOnlyCollection<SiteHost> existingHosts)
    {
        var ctx = new ImportContext();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(adaptedJson) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            ctx.Warn("The adapted configuration could not be read: " + ex.Message);
            return ctx.Result();
        }

        foreach (var (key, value) in root)
        {
            if (key == "apps") continue;
            ctx.Warn($"The global '{key}' settings of the Caddyfile are not imported: Caddy Proxy Manager generates them.");
            ctx.Unmapped.Add($"(global) {key}: {Compact(value)}");
        }
        var apps = root["apps"] as JsonObject;
        if (apps is null)
        {
            ctx.Warn("The Caddyfile contains no sites.");
            return ctx.Result();
        }
        foreach (var (name, value) in apps)
        {
            if (name is "http" or "tls") continue;
            ctx.Warn($"The '{name}' app of the Caddyfile is not imported. If a plugin needs it, add it under Settings > Caddy > Extra apps.");
            ctx.Unmapped.Add($"(app) {name}: {Compact(value)}");
        }

        var tls = ReadTls(apps["tls"] as JsonObject, ctx);
        if (apps["http"] is not JsonObject http || http["servers"] is not JsonObject servers)
        {
            ctx.Warn("The Caddyfile contains no HTTP sites.");
            return ctx.Result();
        }
        var httpPort = Int(http["http_port"]) ?? 80;
        var httpsPort = Int(http["https_port"]) ?? 443;

        var ordered = servers
            .Where(s => s.Value is JsonObject)
            .Select(s => (Name: s.Key, Server: (JsonObject)s.Value!))
            .Select(s => (s.Name, s.Server, IsHttp: IsHttpServer(s.Server, httpPort)))
            .OrderBy(s => s.IsHttp ? 1 : 0)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var (name, server, isHttp) in ordered)
        {
            var ports = ListenPorts(server).Distinct().ToList();
            var odd = ports.Where(p => p != httpPort && p != httpsPort).ToList();
            foreach (var key in server.Select(p => p.Key))
            {
                if (key is "listen" or "routes" or "tls_connection_policies" or "logs" or "automatic_https") continue;
                ctx.Warn($"Server option '{key}' ({name}) is not imported; global server options can be set under Settings > Caddy > Server options JSON.");
            }
            if (server["routes"] is not JsonArray routes) continue;
            var sni = ConnectionPolicyTags(server);
            foreach (var routeNode in routes)
            {
                if (routeNode is not JsonObject route) continue;
                var domains = HostMatch(route);
                if (domains is null)
                {
                    var listen = string.Join(", ", ListenAddresses(server));
                    ctx.Warn($"A site without host names (listening on {listen}) cannot be imported: Caddy Proxy Manager serves hosts by name on the configured HTTP/HTTPS ports.");
                    ctx.Unmapped.Add($"({listen}) {Compact(route)}");
                    continue;
                }
                if (odd.Count > 0)
                    ctx.Warn($"'{domains[0]}' was served on port {string.Join("/", odd)}; the imported host is served on the configured {(isHttp ? "HTTP" : "HTTPS")} port instead.");

                if (isHttp)
                {
                    var tlsTwin = ctx.Drafts.FirstOrDefault(d => d.Tls != TlsMode.None && d.Domains.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(domains));
                    if (tlsTwin is not null)
                    {
                        // The same site is also served over plain HTTP: keep one host without the HTTPS redirect.
                        tlsTwin.ForceHttps = false;
                        continue;
                    }
                }
                var draft = ImportSite(domains, route, isHttp, IsLogged(server, domains), tls, sni, ctx);
                ctx.Drafts.Add(draft);
            }
        }

        CheckConflicts(ctx, existingHosts);
        foreach (var d in ctx.Drafts) ModelValidation.Normalize(d);
        return ctx.Result();
    }

    // ------------------------------------------------------------------ site

    private static SiteHost ImportSite(List<string> domains, JsonObject route, bool isHttp, bool logged, TlsInfo tls,
        Dictionary<string, string> sniTags, ImportContext ctx)
    {
        var label = domains[0];
        var d = new SiteHost
        {
            Domains = domains,
            Kind = HostKind.Response,
            ResponseStatus = 200,
            Compression = false,
            Tls = isHttp ? TlsMode.None : TlsMode.Acme,
            ForceHttps = true,
            AccessLog = logged,
            Notes = Note,
        };
        var site = new SiteState(d, label);

        IEnumerable<JsonNode?> inner = route["handle"] is JsonArray h && h.Count == 1 && h[0] is JsonObject only && Str(only["handler"]) == "subroute"
            ? (only["routes"] as JsonArray ?? [])
            : [new JsonObject { ["handle"] = route["handle"]?.DeepClone() }];
        foreach (var r in Flatten(inner))
        {
            if (r["match"] is null) ImportUnmatched(r, site, ctx);
            else if (!TryMatchedRoute(r, site, ctx)) site.Preserve(r, ctx, "a route with a request matcher (" + string.Join(", ", HandlerNames(r).Distinct()) + ")");
        }

        // Kind and its fields
        if (site.Main is null)
        {
            if (site.Locations.Count > 0)
            {
                // Locations only (e.g. handle_path blocks without a default): keep them as advanced routes.
                foreach (var (_, original) in site.Locations) site.Preserve(original, ctx, "a path-specific proxy without a default upstream");
                site.Locations.Clear();
            }
            if (site.StaticRoot is not null || site.Spa)
            {
                d.Kind = HostKind.Static;
            }
            else if (site.Advanced.Count > 0)
            {
                d.Kind = HostKind.Response;
                d.ResponseStatus = 404;
                d.ResponseBody = "404 Not Found";
                ctx.Warn($"'{label}': no main handler could be imported; requests that the preserved advanced routes do not answer receive 404.");
            }
            else
            {
                ctx.Warn($"'{label}': the site block has no handler; the draft answers with an empty 200 response like Caddy does.");
            }
        }
        if (d.Kind == HostKind.Static)
        {
            d.RootPath = site.FileServerRoot ?? site.StaticRoot;
            d.SpaFallback = site.Spa;
            if (string.IsNullOrWhiteSpace(d.RootPath))
                ctx.Warn($"'{label}': file_server has no root folder; set the root folder before saving.");
            else if (d.RootPath.Contains('{'))
                ctx.Warn($"'{label}': the root folder '{d.RootPath}' contains a placeholder; replace it with a fixed folder.");
        }
        else if (site.Spa)
        {
            ctx.Warn($"'{label}': try_files without file_server was not imported.");
        }
        if (d.Kind == HostKind.Proxy)
        {
            foreach (var (loc, _) in site.Locations) d.Locations.Add(loc);
        }
        else if (site.Locations.Count > 0)
        {
            foreach (var (_, original) in site.Locations) site.Preserve(original, ctx, "a path-specific proxy on a non-proxy site");
        }

        // TLS
        if (!isHttp)
        {
            var custom = domains.Where(sniTags.ContainsKey).ToList();
            if (domains.All(tls.InternalSubjects.Contains))
            {
                d.Tls = TlsMode.Internal;
            }
            else if (custom.Count > 0)
            {
                var files = custom.Select(x => sniTags[x]).Distinct()
                    .Select(tag => tls.LoadFiles.TryGetValue(tag, out var f) ? $"certificate {f.Cert}, key {f.Key}" : $"tag {tag}")
                    .ToList();
                d.Notes = $"{Note} The Caddyfile used certificate files ({string.Join("; ", files)}). Add them under Certificates (Reference files on disk) and switch this host to Custom TLS.";
                ctx.Warn($"'{label}': 'tls <cert> <key>' used certificate files ({string.Join("; ", files)}). Certificates are not created automatically, so the draft uses ACME; add the files under Certificates and switch the host to Custom TLS before or after importing.");
            }
            else if (domains.Any(tls.InternalSubjects.Contains))
            {
                ctx.Warn($"'{label}': only some of its names used 'tls internal'; the draft uses ACME for all of them.");
            }
        }

        if (site.Advanced.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var a in site.Advanced) arr.Add(a.DeepClone());
            d.AdvancedRoutesJson = CaddyJson.Serialize(arr);
        }
        return d;
    }

    /// <summary>Routes without matchers whose only handler is a subroute are inlined (handle blocks without a matcher).</summary>
    private static IEnumerable<JsonObject> Flatten(IEnumerable<JsonNode?> routes)
    {
        foreach (var node in routes)
        {
            if (node is not JsonObject r) continue;
            if (r["match"] is null && r["handle"] is JsonArray h && h.Count == 1 && h[0] is JsonObject sub &&
                Str(sub["handler"]) == "subroute" && sub["routes"] is JsonArray inner &&
                r.All(p => p.Key is "handle" or "group" or "terminal"))
            {
                foreach (var x in Flatten(inner)) yield return x;
            }
            else
            {
                yield return r;
            }
        }
    }

    private static void ImportUnmatched(JsonObject route, SiteState site, ImportContext ctx)
    {
        if (route["handle"] is not JsonArray handlers) return;
        var rest = new JsonArray();
        foreach (var hn in handlers)
        {
            if (hn is not JsonObject h) continue;
            if (!TryHandler(h, site, ctx)) rest.Add(h.DeepClone());
        }
        if (rest.Count > 0)
        {
            var preserved = new JsonObject { ["handle"] = rest };
            var what = rest.Select(x => Str(x?["handler"]) ?? "?").Distinct();
            site.Preserve(preserved, ctx, "handler " + string.Join(", ", what));
        }
    }

    private static bool TryHandler(JsonObject h, SiteState site, ImportContext ctx)
    {
        var d = site.Host;
        switch (Str(h["handler"]))
        {
            case "vars" when h.Count == 2 && h["root"] is JsonValue:
                site.StaticRoot = Str(h["root"]);
                return true;
            case "encode":
                d.Compression = true;
                return true;
            case "headers" when h.All(p => p.Key is "handler" or "response") && h["response"] is JsonObject resp &&
                                resp.All(p => p.Key is "set" or "add" or "delete" or "deferred"):
                d.ResponseHeaders.AddRange(HeaderOps(resp));
                return true;
            case "reverse_proxy" when site.Main is null:
                if (!TryReverseProxy(h, site.Label, ctx, out var rp)) return false;
                d.Kind = HostKind.Proxy;
                d.Upstreams = rp.Upstreams;
                d.UpstreamTlsInsecure = rp.Insecure;
                d.UpstreamNtlm = rp.Ntlm;
                d.LoadBalancing = rp.Policy;
                d.HealthCheck = rp.Health;
                d.RequestHeaders = rp.RequestHeaders;
                d.UpstreamHostHeader = rp.HostHeader;
                d.ResponseHeaders.AddRange(rp.ResponseHeaders);
                site.Main = "reverse_proxy";
                return true;
            case "file_server" when site.Main is null:
                d.Kind = HostKind.Static;
                site.FileServerRoot = Str(h["root"]);
                d.Browse = h["browse"] is JsonObject;
                foreach (var key in h.Select(p => p.Key).Where(k => k is not ("handler" or "root" or "browse" or "hide")))
                    ctx.Warn($"'{site.Label}': file_server option '{key}' is not supported and was ignored.");
                site.Main = "file_server";
                return true;
            case "static_response" when site.Main is null && h["abort"] is null:
                return TryStaticResponse(h, site, ctx);
            default:
                return false;
        }
    }

    private static bool TryStaticResponse(JsonObject h, SiteState site, ImportContext ctx)
    {
        var d = site.Host;
        var status = Int(h["status_code"]) ?? (int.TryParse(Str(h["status_code"]), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 200);
        var headers = h["headers"] as JsonObject;
        var location = headers?["Location"] is JsonArray loc && loc.Count == 1 ? Str(loc[0]) : null;
        if (location is not null && status is >= 300 and < 400)
        {
            if (h["body"] is not null || headers!.Count > 1) return false;
            d.Kind = HostKind.Redirect;
            d.RedirectCode = status is 301 or 302 or 303 or 307 or 308 ? status : 302;
            d.PreservePath = location.EndsWith("{http.request.uri}", StringComparison.Ordinal);
            d.RedirectTarget = d.PreservePath ? location[..^"{http.request.uri}".Length] : location;
            if (d.RedirectTarget.Contains('{'))
                ctx.Warn($"'{site.Label}': the redirect target '{location}' contains placeholders the manager cannot express; check the target before saving.");
            site.Main = "redir";
            return true;
        }
        if (status is < 100 or > 599) return false;
        d.Kind = HostKind.Response;
        d.ResponseStatus = status;
        d.ResponseBody = Str(h["body"]);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && value is JsonArray ct && ct.Count > 0)
                    d.ResponseContentType = Str(ct[0]) ?? d.ResponseContentType;
                else
                    d.ResponseHeaders.Add(new HeaderOp { Action = HeaderAction.Set, Name = name, Value = value is JsonArray a && a.Count > 0 ? Str(a[0]) ?? "" : "" });
            }
        }
        site.Main = "respond";
        return true;
    }

    /// <summary>try_files SPA fallback, handle_path / handle with a path prefix + reverse_proxy (→ location).</summary>
    private static bool TryMatchedRoute(JsonObject r, SiteState site, ImportContext ctx)
    {
        if (r["match"] is not JsonArray match || match.Count != 1 || match[0] is not JsonObject m || m.Count != 1) return false;
        if (r["handle"] is not JsonArray handle) return false;

        // try_files {path} /index.html → SPA fallback
        if (m["file"] is JsonObject file && file["try_files"] is JsonArray tf && tf.Count == 2 &&
            Str(tf[0]) is "{http.request.uri.path}" && Str(tf[1]) is "/index.html" &&
            file.All(p => p.Key is "try_files" or "root") &&
            handle.Count == 1 && handle[0] is JsonObject rw && Str(rw["handler"]) == "rewrite" && Str(rw["uri"]) == "{http.matchers.file.relative}" && rw.Count == 2)
        {
            site.Spa = true;
            if (Str(file["root"]) is { } fr) site.StaticRoot ??= fr;
            return true;
        }

        // path prefix + (strip) + reverse_proxy → location
        if (m["path"] is JsonArray paths && paths.Count == 1 && Str(paths[0]) is { } p && p.StartsWith('/') && p.EndsWith("/*", StringComparison.Ordinal) && !p[..^2].Contains('*'))
        {
            var handlers = new List<JsonObject>();
            foreach (var x in handle)
            {
                if (x is JsonObject xo && Str(xo["handler"]) == "subroute" && xo["routes"] is JsonArray subRoutes)
                {
                    foreach (var sr in Flatten(subRoutes))
                    {
                        if (sr["match"] is not null || sr["handle"] is not JsonArray sh) return false;
                        handlers.AddRange(sh.OfType<JsonObject>());
                    }
                }
                else if (x is JsonObject xo2) handlers.Add(xo2);
            }
            var prefix = p[..^2];
            var strip = false;
            if (handlers.Count == 2 && Str(handlers[0]["handler"]) == "rewrite" && handlers[0].Count == 2 && Str(handlers[0]["strip_path_prefix"]) == prefix)
            {
                strip = true;
                handlers.RemoveAt(0);
            }
            if (handlers.Count != 1 || Str(handlers[0]["handler"]) != "reverse_proxy") return false;
            var rp = handlers[0];
            if (rp.Any(k => k.Key is not ("handler" or "upstreams" or "transport"))) return false;
            if (!TryReverseProxy(rp, site.Label, ctx, out var parsed) || parsed.Ntlm) return false;
            var loc = new ProxyLocation { Path = prefix, StripPrefix = strip, Upstreams = parsed.Upstreams, UpstreamTlsInsecure = parsed.Insecure };
            site.Locations.Add((loc, r));
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ reverse_proxy

    private sealed record ProxyParts(List<Upstream> Upstreams, bool Insecure, bool Ntlm, LoadBalancingPolicy Policy, HealthCheck Health,
        List<HeaderOp> RequestHeaders, string? HostHeader, List<HeaderOp> ResponseHeaders);

    private static readonly string[] ProxyNotMappable = ["dynamic_upstreams", "handle_response", "rewrite", "request_buffers", "response_buffers"];

    private static bool TryReverseProxy(JsonObject rp, string label, ImportContext ctx, out ProxyParts parts)
    {
        parts = null!;
        if (rp.Any(p => ProxyNotMappable.Contains(p.Key))) return false;
        if (rp["upstreams"] is not JsonArray ups || ups.Count == 0) return false;

        var scheme = UpstreamScheme.Http;
        var insecure = false;
        var ntlm = false;
        if (rp["transport"] is JsonObject transport)
        {
            switch (Str(transport["protocol"]))
            {
                case "http":
                    break;
                case "http_ntlm":
                    ntlm = true;
                    break;
                default:
                    return false; // fastcgi and others
            }
            if (transport["tls"] is JsonObject t)
            {
                scheme = UpstreamScheme.Https;
                insecure = t["insecure_skip_verify"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.True;
                foreach (var key in t.Select(x => x.Key).Where(k => k != "insecure_skip_verify"))
                    ctx.Warn($"'{label}': upstream TLS option '{key}' is not supported and was ignored.");
            }
            foreach (var key in transport.Select(x => x.Key).Where(k => k is not ("protocol" or "tls")))
                ctx.Warn($"'{label}': reverse_proxy transport option '{key}' is not supported and was ignored.");
        }

        var upstreams = new List<Upstream>();
        foreach (var u in ups)
        {
            if (u is not JsonObject uo || uo.Count != 1 || Str(uo["dial"]) is not { } dial) return false;
            if (!TryParseDial(dial, out var host, out var port)) return false;
            upstreams.Add(new Upstream { Scheme = scheme, Host = host, Port = port });
        }

        var policy = LoadBalancingPolicy.RoundRobin;
        if (rp["load_balancing"] is JsonObject lb)
        {
            var name = Str(lb["selection_policy"]?["policy"]);
            if (name is not null)
            {
                var match = Enum.GetValues<LoadBalancingPolicy>().Where(x => CaddyConfigGenerator.PolicyName(x) == name).ToList();
                if (match.Count == 1) policy = match[0];
                else ctx.Warn($"'{label}': load balancing policy '{name}' is not supported; the draft uses round robin.");
            }
            foreach (var key in lb.Select(x => x.Key).Where(k => k is not ("selection_policy" or "try_duration" or "try_interval" or "retries")))
                ctx.Warn($"'{label}': load balancing option '{key}' is not supported and was ignored.");
        }

        var health = new HealthCheck();
        if (rp["health_checks"] is JsonObject hc)
        {
            if (hc["active"] is JsonObject active)
            {
                health.Enabled = true;
                var uri = Str(active["uri"]) ?? Str(active["path"]) ?? "/";
                health.Path = uri.StartsWith('/') ? uri : "/" + uri;
                health.IntervalSeconds = Seconds(active["interval"]) ?? 30;
                health.TimeoutSeconds = Seconds(active["timeout"]) ?? 5;
                health.ExpectStatus = Int(active["expect_status"]) ?? 0;
                foreach (var key in active.Select(x => x.Key).Where(k => k is not ("uri" or "path" or "interval" or "timeout" or "expect_status")))
                    ctx.Warn($"'{label}': active health check option '{key}' is not supported and was ignored.");
            }
            if (hc["passive"] is not null)
                ctx.Warn($"'{label}': passive health checks are not supported and were ignored.");
        }

        var requestHeaders = new List<HeaderOp>();
        string? hostHeader = null;
        var responseHeaders = new List<HeaderOp>();
        if (rp["headers"] is JsonObject hdrs)
        {
            if (hdrs["request"] is JsonObject req)
            {
                foreach (var op in HeaderOps(req))
                {
                    if (op.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) && op.Action == HeaderAction.Set)
                        hostHeader = op.Value == "{http.reverse_proxy.upstream.hostport}" ? "{upstream}" : op.Value;
                    else requestHeaders.Add(op);
                }
                foreach (var key in req.Select(x => x.Key).Where(k => k is not ("set" or "add" or "delete")))
                    ctx.Warn($"'{label}': request header option '{key}' is not supported and was ignored.");
            }
            if (hdrs["response"] is JsonObject resp)
            {
                responseHeaders.AddRange(HeaderOps(resp));
                foreach (var key in resp.Select(x => x.Key).Where(k => k is not ("set" or "add" or "delete" or "deferred")))
                    ctx.Warn($"'{label}': response header option '{key}' is not supported and was ignored.");
            }
        }

        foreach (var key in rp.Select(x => x.Key).Where(k => k is not ("handler" or "upstreams" or "transport" or "load_balancing" or "health_checks" or "headers")))
            ctx.Warn($"'{label}': reverse_proxy option '{key}' is not supported and was ignored.");

        parts = new ProxyParts(upstreams, insecure, ntlm, policy, health, requestHeaders, hostHeader, responseHeaders);
        return true;
    }

    private static bool TryParseDial(string dial, out string host, out int port)
    {
        host = "";
        port = 0;
        var v = dial.Trim();
        if (v.Contains('{')) return false;
        var slash = v.IndexOf('/');
        if (slash >= 0)
        {
            var network = v[..slash];
            if (network is not ("tcp" or "tcp4" or "tcp6")) return false;
            v = v[(slash + 1)..];
        }
        int idx;
        if (v.StartsWith('['))
        {
            var close = v.IndexOf(']');
            if (close < 0 || close + 1 >= v.Length || v[close + 1] != ':') return false;
            host = v[1..close];
            idx = close + 1;
        }
        else
        {
            idx = v.LastIndexOf(':');
            if (idx <= 0) return false;
            host = v[..idx];
        }
        return int.TryParse(v[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && NetUtil.IsValidPort(port) && NetUtil.IsValidHost(host);
    }

    private static List<HeaderOp> HeaderOps(JsonObject ops)
    {
        var list = new List<HeaderOp>();
        if (ops["set"] is JsonObject set)
            foreach (var (name, value) in set)
                list.Add(new HeaderOp { Action = HeaderAction.Set, Name = name, Value = string.Join(", ", Values(value)) });
        if (ops["add"] is JsonObject add)
            foreach (var (name, value) in add)
                foreach (var v in Values(value))
                    list.Add(new HeaderOp { Action = HeaderAction.Add, Name = name, Value = v });
        if (ops["delete"] is JsonArray del)
            foreach (var name in del)
                if (Str(name) is { } n) list.Add(new HeaderOp { Action = HeaderAction.Delete, Name = n });
        return list;

        static IEnumerable<string> Values(JsonNode? n) => n is JsonArray a ? a.Select(x => Str(x) ?? "") : [Str(n) ?? ""];
    }

    // ------------------------------------------------------------------ servers / TLS

    private sealed record TlsInfo(HashSet<string> InternalSubjects, Dictionary<string, (string Cert, string Key)> LoadFiles);

    private static TlsInfo ReadTls(JsonObject? tls, ImportContext ctx)
    {
        var info = new TlsInfo(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, (string, string)>(StringComparer.Ordinal));
        if (tls is null) return info;
        if (tls["automation"]?["policies"] is JsonArray policies)
        {
            foreach (var p in policies.OfType<JsonObject>())
            {
                var issuers = (p["issuers"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                var subjects = (p["subjects"] as JsonArray)?.Select(Str).Where(x => x is not null).Select(x => x!).ToList() ?? [];
                if (issuers.Any(i => Str(i["module"]) == "internal"))
                {
                    foreach (var s in subjects) info.InternalSubjects.Add(s);
                    if (subjects.Count == 0) ctx.Warn("A global 'local_certs'/internal issuer is not imported; choose Internal TLS per host instead.");
                }
                else if (issuers.Any(i => i.Any(k => k.Key is not ("module"))) || p.Any(k => k.Key is not ("subjects" or "issuers")))
                {
                    ctx.Warn("ACME settings from the Caddyfile (e-mail, CA, DNS challenge, ...) are not imported; configure them under Settings > Caddy.");
                    ctx.Unmapped.Add($"(tls automation) {Compact(p)}");
                }
            }
        }
        if (tls["certificates"] is JsonObject certs)
        {
            foreach (var (kind, value) in certs)
            {
                if (kind == "load_files" && value is JsonArray files)
                {
                    foreach (var f in files.OfType<JsonObject>())
                    {
                        var entry = (Str(f["certificate"]) ?? "?", Str(f["key"]) ?? "?");
                        foreach (var tag in (f["tags"] as JsonArray)?.Select(Str).Where(t => t is not null) ?? [])
                            info.LoadFiles[tag!] = entry;
                    }
                }
                else
                {
                    ctx.Warn($"TLS certificate loader '{kind}' is not imported; add those certificates under Certificates.");
                    ctx.Unmapped.Add($"(tls certificates) {kind}: {Compact(value)}");
                }
            }
        }
        foreach (var key in tls.Select(p => p.Key).Where(k => k is not ("automation" or "certificates")))
        {
            ctx.Warn($"TLS app option '{key}' is not imported.");
            ctx.Unmapped.Add($"(tls) {key}: {Compact(tls[key])}");
        }
        return info;
    }

    private static Dictionary<string, string> ConnectionPolicyTags(JsonObject server)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (server["tls_connection_policies"] is not JsonArray policies) return map;
        foreach (var p in policies.OfType<JsonObject>())
        {
            var tag = (p["certificate_selection"]?["any_tag"] as JsonArray)?.Select(Str).FirstOrDefault(t => t is not null);
            if (tag is null) continue;
            foreach (var sni in (p["match"]?["sni"] as JsonArray)?.Select(Str) ?? [])
                if (sni is not null) map[sni] = tag;
        }
        return map;
    }

    private static bool IsHttpServer(JsonObject server, int httpPort)
    {
        if (server["automatic_https"]?["disable"] is JsonValue dv && dv.GetValueKind() == JsonValueKind.True) return true;
        var ports = ListenPorts(server).ToList();
        return ports.Count > 0 && ports.All(p => p == httpPort);
    }

    private static IEnumerable<string> ListenAddresses(JsonObject server) =>
        (server["listen"] as JsonArray)?.Select(Str).Where(x => x is not null).Select(x => x!) ?? [];

    private static IEnumerable<int> ListenPorts(JsonObject server)
    {
        foreach (var a in ListenAddresses(server))
        {
            var idx = a.LastIndexOf(':');
            if (idx >= 0 && int.TryParse(a[(idx + 1)..].Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) yield return port;
        }
    }

    private static bool IsLogged(JsonObject server, List<string> domains)
    {
        if (server["logs"] is not JsonObject logs) return false;
        var names = logs["logger_names"] as JsonObject;
        var skip = (logs["skip_hosts"] as JsonArray)?.Select(Str).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (names is not null && domains.Any(d => names.ContainsKey(d))) return true;
        if (logs["skip_unmapped_hosts"] is JsonValue su && su.GetValueKind() == JsonValueKind.True) return false;
        return names is null && !domains.All(skip.Contains);
    }

    /// <summary>The host names of a site route (a single host matcher), or null when the route is not a plain site.</summary>
    private static List<string>? HostMatch(JsonObject route)
    {
        if (route["match"] is not JsonArray match || match.Count != 1 || match[0] is not JsonObject m || m.Count != 1) return null;
        if (m["host"] is not JsonArray hosts) return null;
        var list = hosts.Select(Str).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim().ToLowerInvariant()).Distinct().ToList();
        return list.Count == 0 ? null : list;
    }

    private static void CheckConflicts(ImportContext ctx, IReadOnlyCollection<SiteHost> existing)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in existing.Where(h => h.Enabled))
            foreach (var d in h.Domains)
                if (NetUtil.NormalizeDomain(d) is { } n) owners.TryAdd(n, h.Domains.FirstOrDefault() ?? h.Id);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in ctx.Drafts)
        {
            foreach (var d in draft.Domains)
            {
                var n = NetUtil.NormalizeDomain(d) ?? d;
                if (owners.TryGetValue(n, out var owner))
                    ctx.Warn($"'{n}' is already served by the existing host '{owner}'; remove that draft or disable the existing host, otherwise the import is rejected.");
                if (!seen.Add(n))
                    ctx.Warn($"'{n}' appears in more than one site of the Caddyfile; keep it in one draft only.");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed class SiteState(SiteHost host, string label)
    {
        public SiteHost Host { get; } = host;
        public string Label { get; } = label;
        public string? Main { get; set; }
        public string? StaticRoot { get; set; }
        public string? FileServerRoot { get; set; }
        public bool Spa { get; set; }
        public List<(ProxyLocation Location, JsonObject Original)> Locations { get; } = new();
        public List<JsonObject> Advanced { get; } = new();

        public void Preserve(JsonObject route, ImportContext ctx, string what)
        {
            if (HandlerNames(route).Contains("authentication"))
                ctx.Warn($"'{Label}': basic_auth was kept as an advanced route so the site stays protected. Consider replacing it with an access list (Security > Access Lists).");
            Advanced.Add((JsonObject)route.DeepClone());
            ctx.Unmapped.Add($"{Label}: {Compact(route)}");
            ctx.Warn($"'{Label}': {what} could not be mapped to host settings and was kept as an advanced route (it runs before the host's main handler); review it before saving.");
        }
    }

    private sealed class ImportContext
    {
        public List<SiteHost> Drafts { get; } = new();
        public List<string> Unmapped { get; } = new();
        private readonly List<string> _warnings = new();
        public void Warn(string w) { if (!_warnings.Contains(w)) _warnings.Add(w); }
        public CaddyfileImportResult Result() => new(Drafts, Unmapped, _warnings);
    }

    /// <summary>Every "handler" name in a route (nested subroutes included).</summary>
    private static IEnumerable<string> HandlerNames(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["handler"]) is { } h) yield return h;
                foreach (var (_, child) in o)
                    foreach (var x in HandlerNames(child)) yield return x;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var x in HandlerNames(child)) yield return x;
                break;
        }
    }

    private static string Compact(JsonNode? n) => n is null ? "null" : CaddyJson.Serialize(n, indented: false);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static int? Int(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>Caddy durations: nanoseconds (number) or Go duration strings ("10s", "1m30s", "500ms").</summary>
    internal static int? Seconds(JsonNode? n)
    {
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var ns))
            return (int)Math.Max(1, Math.Round(ns / 1_000_000_000.0));
        var s = Str(n);
        if (string.IsNullOrWhiteSpace(s)) return null;
        double total = 0;
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (!double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
            var ustart = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            total += s[ustart..i] switch
            {
                "h" => num * 3600,
                "m" => num * 60,
                "s" => num,
                "ms" => num / 1000,
                "us" or "µs" => num / 1_000_000,
                "ns" => num / 1_000_000_000,
                "d" => num * 86400,
                _ => double.NaN,
            };
            if (double.IsNaN(total)) return null;
        }
        return (int)Math.Max(1, Math.Round(total));
    }
}
