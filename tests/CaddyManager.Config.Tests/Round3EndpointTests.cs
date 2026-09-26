using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Round 3 API contract of the Config module, hosted in-process against a real LiveCaddy-free data directory (Caddy is not
/// running: applies are "written only"). The real-Caddy behaviour of the same features is covered by the E2E tests
/// (Dns01IssuanceE2ETests, TrafficStatsLogE2ETests, SharedStorageE2ETests, SecretScrubE2ETests).
///
/// Ways the API could fail (each is asserted below):
/// - DNS catalog: not 23 providers (hetzner offered though no working plugin can be built; rfc2136 promising Windows DNS), wrong order, secret/required/type flags wrong (e.g. route53_max_wait not "duration"),
///   `installed` true without a binary or false when the module is compiled in;
/// - secrets: a DNS/Redis/storage secret is returned by GET/PUT (any role); dnsProviderSecrets does not follow
///   set / "" removes / absent unchanged / dnsProviderSecretsClear; has* flags wrong; a provider change keeps the old
///   provider's options/secrets;
/// - validation: unknown-but-valid provider names rejected or invalid names accepted; a secret sent as a plain option
///   accepted (stored readable); DNS as default or on a host without a provider accepted; required fields not enforced;
///   bad resolvers accepted, bare IPs not normalised to :53; FileSystem storage with a relative/unwritable path accepted;
///   Redis/custom storage accepted although the installed binary lacks the module, or the plugin not named;
/// - host acmeChallenge "dns" without a provider not rejected with the field error acmeChallenge;
/// - node mode: any mutating Config endpoint (hosts incl. enable/disable, streams, access lists, certificates, Caddyfile
///   import, settings except node-local fields) not answering 409 "Managed by the cluster primary", or node-local
///   settings / POST /api/config/apply / GETs blocked;
/// - viewer redaction: Redis password / encryption key or custom storage values visible in the config preview;
/// - IConfigChangeFeed not raised after a (written-only) apply; ICertificateMaterialStore not registered or not writing
///   the store layout.
/// </summary>
public sealed class Round3EndpointTests
{
    private sealed class FakeClusterRole(ClusterRole role, string? primary) : IClusterRole
    {
        public ClusterRole Role => role;
        public string? PrimaryName => primary;
    }

    /// <summary>Only GetInstalledAsync is used by the Config module's plugin checks.</summary>
    private sealed class FakeBinaryManager(params string[] modules) : ICaddyBinaryManager
    {
        public Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default) =>
            Task.FromResult<InstalledBinary?>(new InstalledBinary { Version = "v2.11.4", Path = "caddy", Modules = ["http", "tls", .. modules] });
        public Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
        public Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public JobInfo StartInstallOrUpdate(string? version = null) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default) => Task.FromResult(new List<PluginPackage>());
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    private static string[] Errors(JsonNode problem, string field) =>
        problem["errors"]?[field]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];

    // ------------------------------------------------------------------ DNS provider catalog

    [Fact]
    public async Task Dns_provider_catalog_lists_23_typed_providers_and_reports_installed_modules()
    {
        await using (var api = ApiHost.Start())
        {
            var list = (await Json(await api.SendAsync(HttpMethod.Get, "/api/settings/caddy/dns-providers", role: "viewer"))).AsArray();
            Assert.Equal(23, list.Count);
            // DNS-1: caddyserver.com only builds caddy-dns/hetzner v1 (field auth_api_token, the DNS Console API Hetzner shut
            // down in May 2026); a catalog entry could never work, so none is offered.
            Assert.DoesNotContain(list, p => p!["name"]!.GetValue<string>() == "hetzner");
            Assert.Equal(["cloudflare", "route53", "azure", "digitalocean"], list.Take(4).Select(p => p!["name"]!.GetValue<string>()));
            Assert.All(list, p => Assert.False(p!["installed"]!.GetValue<bool>())); // no binary manager → unknown → false
            var cf = list[0]!;
            Assert.Equal("github.com/caddy-dns/cloudflare", cf["package"]!.GetValue<string>());
            Assert.Equal("dns.providers.cloudflare", cf["module"]!.GetValue<string>());
            Assert.Equal("https://github.com/caddy-dns/cloudflare", cf["docsUrl"]!.GetValue<string>());
            var token = cf["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "api_token")!;
            Assert.True(token["secret"]!.GetValue<bool>());
            Assert.True(token["required"]!.GetValue<bool>());
            var route53 = list.Single(p => p!["name"]!.GetValue<string>() == "route53")!["fields"]!.AsArray();
            Assert.Equal("duration", route53.Single(f => f!["name"]!.GetValue<string>() == "route53_max_wait")!["type"]!.GetValue<string>());
            Assert.Equal("number", route53.Single(f => f!["name"]!.GetValue<string>() == "max_retries")!["type"]!.GetValue<string>());
            Assert.Equal("boolean", route53.Single(f => f!["name"]!.GetValue<string>() == "wait_for_route53_sync")!["type"]!.GetValue<string>());
            Assert.All(route53, f => Assert.False(f!["required"]!.GetValue<bool>()));
            var rfc = list.Single(p => p!["name"]!.GetValue<string>() == "rfc2136")!["fields"]!.AsArray();
            Assert.Equal(["server", "key_name", "key_alg", "key"], rfc.Select(f => f!["name"]!.GetValue<string>()));
            // DNS-4: TSIG only — Windows DNS (AD) accepts GSS-TSIG secure updates only, so the label must not promise it.
            var rfcInfo = list.Single(p => p!["name"]!.GetValue<string>() == "rfc2136")!;
            Assert.DoesNotContain("Windows", rfcInfo["label"]!.GetValue<string>());
            Assert.Contains("GSS-TSIG", rfcInfo["notes"]!.GetValue<string>());
            Assert.Contains("HTTP challenge", rfcInfo["notes"]!.GetValue<string>());
            Assert.DoesNotContain("delegat", rfcInfo["notes"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.True(rfc.Last()!["secret"]!.GetValue<bool>());
        }

        await using var withBinary = ApiHost.Start(configure: s => s.AddSingleton<ICaddyBinaryManager>(new FakeBinaryManager("dns.providers.cloudflare")));
        var installed = (await Json(await withBinary.SendAsync(HttpMethod.Get, "/api/settings/caddy/dns-providers", role: "viewer"))).AsArray();
        Assert.Equal(["cloudflare"], installed.Where(p => p!["installed"]!.GetValue<bool>()).Select(p => p!["name"]!.GetValue<string>()));
    }

    // ------------------------------------------------------------------ settings wire shape

    [Fact]
    public async Task Dns_provider_secrets_are_write_only_and_follow_set_remove_keep_clear()
    {
        await using var api = ApiHost.Start();
        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
        {
            dnsProvider = "Route53",
            dnsProviderOptions = new Dictionary<string, string> { ["region"] = "eu-west-1", ["max_retries"] = "5" },
            dnsProviderSecrets = new Dictionary<string, string> { ["secret_access_key"] = "sak-value-1", ["session_token"] = "st-value-1" },
            dnsResolvers = new[] { "1.1.1.1", "2606:4700:4700::1111", "dns.corp.local:5353" },
        });
        var body = await Json(r);
        Assert.True(r.StatusCode == HttpStatusCode.OK, body.ToJsonString());
        var item = body["item"]!;
        Assert.Equal("route53", item["dnsProvider"]!.GetValue<string>());
        Assert.Equal(["secret_access_key", "session_token"], item["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["1.1.1.1:53", "[2606:4700:4700::1111]:53", "dns.corp.local:5353"], item["dnsResolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(item["dnsProviderSecretsProtected"]);
        Assert.DoesNotContain("sak-value-1", body.ToJsonString());

        foreach (var role in new[] { "viewer", "operator", "admin" })
        {
            var get = await (await api.SendAsync(HttpMethod.Get, "/api/settings/caddy", role: role)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("sak-value-1", get);
            Assert.DoesNotContain("st-value-1", get);
            Assert.Contains("\"region\":\"eu-west-1\"", get); // options are not secret
        }

        // "" removes one key, absent keys stay, a new key is added
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProviderSecrets = new Dictionary<string, string> { ["session_token"] = "", ["secret_access_key"] = "sak-value-2" } }));
        Assert.Equal(["secret_access_key"], body["item"]!["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
        // absent object = unchanged
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { acmeEmail = "a@b.example" }));
        Assert.Equal(["secret_access_key"], body["item"]!["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
        // an ACME host using DNS: the generated issuer carries the typed options and the latest secret (admins only)
        var host = await api.SendAsync(HttpMethod.Post, "/api/hosts",
            new { kind = "response", domains = new[] { "r53.example.com" }, tls = "acme", acmeChallenge = "dns", responseStatus = 200 });
        Assert.Equal(HttpStatusCode.OK, host.StatusCode);
        var generated = JsonNode.Parse((await Json(await api.SendAsync(HttpMethod.Get, "/api/config/preview")))["json"]!.GetValue<string>())!;
        var provider = generated["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]![0]!["challenges"]!["dns"]!["provider"]!;
        Assert.Equal("route53", provider["name"]!.GetValue<string>());
        Assert.Equal(5, provider["max_retries"]!.GetValue<long>());
        Assert.Equal("sak-value-2", provider["secret_access_key"]!.GetValue<string>());
        var viewerPreview = await (await api.SendAsync(HttpMethod.Get, "/api/config/preview", role: "viewer")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("sak-value-2", viewerPreview);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Delete, $"/api/hosts/{(await Json(host))["item"]!["id"]!.GetValue<string>()}")).StatusCode);
        // clear all, then set in the same request
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProviderSecretsClear = true, dnsProviderSecrets = new Dictionary<string, string> { ["session_token"] = "st-value-3" } }));
        Assert.Equal(["session_token"], body["item"]!["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));

        // provider change: options and secrets that are not fields of the new provider are dropped
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "cf-token-value" } }));
        Assert.Equal(["api_token"], body["item"]!["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(body["item"]!["dnsProviderOptions"]!.AsObject());
        // a provider outside the catalog is accepted by module name, with untyped options
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProvider = "my_dns", dnsProviderOptions = new Dictionary<string, string> { ["endpoint"] = "x" } }));
        Assert.Equal("my_dns", body["item"]!["dnsProvider"]!.GetValue<string>());
        Assert.Empty(body["item"]!["dnsProviderSecretFields"]!.AsArray());
        Assert.Equal("x", body["item"]!["dnsProviderOptions"]!["endpoint"]!.GetValue<string>());
        // removing the provider clears everything
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProvider = "" }));
        Assert.Null(body["item"]!["dnsProvider"]);
        Assert.Empty(body["item"]!["dnsProviderOptions"]!.AsObject());
    }

    [Fact]
    public async Task Dns_settings_are_validated()
    {
        await using var api = ApiHost.Start();
        async Task<JsonNode> Bad(object body, string field)
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", body);
            var p = await Json(r);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, p.ToJsonString());
            Assert.NotEmpty(Errors(p, field));
            return p;
        }
        await Bad(new { dnsProvider = "Bad-Name!" }, "dnsProvider");
        await Bad(new { dnsProvider = "cloudflare", dnsProviderOptions = new Dictionary<string, string> { ["api_token"] = "plain" } }, "dnsProviderOptions.api_token");
        await Bad(new { dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["zone_id"] = "x" } }, "dnsProviderSecrets.zone_id");
        await Bad(new { dnsProvider = "route53", dnsProviderOptions = new Dictionary<string, string> { ["max_retries"] = "many" } }, "dnsProviderOptions.max_retries");
        await Bad(new { defaultAcmeChallenge = "dns" }, "dnsProvider");
        await Bad(new { defaultAcmeChallenge = "dns", dnsProvider = "cloudflare" }, "dnsProviderSecrets.api_token");
        await Bad(new { defaultAcmeChallenge = "dns", dnsProvider = "azure", dnsProviderOptions = new Dictionary<string, string> { ["subscription_id"] = "s" } }, "dnsProviderOptions.resource_group_name");
        await Bad(new { dnsResolvers = new[] { "1.1.1.1:99999" } }, "dnsResolvers[0]");
        await Bad(new { dnsPropagationTimeoutSeconds = -5 }, "dnsPropagationTimeoutSeconds");
        var ok = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
        {
            defaultAcmeChallenge = "dns", dnsProvider = "cloudflare", dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "t" },
            dnsPropagationTimeoutSeconds = -1,
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // the provider may not be removed while it is the default challenge
        await Bad(new { dnsProvider = "" }, "dnsProvider");
    }

    [Fact]
    public async Task Host_dns_challenge_needs_a_configured_provider()
    {
        await using var api = ApiHost.Start();
        object Host(string domain) => new { kind = "response", domains = new[] { domain }, tls = "acme", acmeChallenge = "dns", responseStatus = 200 };
        var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Host("dns.example.com"), role: "operator");
        var p = await Json(r);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.NotEmpty(Errors(p, "acmeChallenge"));

        // provider selected but its required secret missing → still acmeChallenge error
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProvider = "cloudflare" })).StatusCode);
        p = await Json(await api.SendAsync(HttpMethod.Post, "/api/hosts", Host("dns.example.com"), role: "operator"));
        Assert.Contains("api_token", Errors(p, "acmeChallenge").Single());

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "tok-123456" } })).StatusCode);
        r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Host("dns.example.com"), role: "operator");
        var created = await Json(r);
        Assert.True(r.StatusCode == HttpStatusCode.OK, created.ToJsonString());
        Assert.Equal("dns", created["item"]!["acmeChallenge"]!.GetValue<string>());
        // with the host in use, removing the token is refused
        var removal = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "" } });
        Assert.Equal(HttpStatusCode.BadRequest, removal.StatusCode);
        // non-ACME hosts drop the challenge choice
        var internalHost = await Json(await api.SendAsync(HttpMethod.Post, "/api/hosts",
            new { kind = "response", domains = new[] { "int.example.com" }, tls = "internal", acmeChallenge = "dns", responseStatus = 200 }, role: "operator"));
        Assert.Equal("default", internalHost["item"]!["acmeChallenge"]!.GetValue<string>());
    }

    [Fact]
    public async Task Storage_secrets_are_write_only_and_storage_is_validated()
    {
        var shared = Path.Combine(Path.GetTempPath(), "cpm-storage-api", Guid.NewGuid().ToString("N")[..8]);
        using var cleanup = new DirCleanup(shared);
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<ICaddyBinaryManager>(new FakeBinaryManager()));

        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = "relative/path" });
        Assert.NotEmpty(Errors(await Json(r), "storagePath"));
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = shared });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(Directory.Exists(shared)); // created by the write test
        Assert.Empty(Directory.EnumerateFiles(shared)); // the test file was removed

        // Redis: plugin missing in the (fake) installed binary → 400 naming the plugin
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "redis", redisAddresses = new[] { "redis.corp.local:6379" }, redisPassword = "redis-pass-1" });
        var p = await Json(r);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("github.com/pberkel/caddy-storage-redis", Errors(p, "storageBackend").Single());
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "redis", redisAddresses = new[] { "no-port" } });
        Assert.NotEmpty(Errors(await Json(r), "redisAddresses[0]"));
        // Custom: needs a "module"; the module must be installed
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "custom", storageJson = new { host = "x" } });
        Assert.NotEmpty(Errors(await Json(r), "storageJson"));
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "custom", storageJson = new { module = "consul", token = "consul-token-1" } });
        Assert.Contains("github.com/pteich/caddy-tlsconsul", Errors(await Json(r), "storageBackend").Single());
    }

    [Fact]
    public async Task Storage_secrets_never_reach_viewers()
    {
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<ICaddyBinaryManager>(new FakeBinaryManager("caddy.storage.redis", "caddy.storage.consul")));
        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
        {
            storageBackend = "redis", redisAddresses = new[] { "redis.corp.local:6379" }, redisPassword = "redis-pass-1", redisEncryptionKey = "enc-key-0123456789",
        });
        var body = await Json(r);
        Assert.True(r.StatusCode == HttpStatusCode.OK, body.ToJsonString());
        Assert.True(body["item"]!["hasRedisPassword"]!.GetValue<bool>());
        Assert.True(body["item"]!["hasRedisEncryptionKey"]!.GetValue<bool>());
        Assert.False(body["item"]!["hasStorageJson"]!.GetValue<bool>());
        Assert.DoesNotContain("redis-pass-1", body.ToJsonString());

        var admin = (await Json(await api.SendAsync(HttpMethod.Get, "/api/config/preview")))["json"]!.GetValue<string>();
        var storage = JsonNode.Parse(admin)!["storage"]!;
        Assert.Equal("redis", storage["module"]!.GetValue<string>());
        Assert.Equal("redis-pass-1", storage["password"]!.GetValue<string>());
        Assert.Equal("enc-key-0123456789", storage["encryption_key"]!.GetValue<string>());
        Assert.Equal(["redis.corp.local:6379"], storage["address"]!.AsArray().Select(x => x!.GetValue<string>()));
        var viewer = await (await api.SendAsync(HttpMethod.Get, "/api/config/preview", role: "viewer")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("redis-pass-1", viewer);
        Assert.DoesNotContain("enc-key-0123456789", viewer);

        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "custom", storageJson = "{\"module\":\"consul\",\"address\":\"consul:8500\",\"token\":\"consul-token-1\"}" });
        body = await Json(r);
        Assert.True(body["item"]!["hasStorageJson"]!.GetValue<bool>());
        Assert.DoesNotContain("consul-token-1", body.ToJsonString());
        viewer = await (await api.SendAsync(HttpMethod.Get, "/api/config/preview", role: "viewer")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("consul-token-1", viewer);
        Assert.DoesNotContain("consul:8500", viewer); // every string value of a custom storage module
        Assert.Contains("consul", viewer);
        // "" clears
        body = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "local", storageJson = "", redisPassword = "" }));
        Assert.False(body["item"]!["hasStorageJson"]!.GetValue<bool>());
        Assert.False(body["item"]!["hasRedisPassword"]!.GetValue<bool>());
        Assert.True(body["item"]!["hasRedisEncryptionKey"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ cluster node mode

    [Fact]
    public async Task Managed_node_rejects_replicated_changes_but_keeps_local_ones()
    {
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<IClusterRole>(new FakeClusterRole(ClusterRole.Node, "proxy-primary")));
        using var cert = TestCerts.SelfSigned(["node.example.com"]);
        var hostId = Entity.NewId();
        api.Store.Col<SiteHost>().Insert(new SiteHost { Id = hostId, Kind = HostKind.Response, Domains = ["existing.example.com"], Tls = TlsMode.None });
        var mutations = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "n.example.com" }, tls = "none", responseStatus = 200 }),
            (HttpMethod.Put, $"/api/hosts/{hostId}", new { kind = "response", domains = new[] { "existing.example.com" }, tls = "none", responseStatus = 200 }),
            (HttpMethod.Post, $"/api/hosts/{hostId}/disable", null),
            (HttpMethod.Post, $"/api/hosts/{hostId}/enable", null),
            (HttpMethod.Delete, $"/api/hosts/{hostId}", null),
            (HttpMethod.Post, "/api/streams", new { protocol = "tcp", listenPort = 3390, upstreamHost = "10.0.0.1", upstreamPort = 3389 }),
            (HttpMethod.Post, "/api/access-lists", new { name = "office", rules = Array.Empty<object>() }),
            (HttpMethod.Post, "/api/certificates/pem", new { name = "c", certPem = cert.ExportCertificatePem(), keyPem = TestCerts.KeyPem(cert) }),
            (HttpMethod.Post, "/api/certificates/path", new { name = "c", certPath = "/tmp/x.pem", keyPath = "/tmp/x.key" }),
            (HttpMethod.Delete, "/api/certificates/nope", null),
            (HttpMethod.Post, "/api/config/caddyfile/import", new { caddyfile = "a.example.com {\n respond ok\n}" }),
            (HttpMethod.Post, "/api/config/caddyfile/import/commit", new { hosts = Array.Empty<object>() }),
            (HttpMethod.Put, "/api/settings/caddy", new { acmeEmail = "ops@example.com" }),
            (HttpMethod.Put, "/api/settings/caddy", new { redisPassword = "x" }),
        };
        foreach (var (method, url, body) in mutations)
        {
            var r = await api.SendAsync(method, url, body);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.Conflict, $"{method} {url}: {(int)r.StatusCode} {text}");
            Assert.Contains(ApiResults.ManagedByPrimaryTitle, text);
            Assert.Contains("proxy-primary", text);
        }
        Assert.Single(api.Store.Col<SiteHost>().FindAll());

        // node-local settings (listeners, admin address, certificate store) stay editable; applying and reading too
        var local = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { httpPort = 18181, httpsPort = 18443, publicHttpsPort = 443, certificateStorePath = (string?)null });
        var localBody = await Json(local);
        Assert.True(local.StatusCode == HttpStatusCode.OK, localBody.ToJsonString());
        Assert.Equal(18181, localBody["item"]!["httpPort"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Post, "/api/config/apply", role: "operator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/hosts", role: "viewer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/certificates", role: "viewer")).StatusCode);

        // a primary (or standalone) server is not restricted
        await using var primary = ApiHost.Start(configure: s => s.AddSingleton<IClusterRole>(new FakeClusterRole(ClusterRole.Primary, null)));
        Assert.Equal(HttpStatusCode.OK, (await primary.SendAsync(HttpMethod.Post, "/api/hosts", mutations[0].Body)).StatusCode);
    }

    // ------------------------------------------------------------------ contracts for other modules

    [Fact]
    public async Task Config_change_feed_and_certificate_material_store_are_provided()
    {
        await using var api = ApiHost.Start();
        var feed = api.App.Services.GetRequiredService<IConfigChangeFeed>();
        var seen = new List<(ApplyResult Result, string Reason)>();
        feed.Applied += (result, reason) => { lock (seen) seen.Add((result, reason)); };
        feed.Applied += (_, _) => throw new InvalidOperationException("a failing subscriber must not break the apply");
        var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "feed.example.com" }, tls = "none", responseStatus = 200 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var entry = Assert.Single(seen);
        Assert.True(entry.Result.Success);
        Assert.True(entry.Result.WrittenOnly);
        Assert.Contains("feed.example.com", entry.Reason);

        var material = api.App.Services.GetRequiredService<ICertificateMaterialStore>();
        using var cert = TestCerts.SelfSigned(["replicated.example.com"]);
        var (certPath, keyPath) = material.WritePem("replicated1", cert.ExportCertificatePem(), TestCerts.KeyPem(cert));
        Assert.Equal(Path.Combine(api.Env.Paths.DefaultCertificateStore, "replicated1", "fullchain.pem"), certPath);
        Assert.True(File.Exists(keyPath));
        Assert.Throws<CaddyManager.Config.Certificates.CertificateImportException>(() =>
            material.WritePem("../evil", cert.ExportCertificatePem(), TestCerts.KeyPem(cert)));
        material.Delete("replicated1");
        Assert.False(Directory.Exists(Path.GetDirectoryName(certPath)));
        material.Delete("replicated1"); // no error when missing
    }
}
