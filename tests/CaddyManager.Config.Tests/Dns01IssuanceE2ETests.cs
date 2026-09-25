using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Real DNS-01 issuance end to end: settings and host are configured through the manager's HTTP API, the generated config
/// is loaded into a real Caddy v2.11.4 built with caddy-dns/rfc2136, Caddy updates an authoritative DNS server (this
/// test's <see cref="DnsTestServer"/>, TSIG-verified) and Pebble validates the TXT record through the same server.
/// Artifact: dns01-issuance.json.
/// </summary>
public sealed class Dns01IssuanceE2ETests
{
    private const string Zone = "dns01.test";

    [Dns01Fact]
    public async Task Wildcard_and_exact_names_are_issued_over_dns01_through_rfc2136()
    {
        var report = E2EArtifacts.Report(nameof(Wildcard_and_exact_names_are_issued_over_dns01_through_rfc2136));
        var caddyBin = E2ETools.CaddyDns().Path!;
        var pebbleBin = E2ETools.Pebble().Path!;
        report["caddyBinary"] = new JsonObject
        {
            ["version"] = E2ETools.Run(caddyBin, "version")?.Trim(),
            ["dnsProviders"] = new JsonArray(E2ETools.Modules(caddyBin, "dns.providers.").Select(m => (JsonNode)m).ToArray()),
        };
        report["pebble"] = new JsonObject { ["version"] = E2ETools.PebbleVersion, ["binary"] = Path.GetFileName(pebbleBin) };

        var tsigKey = RandomNumberGenerator.GetBytes(32);
        await using var dns = new DnsTestServer(Zone, "cpm-e2e-key", tsigKey);
        await using var api = ApiHost.Start(installBinary: true, caddyBinary: caddyBin);
        using var pebble = await PebbleProcess.StartAsync(pebbleBin, dns.Endpoint, Path.Combine(api.Env.Dir, "pebble"));
        using var caddy = new CaddyProcess(api.Env.Paths);
        var admin = api.Store.GetSettings<CaddySettings>().AdminListen;
        await Wait.Until(async () => await CanConnect(admin), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + caddy.Output);

        var httpsPort = Net.FreeTcpPort();
        try
        {
            // ---- settings through the API: Pebble as custom CA, rfc2136 as DNS provider (secret TSIG key write-only)
            var put = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                httpPort = Net.FreeTcpPort(),
                httpsPort,
                acmeCa = "custom",
                customAcmeDirectory = pebble.Directory,
                customAcmeRootPath = pebble.CaPemPath,
                acmeEmail = "e2e@dns01.test",
                dnsProvider = "rfc2136",
                dnsProviderOptions = new Dictionary<string, string> { ["server"] = dns.Endpoint, ["key_name"] = "cpm-e2e-key", ["key_alg"] = "hmac-sha256" },
                dnsProviderSecrets = new Dictionary<string, string> { ["key"] = Convert.ToBase64String(tsigKey) },
                dnsResolvers = new[] { dns.Endpoint },
                dnsTtlSeconds = 60,
                dnsPropagationTimeoutSeconds = 60,
                logLevel = "debug",
            });
            var putBody = JsonNode.Parse(await put.Content.ReadAsStringAsync())!;
            Assert.True(put.StatusCode == HttpStatusCode.OK, putBody.ToJsonString());
            Assert.Equal(["key"], putBody["item"]!["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.DoesNotContain(Convert.ToBase64String(tsigKey), putBody.ToJsonString());
            Assert.False(putBody["apply"]!["writtenOnly"]!.GetValue<bool>());

            // ---- the host: a wildcard and an exact name it covers, TLS = ACME with the DNS challenge
            var post = await api.SendAsync(HttpMethod.Post, "/api/hosts", new
            {
                kind = "response",
                domains = new[] { "*." + Zone, "a." + Zone },
                tls = "acme",
                acmeChallenge = "dns",
                forceHttps = false,
                responseStatus = 200,
                responseBody = "dns01 ok",
            });
            var postBody = JsonNode.Parse(await post.Content.ReadAsStringAsync())!;
            Assert.True(post.StatusCode == HttpStatusCode.OK, postBody.ToJsonString());
            report["applyWarnings"] = postBody["apply"]!["warnings"]!.DeepClone();

            // ---- the loaded config carries challenges.dns on the ACME issuer (typed fields + secret, resolvers, durations)
            var revisionId = postBody["apply"]!["revisionId"]!.GetValue<string>();
            var revision = JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, $"/api/config/revisions/{revisionId}")).Content.ReadAsStringAsync())!;
            var config = JsonNode.Parse(revision["json"]!.GetValue<string>())!;
            var policy = config["apps"]!["tls"]!["automation"]!["policies"]!.AsArray()
                .Single(p => p!["subjects"]!.AsArray().Any(s => s!.GetValue<string>() == "*." + Zone))!;
            var issuer = policy["issuers"]![0]!;
            var challengeDns = issuer["challenges"]!["dns"]!;
            Assert.Equal("rfc2136", challengeDns["provider"]!["name"]!.GetValue<string>());
            Assert.Equal(dns.Endpoint, challengeDns["provider"]!["server"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(tsigKey), challengeDns["provider"]!["key"]!.GetValue<string>());
            Assert.Equal([dns.Endpoint], challengeDns["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal("60s", challengeDns["propagation_timeout"]!.GetValue<string>());
            Assert.Equal("60s", challengeDns["ttl"]!.GetValue<string>());
            Assert.Null(issuer["challenges"]!["http"]);
            Assert.Equal(pebble.Directory, issuer["ca"]!.GetValue<string>());
            // Viewers never see the TSIG secret.
            var viewerRevision = await (await api.SendAsync(HttpMethod.Get, $"/api/config/revisions/{revisionId}", role: "viewer")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Convert.ToBase64String(tsigKey), viewerRevision);
            report["generatedDnsChallenge"] = Redacted(challengeDns);

            // ---- Caddy obtains the certificate in the background: wait for a handshake presenting it
            var exact = await TlsProbe.UntilAsync(httpsPort, "a." + Zone, r => r.Certificate?.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(120));
            report["handshakeExact"] = exact.ToJson();
            Assert.True(exact.Certificate is not null && exact.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase),
                $"No Pebble-issued certificate for a.{Zone}: {exact.Error} {exact.Issuer}\n--- caddy.log\n{Tail(api.Env.Paths.CaddyProcessLog)}\n--- caddy stdout\n{caddy.Output}\n--- pebble\n{pebble.Output}");
            Assert.Equal(200, exact.Status);
            Assert.Equal("dns01 ok", exact.Body);
            Assert.Contains(exact.DnsNames, n => n == "a." + Zone || n == "*." + Zone);
            var other = await TlsProbe.UntilAsync(httpsPort, "b." + Zone, r => r.Certificate?.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase) == true, TimeSpan.FromSeconds(30));
            report["handshakeWildcardOnly"] = other.ToJson();
            Assert.Contains("*." + Zone, other.DnsNames);

            // ---- the served chain verifies against Pebble's root (Pebble really issued it after validating)
            var root = X509Certificate2.CreateFromPem((await pebble.ManagementGetAsync("/roots/0"))!);
            var intermediate = X509Certificate2.CreateFromPem((await pebble.ManagementGetAsync("/intermediates/0"))!);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ExtraStore.Add(intermediate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var chainOk = chain.Build(exact.Certificate!);
            report["chain"] = new JsonObject
            {
                ["verified"] = chainOk,
                ["root"] = root.Subject,
                ["intermediate"] = intermediate.Subject,
                ["status"] = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())),
            };
            Assert.True(chainOk, "Chain does not verify against Pebble's root: " + report["chain"]!.ToJsonString());

            // ---- the DNS server saw Caddy's signed UPDATE for the challenge record, and Pebble's TXT lookup
            var updates = dns.Updates;
            var adds = updates.Where(u => u.Operation == "add" && u.Type == "TXT" && u.Name == "_acme-challenge." + Zone + ".").ToList();
            Assert.NotEmpty(adds);
            Assert.All(adds, u => Assert.True(u.TsigValid, u.Error));
            Assert.All(adds, u => Assert.Equal("cpm-e2e-key.", u.KeyName));
            var txtQueries = dns.Queries.Where(q => q.Type == "TXT" && q.Name == "_acme-challenge." + Zone + "." && q.Answers > 0).ToList();
            Assert.NotEmpty(txtQueries);
            // certmagic removes the challenge record after validation.
            var cleaned = await Wait.For(() => Task.FromResult(dns.Updates.Any(u => u.Operation.StartsWith("delete", StringComparison.Ordinal) && u.TsigValid)), TimeSpan.FromSeconds(20));
            report["dnsUpdates"] = new JsonArray(dns.Updates.Select(u => (JsonNode)new JsonObject
            {
                ["zone"] = u.Zone, ["operation"] = u.Operation, ["name"] = u.Name, ["type"] = u.Type,
                ["tsigValid"] = u.TsigValid, ["keyName"] = u.KeyName, ["error"] = u.Error,
            }).ToArray());
            report["dnsQueries"] = new JsonArray(dns.Queries.GroupBy(q => (q.Transport, q.Name, q.Type))
                .Select(g => (JsonNode)new JsonObject { ["transport"] = g.Key.Transport, ["name"] = g.Key.Name, ["type"] = g.Key.Type, ["count"] = g.Count(), ["answered"] = g.Count(q => q.Answers > 0) })
                .ToArray());
            report["challengeRecordRemoved"] = cleaned && dns.Txt("_acme-challenge." + Zone).Count == 0;
            report["certificateInventory"] = new JsonArray((await api.Client.GetFromJsonAsync<JsonArray>("/api/certificates"))!
                .Select(c => (JsonNode)new JsonObject { ["kind"] = c!["kind"]!.DeepClone(), ["name"] = c["name"]!.DeepClone(), ["issuer"] = c["issuer"]?.DeepClone() }).ToArray());
            report["passed"] = true;
        }
        finally
        {
            E2EArtifacts.Write("dns01-issuance.json", report);
        }
    }

    private static JsonNode Redacted(JsonNode challengeDns)
    {
        var copy = challengeDns.DeepClone();
        if (copy["provider"] is JsonObject p && p.ContainsKey("key")) p["key"] = "***";
        return copy;
    }

    private static async Task<bool> CanConnect(string listen)
    {
        var colon = listen.LastIndexOf(':');
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, int.Parse(listen[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    internal static string Tail(string file, int lines = 80)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var all = new StreamReader(fs).ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(lines));
        }
        catch (IOException)
        {
            return "(no " + file + ")";
        }
    }
}
