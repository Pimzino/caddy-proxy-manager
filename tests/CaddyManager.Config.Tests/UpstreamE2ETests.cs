using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>End-to-end checks of reverse-proxy behaviour towards upstreams with the real Caddy binary.</summary>
public sealed class UpstreamE2ETests
{
    /// <summary>
    /// TLS SNI towards HTTPS upstreams with "skip TLS verification" (research #79; reviewer regression: 421 on
    /// connection reuse for hosts with several names).
    /// Ways it could fail: (1) a host with several names sends the first request's name as SNI and Go's connection
    /// pool (keyed by upstream address, not SNI) reuses that connection for the other names, so an Apache-style
    /// upstream answers 421 Misdirected Request; (2) a single-name host dialled by IP sends no SNI, so IIS SNI
    /// bindings / name-based vhosts pick the wrong site; (3) the SNI does not equal the Host header sent upstream —
    /// also for every name of a host with several names (review round 2: they sent no SNI at all);
    /// (4) a wildcard host sends a literal "*.x" SNI; (5) connections are not reused at all (the test would then not
    /// exercise the reuse case); (6) the control (ONE proxy with server_name "{http.request.host}" for all names of
    /// the host) does not reproduce the 421; (7) a host with an exact name and a wildcard sends the wrong SNI for
    /// names under the wildcard.
    /// </summary>
    [CaddyFact]
    public async Task Upstream_sni_matches_host_without_breaking_connection_reuse()
    {
        var report = E2EArtifacts.Report(nameof(Upstream_sni_matches_host_without_breaking_connection_reuse));
        await using var backend = await RecordingBackend.StartAsync(https: true, strictSni: true);
        using var c = new LiveCaddy();
        SiteHost Https(string[] domains)
        {
            var h = Build.Proxy(domains[0], backend.Port);
            h.Domains = domains.ToList();
            h.Upstreams[0].Scheme = UpstreamScheme.Https;
            h.UpstreamTlsInsecure = true;
            return h;
        }
        c.Add(Https(["a.test", "b.test"]));
        c.Add(Https(["single.test"]));
        c.Add(Https(["*.wild.test"]));
        c.Add(Https(["mixed.test", "*.mixed.test"]));
        await c.StartAsync();
        await c.ApplyAsync();

        async Task<JsonArray> Run(string label, string[] hosts)
        {
            backend.Clear();
            var rows = new JsonArray();
            foreach (var host in hosts)
            {
                var r = await c.HttpAsync(host, "/p");
                var seen = backend.Requests.LastOrDefault();
                rows.Add(new JsonObject { ["host"] = host, ["status"] = r.Status, ["upstreamSni"] = seen?.Sni, ["upstreamHost"] = seen?.Host, ["connection"] = seen?.ConnectionId });
            }
            report[label] = rows;
            return rows;
        }
        var transports = new JsonArray(JsonWalk.Handlers(await c.RunningConfigAsync(), "reverse_proxy").Select(rp => rp["transport"]?.DeepClone()).ToArray());
        report["transports"] = transports;
        try
        {
        var multi = await Run("multiName", ["a.test", "b.test", "a.test", "b.test", "a.test", "b.test"]);
        Assert.All(multi, r => Assert.Equal(200, r!["status"]!.GetValue<int>()));                        // (1)
        Assert.All(multi, r => Assert.Equal(r!["host"]!.GetValue<string>(), r["upstreamSni"]?.GetValue<string>())); // (3)
        Assert.True(multi.GroupBy(r => r!["connection"]!.GetValue<string>()).Any(g => g.Count() > 1), "no connection reuse happened"); // (5)
        var mixed = await Run("exactAndWildcard", ["mixed.test", "x.mixed.test", "mixed.test", "x.mixed.test"]);
        Assert.All(mixed, r => Assert.Equal(200, r!["status"]!.GetValue<int>()));
        Assert.All(mixed.Where(r => r!["host"]!.GetValue<string>() == "mixed.test"), r => Assert.Equal("mixed.test", r!["upstreamSni"]?.GetValue<string>()));
        Assert.All(mixed.Where(r => r!["host"]!.GetValue<string>() != "mixed.test"), r => Assert.NotEqual("mixed.test", r!["upstreamSni"]?.GetValue<string>())); // (7)
        var single = await Run("singleName", ["single.test", "single.test"]);
        Assert.All(single, r => Assert.Equal(200, r!["status"]!.GetValue<int>()));
        Assert.All(single, r => Assert.Equal("single.test", r!["upstreamSni"]?.GetValue<string>()));    // (2)(3)
        var wild = await Run("wildcard", ["x.wild.test", "y.wild.test"]);
        Assert.All(wild, r => Assert.Equal(200, r!["status"]!.GetValue<int>()));
        Assert.All(wild, r => Assert.DoesNotContain("*", r!["upstreamSni"]?.GetValue<string>() ?? ""));   // (4)

        // (6) CONTROL: ONE proxy for both names with the request-time placeholder SNI (the round-1 attempt).
        var control = await c.RunningConfigAsync();
        foreach (var route in JsonWalk.Descendants(control).OfType<JsonObject>().Where(o => o["match"]?.ToJsonString().Contains("\"a.test\"") == true && o["match"]?.ToJsonString().Contains("\"b.test\"") == true).ToList())
        {
            var oneProxy = JsonWalk.Handlers(route, "reverse_proxy").First().DeepClone().AsObject();
            oneProxy["transport"]!["tls"]!["server_name"] = "{http.request.host}";
            route["handle"] = new JsonArray(new JsonObject { ["handler"] = "subroute", ["routes"] = new JsonArray(new JsonObject { ["handle"] = new JsonArray(oneProxy) }) });
        }
        await c.LoadRawAsync(control);
        var broken = await Run("control", ["a.test", "b.test", "a.test", "b.test"]);
        Assert.Contains(broken, r => r!["status"]!.GetValue<int>() == 421);
        }
        finally
        {
            var log = c.ProcessLog();
            report["caddyLogTail"] = log.Length > 3000 ? log[^3000..] : log;
            E2EArtifacts.Write("upstream-sni.json", report);
        }
    }


    private static string A(int port) => $"127.0.0.1:{port}";

    /// <summary>A backend path that makes the upstream drop the connection without answering (Caddy: 502).</summary>
    private static Task<bool> DropOnBoom(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        if (ctx.Request.Path != "/boom") return Task.FromResult(false);
        ctx.Abort();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Passive health checks must never take a host offline. Review round 2 (single upstream: 3 upstream errors made the
    /// whole host answer 503 to every client for 20 s) and round 3 (pool: ONE request on which every backend drops the
    /// connection is retried on each upstream and marked them all down for 30 s) — so the manager generates no passive
    /// checks at all, while pools still fail over (dial errors are retried on the next upstream within try_duration).
    /// Caddy counts every proxy error as a passive failure (reverseproxy.go v2.11.4: countFailure for any error except
    /// a client cancel), so a circuit breaker with nothing to fail over to only turns one client's 502s into 503s for
    /// everyone. https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#passive-health-checks
    /// Ways it could fail:
    /// (1) after three requests on which the backend drops the connection, the next ordinary request to the same
    ///     single-upstream host is refused (503) instead of reaching the backend;
    /// (2) the "boom" requests do not produce proxy errors at all (then the test proves nothing) — they must be 502;
    /// (3) a backend restart (IIS app-pool recycle) leaves the host answering 503 after the backend is back;
    /// (4) a round-robin host with two upstreams, one of them dead, returns errors instead of failing over;
    /// (5) any generated proxy (single upstream or pool) still carries passive health checks;
    /// (6) the control — the round-2 passive block (fail_duration 20s, max_fails 3) put back on the single upstream —
    ///     does not reproduce the 503 lockout;
    /// (7) on a pool whose two upstreams both drop the connection for one path, one such request makes the next
    ///     ordinary request fail (503) — the round-3 blocker;
    /// (8) the control — the round-3 passive block (fail_duration 30s, max_fails 1) put back on the pool — does not
    ///     reproduce that lockout.
    /// </summary>
    [CaddyFact]
    public async Task Upstream_errors_never_lock_out_a_single_upstream_host_and_pools_still_fail_over()
    {
        var report = E2EArtifacts.Report(nameof(Upstream_errors_never_lock_out_a_single_upstream_host_and_pools_still_fail_over));
        var backendPort = Net.FreeTcpPort();
        var backend = await RecordingBackend.StartAsync(https: false, port: backendPort, custom: DropOnBoom);
        await using var poolLive = await RecordingBackend.StartAsync(https: false);
        var poolDead = Net.FreeTcpPort();
        using var c = new LiveCaddy();
        c.Add(Build.Proxy("single.test", backendPort));
        var pool = Build.Proxy("pool.test", poolLive.Port);
        pool.Upstreams.Add(new Upstream { Host = "127.0.0.1", Port = poolDead });
        pool.Upstreams.Reverse();                        // [dead, live]; round robin retries the other on a dial error
        c.Add(pool);
        // Two live upstreams that both drop the connection on /boom (the round-3 blocker).
        var dropA = await RecordingBackend.StartAsync(https: false, custom: DropOnBoom);
        var dropB = await RecordingBackend.StartAsync(https: false, custom: DropOnBoom);
        var drops = Build.Proxy("drops.test", dropA.Port);
        drops.Upstreams.Add(new Upstream { Host = "127.0.0.1", Port = dropB.Port });
        c.Add(drops);
        await c.StartAsync();
        await c.ApplyAsync();
        try
        {
            var cfg = await c.RunningConfigAsync();
            var proxies = JsonWalk.Handlers(cfg, "reverse_proxy").ToList();
            report["generatedHealthChecks"] = new JsonArray(proxies.Select(rp => (JsonNode?)rp["health_checks"]?.DeepClone()).ToArray());
            Assert.All(proxies, rp => Assert.Null(rp["health_checks"]?["passive"])); // (5)

            async Task<JsonArray> Sequence(string label)
            {
                var rows = new JsonArray();
                foreach (var t in new[] { "/", "/boom", "/boom", "/boom", "/", "/" })
                    rows.Add(new JsonObject { ["target"] = t, ["status"] = (await c.HttpAsync("single.test", t)).Status });
                report[label] = rows;
                return rows;
            }
            var seq = await Sequence("singleUpstream");
            Assert.All(seq.Where(r => r!["target"]!.GetValue<string>() == "/boom"), r => Assert.Equal(502, r!["status"]!.GetValue<int>())); // (2)
            Assert.All(seq.Where(r => r!["target"]!.GetValue<string>() == "/"), r => Assert.Equal(200, r!["status"]!.GetValue<int>()));    // (1)

            // (3) backend restart: down → requests fail → back up → the very next request is served.
            await backend.DisposeAsync();
            var whileDown = new JsonArray();
            for (var i = 0; i < 4; i++) whileDown.Add((await c.HttpAsync("single.test", "/")).Status);
            backend = await RecordingBackend.StartAsync(https: false, port: backendPort, custom: DropOnBoom);
            var afterRestart = (await c.HttpAsync("single.test", "/")).Status;
            report["restart"] = new JsonObject { ["whileDown"] = whileDown, ["firstRequestAfterRestart"] = afterRestart };
            Assert.All(whileDown, s => Assert.Equal(502, s!.GetValue<int>()));
            Assert.Equal(200, afterRestart);

            // (4) failover pool: every request succeeds although one member is dead.
            var poolStatuses = new JsonArray();
            for (var i = 0; i < 6; i++) poolStatuses.Add((await c.HttpAsync("pool.test", "/")).Status);
            report["poolStatuses"] = poolStatuses;
            Assert.All(poolStatuses, s => Assert.Equal(200, s!.GetValue<int>()));

            // (7) one request that makes both upstreams drop the connection must not take the pool host offline.
            async Task<JsonArray> PoolSequence(string label)
            {
                var rows = new JsonArray();
                foreach (var t in new[] { "/", "/boom", "/", "/" })
                    rows.Add(new JsonObject { ["target"] = t, ["status"] = (await c.HttpAsync("drops.test", t)).Status });
                report[label] = rows;
                return rows;
            }
            var poolSeq = await PoolSequence("poolBoom");
            Assert.Equal(502, poolSeq[1]!["status"]!.GetValue<int>());
            Assert.Equal(200, poolSeq[2]!["status"]!.GetValue<int>());
            Assert.Equal(200, poolSeq[3]!["status"]!.GetValue<int>());

            // (6) CONTROL: the round-2 passive block on the single upstream reproduces the lockout.
            var control = await c.RunningConfigAsync();
            foreach (var rp in JsonWalk.Handlers(control, "reverse_proxy").Where(rp => rp["upstreams"]!.AsArray().Count == 1))
                rp["health_checks"] = new JsonObject { ["passive"] = new JsonObject { ["fail_duration"] = "20s", ["max_fails"] = 3 } };
            await c.LoadRawAsync(control);
            var broken = await Sequence("control");
            Assert.Equal(503, broken[4]!["status"]!.GetValue<int>());
            report["controlReproducedLockout"] = true;

            // (8) CONTROL: the round-3 passive block on the pool reproduces the one-request lockout.
            var control3 = await c.RunningConfigAsync();
            foreach (var rp in JsonWalk.Handlers(control3, "reverse_proxy").Where(rp => rp["upstreams"]!.AsArray().Count == 2))
                rp["health_checks"] = new JsonObject { ["passive"] = new JsonObject { ["fail_duration"] = "30s", ["max_fails"] = 1 } };
            await c.LoadRawAsync(control3);
            var broken3 = await PoolSequence("controlPool");
            Assert.Equal(503, broken3[2]!["status"]!.GetValue<int>());
            report["controlReproducedPoolLockout"] = true;
        }
        finally
        {
            await backend.DisposeAsync();
            await dropA.DisposeAsync();
            await dropB.DisposeAsync();
            E2EArtifacts.Write("upstream-single-no-lockout.json", report);
        }
    }

    /// <summary>
    /// Upstream health as reported to the dashboard and the upstream-down alert (research #15/#84, review round 2).
    /// Caddy only knows an upstream is down when a health check measures it: Upstream.Healthy() is true for an upstream
    /// without active or passive checks, and caddy_reverse_proxy_upstreams_healthy just reports that value
    /// (hosts.go, metrics.go v2.11.4). So an unchecked upstream must be reported as "not monitored", never "healthy".
    /// Ways it could fail:
    /// (1) a dead single upstream WITH an active check is not reported unhealthy (the alert never fires);
    /// (2) a dead single upstream WITHOUT any check is reported healthy (the dashboard's "n/n healthy" and the alert
    ///     would claim a health nobody measured) — it must be missing from GetUpstreamsAsync (what the dashboard and
    ///     the alert read) and flagged not monitored in the full status list;
    /// (3) a working, actively checked upstream is reported unhealthy;
    /// (4) the dead member of a failover pool with an active check is not reported unhealthy;
    /// (5) recovery of the actively checked upstream is never reported;
    /// (6) the control: Caddy's own metric reports the dead unchecked upstream as healthy (1), so reading it directly
    ///     (the previous behaviour for single upstreams) would have said "healthy".
    /// </summary>
    [CaddyFact]
    public async Task Upstream_health_is_reported_only_where_a_health_check_measures_it()
    {
        var report = E2EArtifacts.Report(nameof(Upstream_health_is_reported_only_where_a_health_check_measures_it));
        await using var live = await RecordingBackend.StartAsync(https: false);
        var activeDead = Net.FreeTcpPort();
        var uncheckedDead = Net.FreeTcpPort();
        var poolDead = Net.FreeTcpPort();
        using var c = new LiveCaddy();
        var check = new HealthCheck { Enabled = true, Path = "/", IntervalSeconds = 1, TimeoutSeconds = 1 };
        var ad = Build.Proxy("active-dead.test", activeDead);
        ad.HealthCheck = check;
        c.Add(ad);
        var al = Build.Proxy("active-live.test", live.Port);
        al.HealthCheck = check;
        c.Add(al);
        c.Add(Build.Proxy("unchecked-dead.test", uncheckedDead));
        var pool = Build.Proxy("pool.test", live.Port);
        pool.Upstreams.Add(new Upstream { Host = "127.0.0.1", Port = poolDead });
        pool.HealthCheck = check; // pools are monitored by the active check (the manager generates no passive checks)
        c.Add(pool);
        await c.StartAsync();
        await c.ApplyAsync();

        var traffic = new JsonArray();
        foreach (var h in new[] { "active-dead.test", "unchecked-dead.test", "pool.test", "pool.test", "pool.test", "active-live.test" })
            traffic.Add(new JsonObject { ["host"] = h, ["status"] = (await c.HttpAsync(h, "/")).Status });
        report["traffic"] = traffic;

        List<Core.Contracts.UpstreamHealth> ups = [];
        List<Admin.UpstreamStatus> all = [];
        var detected = await Wait.For(async () =>
        {
            ups = await c.Admin.GetUpstreamsAsync();
            return ups.Any(u => u.Address == A(activeDead) && !u.Healthy) && ups.Any(u => u.Address == A(poolDead) && !u.Healthy)
                && ups.Any(u => u.Address == A(live.Port) && u.Healthy);
        }, TimeSpan.FromSeconds(30));
        all = await c.Admin.GetUpstreamStatusAsync();
        report["reported"] = new JsonArray(ups.Select(u => (JsonNode)new JsonObject { ["address"] = u.Address, ["healthy"] = u.Healthy, ["fails"] = u.Fails }).ToArray());
        report["statusList"] = new JsonArray(all.Select(u => (JsonNode)new JsonObject { ["address"] = u.Address, ["healthy"] = u.Healthy, ["monitored"] = u.Monitored, ["fails"] = u.Fails }).ToArray());
        try
        {
            Assert.True(detected, "health not reported as expected: " + report["reported"]!.ToJsonString()); // (1)(3)(4)
            Assert.DoesNotContain(ups, u => u.Address == A(uncheckedDead));                                  // (2)
            var notChecked = all.Single(u => u.Address == A(uncheckedDead));
            Assert.False(notChecked.Monitored);
            Assert.True(all.Single(u => u.Address == A(activeDead)).Monitored);

            // (6) CONTROL: the raw metric claims the unchecked dead upstream is healthy.
            using (var http = new HttpClient())
            {
                var metrics = await http.GetStringAsync(c.Admin.BaseUrl + "/metrics");
                var line = metrics.Split('\n').FirstOrDefault(l => l.StartsWith("caddy_reverse_proxy_upstreams_healthy", StringComparison.Ordinal) && l.Contains(A(uncheckedDead)));
                report["controlMetricLine"] = line;
                Assert.NotNull(line);
                Assert.EndsWith(" 1", line!.TrimEnd());
            }

            // (5) the actively checked upstream comes back.
            await using var revived = await RecordingBackend.StartAsync(https: false, port: activeDead);
            var started = DateTime.UtcNow;
            var recovered = await Wait.For(async () => (await c.Admin.GetUpstreamsAsync()).Single(u => u.Address == A(activeDead)).Healthy, TimeSpan.FromSeconds(30));
            report["activeRecoverySeconds"] = (DateTime.UtcNow - started).TotalSeconds;
            Assert.True(recovered, "recovered upstream still reported unhealthy");
            Assert.Equal(200, (await c.HttpAsync("active-dead.test", "/")).Status);
        }
        finally
        {
            var cfg = await c.RunningConfigAsync();
            report["healthChecks"] = new JsonArray(JsonWalk.Handlers(cfg, "reverse_proxy").Select(rp => (JsonNode)new JsonObject
            {
                ["upstreams"] = rp["upstreams"]!.DeepClone(), ["health_checks"] = rp["health_checks"]?.DeepClone(),
            }).ToArray());
            E2EArtifacts.Write("upstream-health.json", report);
        }
    }

    /// <summary>
    /// Active health checks send the host's own name as Host and accept status classes (research #45).
    /// Caddy's active checker sends the upstream address as Host and applies none of the proxy's header operations
    /// (healthchecks.go v2.11.4), so a backend that routes by Host (IIS site bindings, appliances that redirect IP
    /// access) failed every check and the host answered 503.
    /// Ways it could fail: (1) the check request carries the upstream address instead of the site name, the backend
    /// answers 404 and the host goes 503; (2) a health path that redirects (302) fails although "3" (any 3xx) is
    /// expected; (3) the checked upstreams are not reported healthy; (4) proxied requests do not reach the backend;
    /// (5) the control (no Host header, no status class) does not reproduce the 503 / unhealthy report.
    /// </summary>
    [CaddyFact]
    public async Task Active_health_checks_send_the_site_name_as_host_and_accept_status_classes()
    {
        var report = E2EArtifacts.Report(nameof(Active_health_checks_send_the_site_name_as_host_and_accept_status_classes));
        var checkHosts = new List<string>();
        await using var bound = await RecordingBackend.StartAsync(https: false, custom: ctx =>
        {
            if (ctx.Request.Path != "/hc") return Task.FromResult(false);
            lock (checkHosts) checkHosts.Add(ctx.Request.Host.Value ?? "");
            // An IIS-style binding: only the site's own name is served.
            ctx.Response.StatusCode = string.Equals(ctx.Request.Host.Host, "hc.test", StringComparison.OrdinalIgnoreCase) ? 200 : 404;
            return Task.FromResult(true);
        });
        await using var redirecting = await RecordingBackend.StartAsync(https: false, custom: ctx =>
        {
            if (ctx.Request.Path != "/login") return Task.FromResult(false);
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = "/login/";
            return Task.FromResult(true);
        });
        using var c = new LiveCaddy();
        var hc = Build.Proxy("hc.test", bound.Port);
        hc.HealthCheck = new HealthCheck { Enabled = true, Path = "/hc", IntervalSeconds = 1, TimeoutSeconds = 1 };
        c.Add(hc);
        var hcr = Build.Proxy("hcr.test", redirecting.Port);
        hcr.HealthCheck = new HealthCheck { Enabled = true, Path = "/login", IntervalSeconds = 1, TimeoutSeconds = 1, ExpectStatus = 3 };
        c.Add(hcr);
        await c.StartAsync();
        await c.ApplyAsync();

        async Task<bool> BothHealthy(bool expected) => await Wait.For(async () =>
        {
            var ups = await c.Admin.GetUpstreamsAsync();
            return ups.Count(u => (u.Address == A(bound.Port) || u.Address == A(redirecting.Port)) && u.Healthy == expected) == 2;
        }, TimeSpan.FromSeconds(25));

        await Task.Delay(2500); // at least two check rounds
        var healthy = await BothHealthy(true);
        var proxied = await c.HttpAsync("hc.test", "/page");
        var proxiedR = await c.HttpAsync("hcr.test", "/page");
        lock (checkHosts) report["checkHostHeaders"] = new JsonArray(checkHosts.Distinct().Select(h => (JsonNode)h).ToArray());
        report["fixed"] = new JsonObject { ["bothHealthy"] = healthy, ["hcStatus"] = proxied.Status, ["hcrStatus"] = proxiedR.Status };
        Assert.True(healthy, "checked upstreams not reported healthy");                         // (1)(2)(3)
        Assert.Equal(200, proxied.Status);                                                      // (4)
        Assert.Equal(200, proxiedR.Status);
        lock (checkHosts) Assert.All(checkHosts, h => Assert.Equal("hc.test", h.Split(':')[0])); // (1)

        // (5) CONTROL: Caddy's defaults (upstream address as Host, 2xx only).
        var control = await c.RunningConfigAsync();
        foreach (var rp in JsonWalk.Handlers(control, "reverse_proxy"))
            if (rp["health_checks"]?["active"] is JsonObject active) { active.Remove("headers"); active.Remove("expect_status"); }
        await c.LoadRawAsync(control);
        var unhealthy = await BothHealthy(false);
        var blocked = await c.HttpAsync("hc.test", "/page");
        report["control"] = new JsonObject { ["bothUnhealthy"] = unhealthy, ["hcStatus"] = blocked.Status };
        Assert.True(unhealthy, "control: upstreams stayed healthy without the Host header / status class");
        Assert.Equal(503, blocked.Status);

        E2EArtifacts.Write("upstream-active-health-host.json", report);
    }

    /// <summary>
    /// "IP hash" load balancing behind trusted proxies (research #44). ip_hash hashes the immediate peer, so behind a
    /// load balancer every client landed on one upstream; client_ip_hash hashes the client IP Caddy derived from the
    /// trusted proxy's X-Forwarded-For. https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#load-balancing
    /// Ways it could fail: (1) all clients behind the proxy land on one upstream; (2) a client does not stick to its
    /// upstream across requests; (3) requests fail; (4) the control (ip_hash) does not put every client on one upstream.
    /// </summary>
    [CaddyFact]
    public async Task Ip_hash_spreads_clients_behind_a_trusted_proxy_and_keeps_each_on_one_upstream()
    {
        var report = E2EArtifacts.Report(nameof(Ip_hash_spreads_clients_behind_a_trusted_proxy_and_keeps_each_on_one_upstream));
        await using var b1 = await RecordingBackend.StartAsync(https: false);
        await using var b2 = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy(s => s.TrustedProxies = ["127.0.0.1/32"]);
        var h = Build.Proxy("lb.test", b1.Port);
        h.Upstreams.Add(new Upstream { Host = "127.0.0.1", Port = b2.Port });
        h.LoadBalancing = LoadBalancingPolicy.IpHash;
        c.Add(h);
        await c.StartAsync();
        await c.ApplyAsync();

        async Task<Dictionary<string, HashSet<int>>> Run(string label)
        {
            b1.Clear();
            b2.Clear();
            var placement = new Dictionary<string, HashSet<int>>();
            for (var round = 0; round < 2; round++)
                for (var i = 1; i <= 16; i++)
                {
                    var ip = $"10.9.0.{i}";
                    var before = b1.Requests.Count;
                    var r = await c.HttpAsync("lb.test", "/", ("X-Forwarded-For", ip));
                    Assert.Equal(200, r.Status); // (3)
                    var which = b1.Requests.Count > before ? 1 : 2;
                    if (!placement.TryGetValue(ip, out var set)) placement[ip] = set = new();
                    set.Add(which);
                }
            report[label] = new JsonObject
            {
                ["upstream1Requests"] = b1.Requests.Count,
                ["upstream2Requests"] = b2.Requests.Count,
                ["placement"] = new JsonObject(placement.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.Select(v => (JsonNode)v).ToArray())))),
            };
            return placement;
        }
        var spread = await Run("clientIpHash");
        Assert.True(b1.Requests.Count > 0 && b2.Requests.Count > 0, "all clients landed on one upstream"); // (1)
        Assert.All(spread.Values, s => Assert.Single(s));                                                 // (2)

        // (4) CONTROL: ip_hash (the immediate peer, 127.0.0.1 for everyone).
        var control = await c.RunningConfigAsync();
        foreach (var rp in JsonWalk.Handlers(control, "reverse_proxy"))
            rp["load_balancing"]!["selection_policy"]!["policy"] = "ip_hash";
        await c.LoadRawAsync(control);
        await Run("controlIpHash");
        Assert.True(b1.Requests.Count == 0 || b2.Requests.Count == 0, "control: ip_hash spread the clients");

        E2EArtifacts.Write("upstream-client-ip-hash.json", report);
    }
}
