using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// DNS-2, DNS-3, DNS-5 and STO-1: DNS challenge and storage settings that Caddy would reject are refused with field errors,
/// and the generated DNS policies are ones the real Caddy accepts. Artifact: dns-challenge-fixes.json.
/// </summary>
public sealed class DnsChallengeFixesTests
{
    // A syntactically valid Cloudflare API token (^[A-Za-z0-9_-]{35,50}$): provisioning accepts it without network access.
    private const string CloudflareToken = "cpmTestTokenCpmTestTokenCpmTestToken1234";

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    private static string[] Errors(JsonNode problem, string field) =>
        problem["errors"]?[field]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];

    /// <summary>Only GetInstalledAsync is used by the Config module's plugin checks.</summary>
    private sealed class Binary(params string[] modules) : ICaddyBinaryManager
    {
        public Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default) =>
            Task.FromResult<InstalledBinary?>(new InstalledBinary { Version = "v2.11.4", Path = "caddy", Modules = ["http", "tls", .. modules] });
        public Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
        public Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public JobInfo StartInstallOrUpdate(string? version = null) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default) => Task.FromResult(new List<PluginPackage>());
    }

    /// <summary>
    /// Ways it could fail (DNS-2):
    /// (1) a provider whose module is missing from the installed binary is saved while DNS is used (default challenge,
    ///     a DNS host or a wildcard host) — Caddy then rejects the whole configuration with a raw 422 instead of a field
    ///     error naming the plugin;
    /// (2) the check blocks saves where the provider is not used yet (the UI lets users pick the provider, then install
    ///     the plugin), or when the binary is unknown;
    /// (3) a host switched to the DNS challenge (or a wildcard host) is saved although the plugin is missing, or IP-only
    ///     hosts (which never use DNS-01) are refused;
    /// (4) the check fires although the module is installed;
    /// (5) the refusal protects nothing: the real Caddy accepts a config with a missing provider module (control).
    /// </summary>
    [CaddyFact]
    public async Task A_dns_provider_whose_plugin_is_missing_is_refused_while_dns_is_used()
    {
        var report = E2EArtifacts.Report(nameof(A_dns_provider_whose_plugin_is_missing_is_refused_while_dns_is_used));
        await using (var api = ApiHost.Start(configure: s => s.AddSingleton<ICaddyBinaryManager>(new Binary())))
        {
            // (2) not used yet: saved
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = CloudflareToken },
            });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            // (1) DNS as the default challenge
            r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { defaultAcmeChallenge = "dns" });
            var problem = await Json(r);
            report["settingsRefusal"] = problem.DeepClone();
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains(Errors(problem, "dnsProvider"), e => e.Contains("github.com/caddy-dns/cloudflare", StringComparison.Ordinal));
            Assert.Equal(AcmeChallengeType.Http, api.Store.GetSettings<CaddySettings>().DefaultAcmeChallenge);
            // (3) hosts
            r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "dns.example.com" }, tls = "acme", acmeChallenge = "dns" });
            problem = await Json(r);
            report["hostRefusal"] = problem.DeepClone();
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains(Errors(problem, "acmeChallenge"), e => e.Contains("dns.providers.cloudflare", StringComparison.Ordinal));
            r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "*.wild.example.com" }, tls = "acme" });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "203.0.113.10" }, tls = "acme", acmeChallenge = "dns" });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "http.example.com" }, tls = "acme" });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        // (4) module installed: everything is accepted
        await using (var api = ApiHost.Start(configure: s => s.AddSingleton<ICaddyBinaryManager>(new Binary("dns.providers.cloudflare"))))
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                defaultAcmeChallenge = "dns", dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = CloudflareToken },
            });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "*.wild.example.com" }, tls = "acme" });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        // (5) control: the real Caddy (no DNS plugins) rejects a config with the missing provider module.
        using var c = new LiveCaddy(s =>
        {
            s.DefaultAcmeChallenge = AcmeChallengeType.Dns;
            s.DnsProvider = "cloudflare";
        });
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["dns.example.com"], Tls = TlsMode.Acme, ResponseStatus = 200 });
        var json = c.S.Config.Generate().ToJson();
        var validation = await c.S.Config.ValidateAsync(json);
        report["controlValidation"] = validation.Error;
        Assert.False(validation.Valid);
        Assert.Contains("dns.providers.cloudflare", validation.Error ?? "");
        report["passed"] = true;
        E2EArtifacts.Write("dns-provider-plugin-check.json", report);
    }

    /// <summary>
    /// Ways it could fail (DNS-3):
    /// (1) settings with a selected provider AND a legacy challenges.dns.provider in the ACME issuer JSON are saved;
    /// (2) stored or replicated legacy data still deep-merges the two providers into one object (Caddy: unknown field),
    ///     or the selected provider loses its own fields;
    /// (3) other legacy challenges.dns options (resolvers, ttl, ...) or other issuer options are dropped too;
    /// (4) no warning tells the user to clean up;
    /// (5) the real Caddy (with the Cloudflare plugin) still rejects the generated config — or would also have accepted
    ///     the merged one (control).
    /// </summary>
    [CloudflareCaddyFact]
    public async Task A_legacy_dns_provider_in_the_acme_issuer_json_is_refused_and_never_merged_into_the_selected_one()
    {
        var report = E2EArtifacts.Report(nameof(A_legacy_dns_provider_in_the_acme_issuer_json_is_refused_and_never_merged_into_the_selected_one));
        const string legacy = """{"challenges":{"dns":{"provider":{"name":"route53","region":"eu-west-1"},"ttl":"2m"}},"acme_timeout":"45s"}""";
        await using (var api = ApiHost.Start())
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = CloudflareToken }, acmeIssuerJson = legacy,
            });
            var problem = await Json(r);
            report["settingsRefusal"] = problem.DeepClone();
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); // (1)
            Assert.Contains(Errors(problem, "acmeIssuerJson"), e => e.Contains("challenges.dns.provider", StringComparison.Ordinal));
            // Without the provider it is accepted.
            r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = CloudflareToken },
                acmeIssuerJson = """{"challenges":{"dns":{"ttl":"2m"}},"acme_timeout":"45s"}""",
            });
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        // (2)-(5) stored legacy data (Round 2 upgrade, or replicated from a primary) with the real Cloudflare-enabled Caddy
        using var c = new LiveCaddy(caddyBinary: E2ETools.CaddyCloudflare().Path);
        var protector = c.S.Provider.GetRequiredService<ISecretProtector>();
        c.UpdateSettings(s =>
        {
            s.DnsProvider = "cloudflare";
            s.DnsProviderSecretsProtected = protector.Protect(new JsonObject { ["api_token"] = CloudflareToken }.ToJsonString());
            s.AcmeIssuerJsonProtected = protector.Protect(legacy);
        });
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["*.legacy.example.com"], Tls = TlsMode.Acme, ResponseStatus = 200 });
        var generated = c.S.Config.Generate();
        report["warnings"] = new JsonArray(generated.Warnings.Select(w => (JsonNode)w).ToArray());
        var policy = generated.Config["apps"]!["tls"]!["automation"]!["policies"]!.AsArray()
            .Single(p => p!["subjects"]!.AsArray().Any(x => x!.GetValue<string>() == "*.legacy.example.com"))!;
        var issuer = policy["issuers"]![0]!;
        report["dnsIssuer"] = ConfigRedactorSafe(issuer);
        var provider = issuer["challenges"]!["dns"]!["provider"]!.AsObject();
        Assert.Equal(["api_token", "name"], provider.Select(p => p.Key).Order(StringComparer.Ordinal)); // (2)
        Assert.Equal("cloudflare", provider["name"]!.GetValue<string>());
        Assert.Equal("2m", issuer["challenges"]!["dns"]!["ttl"]!.GetValue<string>()); // (3)
        Assert.Equal("45s", issuer["acme_timeout"]!.GetValue<string>());
        Assert.Contains(generated.Warnings, w => w.Contains("challenges.dns.provider", StringComparison.Ordinal)); // (4)

        var ok = await c.S.Config.ValidateAsync(generated.ToJson()); // (5)
        report["generatedValid"] = ok.Valid;
        Assert.True(ok.Valid, ok.Error);
        var merged = generated.Config.DeepClone();
        foreach (var iss in merged["apps"]!["tls"]!["automation"]!["policies"]!.AsArray().SelectMany(p => p!["issuers"]!.AsArray()))
            if (iss!["challenges"]?["dns"] is JsonObject) CaddyJson.DeepMerge(iss.AsObject(), JsonNode.Parse(legacy)!.AsObject());
        var control = await c.S.Config.ValidateAsync(CaddyJson.Serialize(merged));
        report["controlMergedError"] = control.Error?.Replace(CloudflareToken, "***");
        Assert.False(control.Valid);
        report["passed"] = true;
        E2EArtifacts.Write("dns-legacy-provider.json", report);
    }

    private static JsonNode ConfigRedactorSafe(JsonNode issuer)
    {
        var copy = issuer.DeepClone();
        if (copy["challenges"]?["dns"]?["provider"] is JsonObject p && p.ContainsKey("api_token")) p["api_token"] = "***";
        return copy;
    }

    /// <summary>
    /// Ways it could fail (DNS-5):
    /// (1) an IP address of an ACME host whose effective challenge is DNS lands in a DNS policy (dns-01 cannot validate IP
    ///     identifiers — RFC 8738 — and a DNS solver makes certmagic use DNS-01 exclusively, so it is never issued);
    /// (2) the host's DNS names leave the DNS policy, or the IP is dropped altogether;
    /// (3) UsesDnsChallenge (host validation) treats an IP-only host as a DNS-challenge host.
    /// </summary>
    [Fact]
    public void Ip_names_of_a_dns_challenge_host_stay_in_the_http_policy()
    {
        using var env = new TempEnv();
        var settings = new CaddySettings { DefaultAcmeChallenge = AcmeChallengeType.Dns, DnsProvider = "cloudflare" };
        var host = new SiteHost { Kind = HostKind.Response, Domains = ["ip.example.com", "203.0.113.10", "2001:db8::10"], Tls = TlsMode.Acme, ResponseStatus = 200 };
        var result = CaddyConfigGenerator.Generate(Build.Input(env.Paths, settings, [host]) with
        {
            DnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = CloudflareToken },
        });
        var policies = result.Config["apps"]!["tls"]!["automation"]!["policies"]!.AsArray();
        string[] Subjects(JsonNode p) => p["subjects"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var dns = policies.Where(p => p!["issuers"]!.AsArray().Any(i => i!["challenges"]?["dns"] is not null)).ToList();
        Assert.Equal(["ip.example.com"], Subjects(Assert.Single(dns)!)); // (1)(2)
        var http = policies.Single(p => p!["issuers"]!.AsArray().All(i => i!["challenges"]?["dns"] is null))!;
        Assert.Equal(["2001:db8::10", "203.0.113.10"], Subjects(http).Order(StringComparer.Ordinal));
        // (3)
        Assert.False(CaddyConfigGenerator.UsesDnsChallenge(new SiteHost { Domains = ["203.0.113.10"], Tls = TlsMode.Acme, AcmeChallenge = HostAcmeChallenge.Dns }, settings));
        Assert.True(CaddyConfigGenerator.UsesDnsChallenge(host, settings));
    }

    /// <summary>
    /// Ways it could fail (STO-1):
    /// (1) several Redis addresses are saved (caddy-storage-redis v1.8 then creates a Redis Cluster client, which a
    ///     normal primary/replica Redis rejects on every node);
    /// (2) the error is not on the redisAddresses field or does not explain why;
    /// (3) a single address is refused.
    /// </summary>
    [Fact]
    public async Task Redis_storage_accepts_exactly_one_address()
    {
        await using var api = ApiHost.Start();
        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy",
            new { storageBackend = "redis", redisAddresses = new[] { "redis-a.corp.local:6379", "redis-b.corp.local:6379" } });
        var problem = await Json(r);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); // (1)
        Assert.Contains(Errors(problem, "redisAddresses"), e => e.Contains("Redis Cluster", StringComparison.Ordinal)); // (2)
        Assert.Equal(StorageBackend.Local, api.Store.GetSettings<CaddySettings>().StorageBackend);
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "redis", redisAddresses = new[] { "redis-a.corp.local:6379" } });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync()); // (3)
    }
}
