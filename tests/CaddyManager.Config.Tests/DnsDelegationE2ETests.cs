using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// DNS challenge delegation (SPEC Round 3b) end to end, on the Dns01IssuanceE2ETests infrastructure: a real Caddy v2.11.4
/// with caddy-dns/rfc2136, Pebble, and a <see cref="DnsTestServer"/> serving two zones:
/// - deleg.test — the "production" zone: read-only (every UPDATE is REFUSED), holding the one-time CNAMEs
///   _acme-challenge.a.deleg.test → _acme-challenge.a.validation.test (host A, custom delegation, wildcard + base name) and
///   _acme-challenge.b.deleg.test → _acme-challenge.validation.test (host B, the settings' default delegation);
/// - validation.test — the only zone the rfc2136 credentials can write.
/// Pebble follows the CNAMEs and issues; every applied UPDATE is in validation.test. A control host with delegation Off
/// (c.deleg.test) proves the server really refuses writes to the production zone. POST /api/dns/delegation-check is
/// exercised against the same server through Settings > DNS resolvers (ok, chain, wrong target, TXT instead of CNAME,
/// missing). Artifact: dns01-delegation.json.
/// </summary>
public sealed class DnsDelegationE2ETests
{
    private const string Production = "deleg.test";
    private const string Validation = "validation.test";
    private const string DefaultTarget = "_acme-challenge.validation.test";
    private const string HostATarget = "_acme-challenge.a.validation.test";

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Dns01Fact]
    public async Task Delegated_dns01_issues_through_cname_and_never_writes_the_production_zone()
    {
        var report = E2EArtifacts.Report(nameof(Delegated_dns01_issues_through_cname_and_never_writes_the_production_zone));
        var caddyBin = E2ETools.CaddyDns().Path!;
        var pebbleBin = E2ETools.Pebble().Path!;
        report["caddyBinary"] = new JsonObject { ["version"] = E2ETools.Run(caddyBin, "version")?.Trim() };
        report["pebble"] = new JsonObject { ["version"] = E2ETools.PebbleVersion, ["binary"] = Path.GetFileName(pebbleBin) };

        var tsigKey = RandomNumberGenerator.GetBytes(32);
        await using var dns = new DnsTestServer([(Production, false), (Validation, true)], "cpm-e2e-key", tsigKey);
        dns.AddCname("_acme-challenge.a." + Production, HostATarget);
        dns.AddCname("_acme-challenge.b." + Production, DefaultTarget);
        // Only for the delegation check: a two-hop chain, a wrong target, a leftover TXT record.
        dns.AddCname("_acme-challenge.chain." + Production, "hop." + Production);
        dns.AddCname("hop." + Production, DefaultTarget);
        dns.AddCname("_acme-challenge.wrong." + Production, "_acme-challenge.other." + Validation);
        dns.AddTxt("_acme-challenge.txt." + Production, "stale-manual-challenge");
        report["zones"] = new JsonObject
        {
            [Production] = "read-only (UPDATE → REFUSED)",
            [Validation] = "writable with the TSIG key",
            ["cnames"] = new JsonArray(
                $"_acme-challenge.a.{Production} → {HostATarget}", $"_acme-challenge.b.{Production} → {DefaultTarget}",
                $"_acme-challenge.chain.{Production} → hop.{Production} → {DefaultTarget}", $"_acme-challenge.wrong.{Production} → _acme-challenge.other.{Validation}"),
        };

        await using var api = ApiHost.Start(installBinary: true, caddyBinary: caddyBin);
        using var pebble = await PebbleProcess.StartAsync(pebbleBin, dns.Endpoint, Path.Combine(api.Env.Dir, "pebble"));
        using var caddy = new CaddyProcess(api.Env.Paths);
        var admin = api.Store.GetSettings<CaddySettings>().AdminListen;
        await Wait.Until(() => Task.FromResult(CanConnect(admin)), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + caddy.Output);

        var httpsPort = Net.FreeTcpPort();
        try
        {
            // ---- settings: Pebble, rfc2136 (TSIG key write-only), resolvers = the test server, default delegation name
            var put = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                httpPort = Net.FreeTcpPort(),
                httpsPort,
                acmeCa = "custom",
                customAcmeDirectory = pebble.Directory,
                customAcmeRootPath = pebble.CaPemPath,
                acmeEmail = "e2e@deleg.test",
                dnsProvider = "rfc2136",
                dnsProviderOptions = new Dictionary<string, string> { ["server"] = dns.Endpoint, ["key_name"] = "cpm-e2e-key", ["key_alg"] = "hmac-sha256" },
                dnsProviderSecrets = new Dictionary<string, string> { ["key"] = Convert.ToBase64String(tsigKey) },
                dnsResolvers = new[] { dns.Endpoint },
                dnsTtlSeconds = 60,
                dnsPropagationTimeoutSeconds = 60,
                dnsOverrideDomain = "_ACME-Challenge.Validation.Test.", // normalised: lower case, no trailing dot
                logLevel = "debug",
            });
            var putBody = await Json(put);
            Assert.True(put.StatusCode == HttpStatusCode.OK, putBody.ToJsonString());
            Assert.Equal(DefaultTarget, putBody["item"]!["dnsOverrideDomain"]!.GetValue<string>());

            // ---- hosts: A custom delegation (wildcard + base), B the default, C delegation off (control)
            async Task<JsonNode> CreateHost(object body)
            {
                var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", body);
                var b = await Json(r);
                Assert.True(r.StatusCode == HttpStatusCode.OK, b.ToJsonString());
                return b;
            }
            var hostA = await CreateHost(new
            {
                kind = "response", domains = new[] { "*.a." + Production, "a." + Production }, tls = "acme", acmeChallenge = "dns",
                dnsDelegation = "custom", dnsOverrideDomain = HostATarget, forceHttps = false, responseStatus = 200, responseBody = "host a",
            });
            var hostB = await CreateHost(new
            {
                kind = "response", domains = new[] { "b." + Production }, tls = "acme", acmeChallenge = "dns",
                forceHttps = false, responseStatus = 200, responseBody = "host b",
            });
            var hostC = await CreateHost(new
            {
                kind = "response", domains = new[] { "c." + Production }, tls = "acme", acmeChallenge = "dns", dnsDelegation = "off",
                forceHttps = false, responseStatus = 200, responseBody = "host c",
            });
            Assert.Equal("custom", hostA["item"]!["dnsDelegation"]!.GetValue<string>());
            Assert.Equal("default", hostB["item"]!["dnsDelegation"]!.GetValue<string>());
            report["applyWarnings"] = hostC["apply"]!["warnings"]!.DeepClone();

            // ---- generated config: one DNS policy per delegation name, override_domain only inside those policies
            var revisionId = hostC["apply"]!["revisionId"]!.GetValue<string>();
            var revision = await Json(await api.SendAsync(HttpMethod.Get, $"/api/config/revisions/{revisionId}"));
            var config = JsonNode.Parse(revision["json"]!.GetValue<string>())!;
            var policies = config["apps"]!["tls"]!["automation"]!["policies"]!.AsArray();
            var summary = new JsonArray();
            foreach (var p in policies)
            {
                var subjects = p!["subjects"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray();
                var overrides = p["issuers"]!.AsArray().Select(i => i!["challenges"]?["dns"]?["override_domain"]?.GetValue<string>()).Distinct().ToArray();
                Assert.All(p["issuers"]!.AsArray(), i => Assert.Equal([dns.Endpoint], i!["challenges"]!["dns"]!["resolvers"]!.AsArray().Select(x => x!.GetValue<string>())));
                summary.Add(new JsonObject { ["subjects"] = new JsonArray(subjects.Select(s => (JsonNode)s).ToArray()), ["overrideDomain"] = overrides.Single() });
            }
            report["policies"] = summary;
            Assert.Equal(3, policies.Count);
            string[] Override(string subject) => policies.Where(p => p!["subjects"]!.AsArray().Any(s => s!.GetValue<string>() == subject))
                .SelectMany(p => p!["issuers"]!.AsArray().Select(i => i!["challenges"]?["dns"]?["override_domain"]?.GetValue<string>() ?? "(none)")).ToArray();
            Assert.Equal([HostATarget], Override("*.a." + Production));
            Assert.Equal([HostATarget], Override("a." + Production));
            Assert.Equal([DefaultTarget], Override("b." + Production));
            Assert.Equal(["(none)"], Override("c." + Production));
            Assert.Equal(2, JsonWalk.Descendants(config).OfType<JsonObject>().Count(o => o.ContainsKey("override_domain")));

            // ---- delegation check through the test server (Settings > DNS resolvers), as a viewer
            async Task<JsonNode> Check(object body)
            {
                var r = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check", body, role: "viewer");
                var b = await Json(r);
                Assert.True(r.StatusCode == HttpStatusCode.OK, b.ToJsonString());
                Assert.Equal([dns.Endpoint], b["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
                return b;
            }
            static JsonNode For(JsonNode result, string domain) => result["checks"]!.AsArray().Single(c => c!["domain"]!.GetValue<string>() == domain)!;
            var checkA = await Check(new { hostId = hostA["item"]!["id"]!.GetValue<string>() });
            Assert.Equal(["*.a." + Production, "a." + Production], checkA["checks"]!.AsArray().Select(c => c!["domain"]!.GetValue<string>()));
            Assert.All(checkA["checks"]!.AsArray(), c =>
            {
                Assert.Equal("ok", c!["status"]!.GetValue<string>());
                Assert.Equal("_acme-challenge.a." + Production, c["recordName"]!.GetValue<string>());
                Assert.Equal(HostATarget, c["expectedTarget"]!.GetValue<string>());
                Assert.Equal([HostATarget], c["found"]!.AsArray().Select(x => x!.GetValue<string>()));
            });
            var checkB = await Check(new { hostId = hostB["item"]!["id"]!.GetValue<string>() });
            Assert.Equal("ok", For(checkB, "b." + Production)["status"]!.GetValue<string>());
            // C has delegation off: nothing to check
            var offCheck = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check", new { hostId = hostC["item"]!["id"]!.GetValue<string>() }, role: "viewer");
            Assert.Equal(HttpStatusCode.BadRequest, offCheck.StatusCode);

            var mixed = await Check(new
            {
                domains = new[] { "chain." + Production, "wrong." + Production, "txt." + Production, "missing." + Production, "*.B.Deleg.Test." },
            });
            Assert.Equal("ok", For(mixed, "chain." + Production)["status"]!.GetValue<string>());
            Assert.Equal(["hop." + Production, DefaultTarget], For(mixed, "chain." + Production)["found"]!.AsArray().Select(x => x!.GetValue<string>()));
            var wrong = For(mixed, "wrong." + Production);
            Assert.Equal("wrong", wrong["status"]!.GetValue<string>());
            Assert.Equal(["_acme-challenge.other." + Validation], wrong["found"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Contains(DefaultTarget, wrong["detail"]!.GetValue<string>());
            var txt = For(mixed, "txt." + Production);
            Assert.Equal("wrong", txt["status"]!.GetValue<string>());
            Assert.Contains("TXT", txt["detail"]!.GetValue<string>());
            var missing = For(mixed, "missing." + Production);
            Assert.Equal("missing", missing["status"]!.GetValue<string>());
            Assert.Equal("_acme-challenge.missing." + Production, missing["recordName"]!.GetValue<string>());
            Assert.Contains($"_acme-challenge.missing.{Production} CNAME {DefaultTarget}", missing["detail"]!.GetValue<string>());
            var wildcardB = For(mixed, "*.b." + Production); // case and trailing dot ignored
            Assert.Equal("ok", wildcardB["status"]!.GetValue<string>());
            Assert.Equal("_acme-challenge.b." + Production, wildcardB["recordName"]!.GetValue<string>());
            // an explicit target in any case / with a trailing dot
            var explicitTarget = await Check(new { domains = new[] { "a." + Production }, target = "_ACME-challenge.A.validation.test." });
            Assert.Equal("ok", explicitTarget["checks"]![0]!["status"]!.GetValue<string>());
            report["delegationChecks"] = new JsonObject
            {
                ["hostA"] = checkA["checks"]!.DeepClone(),
                ["hostB"] = checkB["checks"]!.DeepClone(),
                ["mixed"] = mixed["checks"]!.DeepClone(),
                ["hostOff"] = (int)offCheck.StatusCode,
            };

            // ---- Caddy obtains the certificates through the CNAMEs
            bool Pebble(TlsResult r) => r.Certificate?.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase) == true;
            string Diagnostics(TlsResult r) => $"{r.Error} {r.Issuer}\n--- caddy.log\n{Dns01IssuanceE2ETests.Tail(api.Env.Paths.CaddyProcessLog)}\n--- caddy stdout\n{caddy.Output}\n--- pebble\n{pebble.Output}";
            var handshakes = new JsonObject();
            foreach (var (sni, body, expectName) in new[]
            {
                ("a." + Production, "host a", "a." + Production),
                ("x.a." + Production, "host a", "*.a." + Production),
                ("b." + Production, "host b", "b." + Production),
            })
            {
                var r = await TlsProbe.UntilAsync(httpsPort, sni, Pebble, TimeSpan.FromSeconds(120));
                handshakes[sni] = r.ToJson();
                Assert.True(Pebble(r), $"No Pebble-issued certificate for {sni}: {Diagnostics(r)}");
                Assert.Equal(200, r.Status);
                Assert.Equal(body, r.Body);
                Assert.Contains(expectName, r.DnsNames);
            }
            report["handshakes"] = handshakes;

            // Pebble resolved the challenge through the production zone's CNAMEs...
            var queries = dns.Queries;
            Assert.Contains(queries, q => q.Type == "TXT" && q.Name == $"_acme-challenge.a.{Production}." && q.Answers > 1);
            Assert.Contains(queries, q => q.Type == "TXT" && q.Name == $"_acme-challenge.b.{Production}." && q.Answers > 1);

            // ...while every applied UPDATE went to validation.test, at the delegation names.
            var cRefused = await Wait.For(() => Task.FromResult(dns.Updates.Any(u => u.Zone == Production + "." && u.Rcode == 5)), TimeSpan.FromSeconds(60));
            var cleaned = await Wait.For(() => Task.FromResult(dns.Txt(HostATarget).Count == 0 && dns.Txt(DefaultTarget).Count == 0), TimeSpan.FromSeconds(20));
            var updates = dns.Updates;
            report["dnsUpdates"] = new JsonArray(updates.Select(u => (JsonNode)new JsonObject
            {
                ["zone"] = u.Zone, ["operation"] = u.Operation, ["name"] = u.Name, ["type"] = u.Type,
                ["tsigValid"] = u.TsigValid, ["rcode"] = u.Rcode, ["error"] = u.Error,
            }).ToArray());
            report["dnsQueries"] = new JsonArray(queries.GroupBy(q => (q.Name, q.Type))
                .Select(g => (JsonNode)new JsonObject { ["name"] = g.Key.Name, ["type"] = g.Key.Type, ["count"] = g.Count(), ["answered"] = g.Count(q => q.Answers > 0) })
                .ToArray());
            var applied = updates.Where(u => u.Rcode == 0).ToList();
            Assert.NotEmpty(applied);
            Assert.All(applied, u => Assert.Equal(Validation + ".", u.Zone));
            Assert.All(applied, u => Assert.True(u.TsigValid, u.Error));
            Assert.Contains(applied, u => u.Operation == "add" && u.Type == "TXT" && u.Name == HostATarget + ".");
            Assert.Contains(applied, u => u.Operation == "add" && u.Type == "TXT" && u.Name == DefaultTarget + ".");
            // The production zone was never written: only the control host (delegation off) tried, and was refused.
            var production = updates.Where(u => u.Zone == Production + ".").ToList();
            Assert.True(cRefused, "The control host with delegation off never tried to write the production zone:\n" + Dns01IssuanceE2ETests.Tail(api.Env.Paths.CaddyProcessLog));
            Assert.All(production, u => Assert.Equal(5, u.Rcode));
            Assert.All(production, u => Assert.Equal($"_acme-challenge.c.{Production}.", u.Name));
            Assert.Empty(dns.Txt($"_acme-challenge.c.{Production}"));
            var c = await TlsProbe.GetAsync(httpsPort, "c." + Production);
            Assert.False(Pebble(c), "The control host must not get a certificate: its zone refuses updates.");
            report["controlHostOff"] = new JsonObject { ["refusedUpdates"] = production.Count, ["handshake"] = c.ToJson() };
            report["challengeRecordsRemoved"] = cleaned;
            report["certificateInventory"] = new JsonArray((await api.Client.GetFromJsonAsync<JsonArray>("/api/certificates"))!
                .Select(x => (JsonNode)new JsonObject { ["kind"] = x!["kind"]!.DeepClone(), ["name"] = x["name"]!.DeepClone(), ["issuer"] = x["issuer"]?.DeepClone() }).ToArray());
            report["passed"] = true;
        }
        finally
        {
            E2EArtifacts.Write("dns01-delegation.json", report);
        }
    }

    private static bool CanConnect(string listen)
    {
        var colon = listen.LastIndexOf(':');
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(IPAddress.Loopback, int.Parse(listen[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
