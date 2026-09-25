using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end through the whole product: one manager composed like Program.cs (every module, the real Telemetry module with
/// short timers) drives a real Caddy v2.11.4. Two proxy hosts are created through the API, loopback is made a trusted proxy
/// through PUT /api/settings/caddy, and a known traffic mix goes through Caddy: distinct client IPs in X-Forwarded-For,
/// 404s and 500s from the upstream, uploads and pages of known body sizes. The traffic API (hour and day, all hosts and
/// per host) must then report exactly what was sent, the samples API a request rate during the load and Caddy's memory,
/// and the servers list Caddy's version. Writes e2e-artifacts/full-stack-traffic.json.
/// </summary>
public sealed class FullStackTrafficE2ETests(ITestOutputHelper output)
{
    private const string Shop = "shop.fullstack.test";
    private const string ApiHost = "api.fullstack.test";
    private const int RequestsPerRound = 40;
    private static readonly TimeSpan RoundInterval = TimeSpan.FromMilliseconds(100);
    private const int MinRounds = 10;
    /// <summary>30 s of load at most.</summary>
    private const int MaxRounds = 300;
    private const int UploadResponseBytes = 42;

    /// <summary>Eleven IPv4 clients and one IPv6 client, as sent in X-Forwarded-For.</summary>
    private static readonly string[] Clients = Enumerable.Range(1, 11).Select(k => $"10.20.30.{k}").Append("2001:db8::c").ToArray();
    /// <summary>Client k sends k + 1 of every 78 requests: the busiest clients differ in request count.</summary>
    private static readonly int[] ClientCycle = Enumerable.Range(0, Clients.Length).SelectMany(k => Enumerable.Repeat(k, k + 1)).ToArray();

    private sealed record Planned(string Host, HttpMethod Method, string Path, int BodyBytes, string Client, int Status, int ResponseBytes);

    /// <summary>Request i: two thirds to the shop host; 10 % 404, 10 % 500, 20 % uploads, the rest pages.</summary>
    private static Planned Plan(int i)
    {
        var host = i % 3 == 2 ? ApiHost : Shop;
        var client = Clients[ClientCycle[i % ClientCycle.Length]];
        return (i % 10) switch
        {
            0 => new Planned(host, HttpMethod.Get, "/missing", 0, client, 404, "not found".Length),
            1 => new Planned(host, HttpMethod.Get, "/fail", 0, client, 500, "boom".Length),
            2 or 3 => new Planned(host, HttpMethod.Post, "/upload", i % 13 * 97 + 1, client, 200, UploadResponseBytes),
            _ => new Planned(host, HttpMethod.Get, "/page/" + i, 0, client, 200, PageBody(host).Length),
        };
    }

    private static string PageBody(string host) => "page " + host;

    [Fact]
    public async Task TrafficThroughCaddyIsReportedExactlyByTheServersApi()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        var report = E2EArtifacts.Report(nameof(FullStackTrafficE2ETests) + "." + nameof(TrafficThroughCaddyIsReportedExactlyByTheServersApi));
        var clock = Stopwatch.StartNew();
        await using var upstream = await Upstream.StartAsync();
        Manager? manager = null;
        try
        {
            manager = await Manager.CreateAsync("fullstack");
            var m = manager;
            Assert.Equal(typeof(CaddyManager.Telemetry.ServerTelemetry).FullName, m.TelemetryImplementation);
            report["manager"] = ClusterE2ETests.Describe(m);
            report["telemetry"] = m.TelemetryImplementation;
            report["upstreamPort"] = upstream.Port;

            // ---- configuration through the API; every change is applied to Caddy before the call returns
            var setup = new JsonObject();
            foreach (var domain in new[] { Shop, ApiHost })
            {
                var created = await m.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { domain }, tls = "none", compression = false,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts " + domain);
                setup[domain] = AssertApplied(created, "POST /api/hosts " + domain);
            }
            // The test client connects from 127.0.0.1 and names the client in X-Forwarded-For: trusting loopback makes Caddy
            // log that address as client_ip (trusted_proxies_strict; client_ip_headers defaults to X-Forwarded-For,
            // https://caddyserver.com/docs/json/apps/http/servers/client_ip_headers/).
            var settings = await m.Api.PutAsJsonAsync("api/settings/caddy", new { trustedProxies = new[] { "127.0.0.1/32" } })
                .OkJsonAsync("PUT /api/settings/caddy trustedProxies");
            setup["trustedProxies"] = AssertApplied(settings, "PUT /api/settings/caddy");
            Assert.Equal(new[] { "127.0.0.1/32" }, settings.GetProperty("item").GetProperty("trustedProxies").EnumerateArray().Select(p => p.GetString()));
            report["setup"] = setup;

            // ---- load: a steady RequestsPerRound every RoundInterval until the sampler has reported a request rate while
            // the load runs (its rate window ends IngestInterval + 1 s in the past, so that takes about two seconds)
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 8 })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{m.HttpPort}"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            var sent = new List<Planned>();
            var loadStart = DateTime.UtcNow;
            var loadWatch = Stopwatch.StartNew();
            JsonElement? rateSample = null;
            var rounds = 0;
            using var pace = new PeriodicTimer(RoundInterval);
            while (rounds < MinRounds || rateSample is null)
            {
                if (rounds >= MaxRounds)
                {
                    // The request rate comes from the stats-log ingester: show whether it read anything at all.
                    var traffic = await m.Api.GetFromJsonAsync<JsonElement>("api/servers/local/traffic?range=hour");
                    var statsLog = Directory.GetFiles(m.Paths.StatsLogDir).Sum(f => new FileInfo(f).Length);
                    Assert.Fail($"no sample with requestsPerSecond > 0 after {rounds} rounds ({sent.Count} requests, {loadWatch.ElapsedMilliseconds} ms); " +
                                $"stats log {statsLog} bytes, traffic totals {traffic.GetProperty("totals")}, " +
                                $"lastIngestAt {(traffic.TryGetProperty("lastIngestAt", out var li) ? li.ToString() : "never")}\n{m.LogTail()}");
                }
                await pace.WaitForNextTickAsync();
                var batch = Enumerable.Range(sent.Count, RequestsPerRound).Select(Plan).ToList();
                await SendAsync(http, batch);
                sent.AddRange(batch);
                rounds++;
                if (rateSample is null)
                {
                    var samples = await m.Api.GetFromJsonAsync<JsonElement>("api/servers/local/samples");
                    foreach (var s in samples.EnumerateArray())
                        if (s.GetProperty("at").GetDateTime().ToUniversalTime() >= loadStart && s.GetProperty("requestsPerSecond").GetDouble() > 0)
                            rateSample = s.Clone();
                }
            }
            var loadMs = loadWatch.ElapsedMilliseconds;
            var loadEnd = DateTime.UtcNow;
            report["load"] = new JsonObject
            {
                ["rounds"] = rounds, ["requests"] = sent.Count, ["ms"] = loadMs,
                ["from"] = loadStart.ToString("O"), ["to"] = loadEnd.ToString("O"),
            };

            // ---- traffic API: exact numbers for hour and day, all hosts and each host
            var expected = Expected(sent, null);
            var perHost = new Dictionary<string, Expectation> { [Shop] = Expected(sent, Shop), [ApiHost] = Expected(sent, ApiHost) };
            report["expected"] = new JsonObject
            {
                ["all"] = expected.Json(), [Shop] = perHost[Shop].Json(), [ApiHost] = perHost[ApiHost].Json(),
            };
            var queries = new List<(string Name, string Url, Expectation Expect, string? Host)>();
            foreach (var range in new[] { "hour", "day" })
            {
                queries.Add(($"{range}", $"api/servers/local/traffic?range={range}", expected, null));
                foreach (var host in new[] { Shop, ApiHost })
                    queries.Add(($"{range} {host}", $"api/servers/local/traffic?range={range}&host={host.ToUpperInvariant()}", perHost[host], host));
            }
            var mismatches = new List<string>();
            var ingestWatch = Stopwatch.StartNew();
            var reports = await Wait.ForValueAsync(async () =>
            {
                mismatches = new List<string>();
                var got = new Dictionary<string, JsonElement>();
                foreach (var q in queries)
                {
                    var r = await m.Api.GetFromJsonAsync<JsonElement>(q.Url);
                    got[q.Name] = r;
                    mismatches.AddRange(Compare(r, q.Expect, q.Host, perHost).Select(x => $"{q.Name}: {x}"));
                }
                return (mismatches.Count == 0, got);
            }, TimeSpan.FromSeconds(60), () => "traffic reports equal to the traffic sent:\n" + string.Join("\n", mismatches.Take(30)) + "\n" + m.LogTail());
            report["ingestedWithinMs"] = ingestWatch.ElapsedMilliseconds;
            report["actual"] = new JsonObject(reports.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Summary(kv.Value))));

            // ---- samples API: a request rate while the load ran, Caddy's memory
            var samplesAfter = await m.Api.GetFromJsonAsync<JsonElement>("api/servers/local/samples");
            var duringLoad = samplesAfter.EnumerateArray()
                .Where(s => s.GetProperty("at").GetDateTime().ToUniversalTime() is var at && at >= loadStart && at <= loadEnd.AddSeconds(3))
                .ToList();
            Assert.Contains(duringLoad, s => s.GetProperty("requestsPerSecond").GetDouble() > 0);
            Assert.All(duringLoad, s => Assert.True(CaddyMemory(s) > 0, "caddyMemoryBytes is set while Caddy runs"));
            report["samples"] = new JsonObject
            {
                ["firstWithRequestRate"] = SampleJson(rateSample!.Value),
                ["duringLoad"] = new JsonArray(duringLoad.Select(s => (JsonNode)SampleJson(s)).ToArray()),
            };

            // ---- servers list: the local server with Caddy's version
            var servers = await m.Api.GetFromJsonAsync<JsonElement>("api/servers");
            var local = servers.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "local");
            Assert.True(local.GetProperty("isLocal").GetBoolean());
            var info = local.GetProperty("info");
            var caddyVersion = info.GetProperty("caddyVersion").GetString();
            Assert.False(string.IsNullOrEmpty(caddyVersion), "info.caddyVersion");
            // `caddy version` prints "v2.11.4 h1:…": the reported version is the binary's.
            Assert.StartsWith(caddyVersion + " ", report["caddyVersion"]!.GetValue<string>() + " ");
            Assert.Equal("running", info.GetProperty("caddyState").GetString());
            Assert.Equal(m.DataDir, info.GetProperty("dataDir").GetString());
            report["localServer"] = new JsonObject
            {
                ["name"] = local.GetProperty("name").GetString(), ["caddyVersion"] = caddyVersion, ["caddyState"] = info.GetProperty("caddyState").GetString(),
                ["hostname"] = info.GetProperty("hostname").GetString(), ["latestSampleAt"] = local.GetProperty("latest").GetProperty("at").GetString(),
            };
            report["result"] = "passed";
        }
        catch (Exception ex)
        {
            report["result"] = "failed";
            report["error"] = ex.ToString();
            if (manager is not null) report["log"] = manager.LogTail(150);
            throw;
        }
        finally
        {
            report["totalMs"] = clock.ElapsedMilliseconds;
            output.WriteLine("Artifact: " + E2EArtifacts.Write("full-stack-traffic.json", report));
            if (manager is not null) await manager.DisposeAsync();
        }
    }

    private static JsonObject AssertApplied(JsonElement response, string what)
    {
        var apply = response.GetProperty("apply");
        Assert.True(apply.GetProperty("success").GetBoolean(), $"{what}: not applied {apply}");
        Assert.False(apply.TryGetProperty("writtenOnly", out var w) && w.GetBoolean(), $"{what}: only written, Caddy not running");
        return new JsonObject { ["revision"] = apply.TryGetProperty("revisionId", out var r) ? r.GetString() : null };
    }

    private static long CaddyMemory(JsonElement sample) =>
        sample.TryGetProperty("caddyMemoryBytes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0;

    private static JsonObject SampleJson(JsonElement s) => new()
    {
        ["at"] = s.GetProperty("at").GetString(),
        ["requestsPerSecond"] = s.GetProperty("requestsPerSecond").GetDouble(),
        ["caddyMemoryBytes"] = CaddyMemory(s),
        ["activeConnections"] = s.TryGetProperty("activeConnections", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
    };

    private static async Task SendAsync(HttpClient http, List<Planned> batch)
    {
        await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (p, ct) =>
        {
            using var req = new HttpRequestMessage(p.Method, p.Path);
            req.Headers.Host = p.Host;
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", p.Client);
            if (p.BodyBytes > 0) req.Content = new ByteArrayContent(new byte[p.BodyBytes]);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            if ((int)resp.StatusCode != p.Status || body.Length != p.ResponseBytes)
                throw new InvalidOperationException($"{p.Method} {p.Host}{p.Path}: expected {p.Status} with {p.ResponseBytes} bytes, got {(int)resp.StatusCode} with {body.Length}");
        });
    }

    // ------------------------------------------------------------------ expectations

    private sealed class ClientExpectation
    {
        public long Requests, BytesOut;
    }

    private sealed record Expectation(long Requests, long BytesIn, long BytesOut, long S2xx, long S4xx, long S5xx,
        Dictionary<int, long> Codes, Dictionary<string, ClientExpectation> Clients)
    {
        public JsonObject Json() => new()
        {
            ["requests"] = Requests, ["bytesIn"] = BytesIn, ["bytesOut"] = BytesOut, ["uniqueClients"] = Clients.Count,
            ["2xx"] = S2xx, ["4xx"] = S4xx, ["5xx"] = S5xx,
            ["statusCodes"] = new JsonObject(Codes.OrderBy(c => c.Key).Select(c => KeyValuePair.Create(c.Key.ToString(), (JsonNode?)c.Value))),
            ["clients"] = new JsonObject(Clients.OrderByDescending(c => c.Value.Requests).Select(c =>
                KeyValuePair.Create(c.Key, (JsonNode?)new JsonObject { ["requests"] = c.Value.Requests, ["bytesOut"] = c.Value.BytesOut }))),
        };
    }

    private static Expectation Expected(List<Planned> sent, string? host)
    {
        var list = sent.Where(p => host is null || p.Host == host).ToList();
        var clients = new Dictionary<string, ClientExpectation>(StringComparer.Ordinal);
        foreach (var p in list)
        {
            if (!clients.TryGetValue(p.Client, out var c)) clients[p.Client] = c = new ClientExpectation();
            c.Requests++;
            c.BytesOut += p.ResponseBytes;
        }
        return new Expectation(list.Count, list.Sum(p => (long)p.BodyBytes), list.Sum(p => (long)p.ResponseBytes),
            list.Count(p => p.Status / 100 == 2), list.Count(p => p.Status / 100 == 4), list.Count(p => p.Status / 100 == 5),
            list.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => (long)g.Count()), clients);
    }

    /// <summary>Every difference between a traffic report and the expectation (empty when they agree exactly).</summary>
    private static List<string> Compare(JsonElement r, Expectation e, string? host, Dictionary<string, Expectation> perHost)
    {
        var d = new List<string>();
        void Eq(string what, long expected, long actual) { if (expected != actual) d.Add($"{what}: expected {expected}, got {actual}"); }
        long L(JsonElement o, string name) => o.GetProperty(name).GetInt64();

        if (!r.GetProperty("enabled").GetBoolean()) d.Add("enabled: false");
        if (host is not null && r.GetProperty("host").GetString() != host) d.Add($"host: expected {host}, got {r.GetProperty("host")}");
        var t = r.GetProperty("totals");
        Eq("requests", e.Requests, L(t, "requests"));
        Eq("bytesIn", e.BytesIn, L(t, "bytesIn"));
        Eq("bytesOut", e.BytesOut, L(t, "bytesOut"));
        Eq("uniqueClients", e.Clients.Count, L(t, "uniqueClients"));
        Eq("status2xx", e.S2xx, L(t, "status2xx"));
        Eq("status3xx", 0, L(t, "status3xx"));
        Eq("status4xx", e.S4xx, L(t, "status4xx"));
        Eq("status5xx", e.S5xx, L(t, "status5xx"));
        Eq("statusOther", 0, L(t, "statusOther"));
        Eq("series sum", e.Requests, r.GetProperty("series").EnumerateArray().Sum(p => L(p, "requests")));

        var codes = r.GetProperty("statusCodes").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetInt32(), c => L(c, "count"));
        if (!codes.OrderBy(c => c.Key).SequenceEqual(e.Codes.OrderBy(c => c.Key)))
            d.Add($"statusCodes: expected {string.Join(",", e.Codes.OrderBy(c => c.Key))}, got {string.Join(",", codes.OrderBy(c => c.Key))}");

        // Per-host split: both hosts without a filter, only the filtered host with one.
        var rows = r.GetProperty("topHosts").EnumerateArray().ToDictionary(h => h.GetProperty("host").GetString()!, h => h);
        var hosts = host is null ? perHost.Keys.ToList() : [host];
        if (!rows.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(hosts.OrderBy(k => k, StringComparer.Ordinal)))
            d.Add($"topHosts: expected {string.Join(",", hosts)}, got {string.Join(",", rows.Keys)}");
        foreach (var h in hosts)
        {
            if (!rows.TryGetValue(h, out var row)) continue;
            var he = perHost[h];
            Eq($"topHosts[{h}].requests", he.Requests, L(row, "requests"));
            Eq($"topHosts[{h}].bytesIn", he.BytesIn, L(row, "bytesIn"));
            Eq($"topHosts[{h}].bytesOut", he.BytesOut, L(row, "bytesOut"));
            Eq($"topHosts[{h}].uniqueClients", he.Clients.Count, L(row, "uniqueClients"));
            Eq($"topHosts[{h}].status4xx", he.S4xx, L(row, "status4xx"));
            Eq($"topHosts[{h}].status5xx", he.S5xx, L(row, "status5xx"));
        }

        // Top clients: 12 clients stay far below the summary's 200 counters, so the heavy-hitter counts are exact.
        var clients = r.GetProperty("topClients").EnumerateArray().ToList();
        var byIp = clients.ToDictionary(c => c.GetProperty("ip").GetString()!, c => c);
        if (!byIp.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(e.Clients.Keys.OrderBy(k => k, StringComparer.Ordinal)))
            d.Add($"topClients: expected {string.Join(",", e.Clients.Keys.Order())}, got {string.Join(",", byIp.Keys.Order())}");
        foreach (var (ip, ce) in e.Clients)
        {
            if (!byIp.TryGetValue(ip, out var c)) continue;
            Eq($"topClients[{ip}].requests", ce.Requests, L(c, "requests"));
            Eq($"topClients[{ip}].bytesOut", ce.BytesOut, L(c, "bytesOut"));
        }
        if (clients.Zip(clients.Skip(1)).Any(p => L(p.First, "requests") < L(p.Second, "requests"))) d.Add("topClients: not ordered by requests");
        return d;
    }

    private static JsonObject Summary(JsonElement r) => new()
    {
        ["range"] = r.GetProperty("range").GetString(),
        ["host"] = r.TryGetProperty("host", out var h) ? h.GetString() : null,
        ["bucketSize"] = r.GetProperty("bucketSize").GetString(),
        ["totals"] = JsonNode.Parse(r.GetProperty("totals").GetRawText()),
        ["statusCodes"] = JsonNode.Parse(r.GetProperty("statusCodes").GetRawText()),
        ["topHosts"] = JsonNode.Parse(r.GetProperty("topHosts").GetRawText()),
        ["topClients"] = new JsonArray(r.GetProperty("topClients").EnumerateArray().Select(c => (JsonNode)new JsonObject
        {
            ["ip"] = c.GetProperty("ip").GetString(), ["requests"] = c.GetProperty("requests").GetInt64(), ["bytesOut"] = c.GetProperty("bytesOut").GetInt64(),
        }).ToArray()),
        ["seriesPoints"] = r.GetProperty("series").GetArrayLength(),
    };

    /// <summary>
    /// Upstream of both hosts: /missing → 404 "not found", /fail → 500 "boom", /upload reads the whole body and answers 42
    /// bytes, anything else → 200 "page &lt;Host&gt;" (Caddy passes the client's Host header to an HTTP upstream).
    /// </summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public int Port { get; private set; }

        public static async Task<Upstream> StartAsync()
        {
            var u = new Upstream { Port = Net.FreePort() };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, u.Port));
            var app = builder.Build();
            app.Run(async ctx =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var (status, body) = ctx.Request.Path.Value switch
                {
                    "/missing" => (404, "not found"),
                    "/fail" => (500, "boom"),
                    "/upload" => (200, new string('u', UploadResponseBytes)),
                    _ => (200, PageBody(ctx.Request.Host.Host)),
                };
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength = body.Length;
                await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(body));
            });
            await app.StartAsync();
            u._app = app;
            return u;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
