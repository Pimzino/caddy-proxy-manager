using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// API contract of DNS challenge delegation (SPEC Round 3b), hosted in-process (Caddy not running). Issuance through the
/// CNAMEs and the ok / chain / wrong / TXT / missing statuses against a real DNS server are covered by
/// DnsDelegationE2ETests.
///
/// Ways the API could fail (each is asserted below):
/// - host: `custom` accepted without a name, or with a wildcard / IP / single-label / malformed name; underscore labels
///   rejected; the name not normalised (case, trailing dot); a name kept when delegation is not `custom`; delegation
///   kept on a non-ACME host (it must reset to `default`, like acmeChallenge); a non-default delegation accepted on an
///   ACME host whose challenge is HTTP (explicitly or through the settings' default) — field error `dnsDelegation`;
///   rejected on an HTTP-challenge host with wildcards although those use DNS;
/// - settings: DnsOverrideDomain accepted with a wildcard, spaces or an IP, or not normalised; "" not clearing it;
/// - delegation check: anonymous callers served; unknown host not 404; a host without DNS challenge or without a
///   delegation name not 400 (hostId / target); missing or invalid domains / target not 400; the settings' resolvers
///   not used; an unreachable or unresolvable resolver turning into a 500 or a hang instead of per-domain `error` within
///   the 5 s budget; answers cached between two checks; publicResolvers / OS resolvers not reported.
/// </summary>
public sealed class DnsDelegationEndpointTests
{
    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    private static string[] Errors(JsonNode problem, string field) =>
        problem["errors"]?[field]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];

    private static async Task ConfigureProvider(ApiHost api, object? extra = null)
    {
        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
        {
            dnsProvider = "cloudflare",
            dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "tok-123456" },
        });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        if (extra is not null)
        {
            r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", extra);
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
    }

    private static object Host(string[] domains, string tls = "acme", string challenge = "dns", string? delegation = null, string? name = null) => new
    {
        kind = "response", domains, tls, acmeChallenge = challenge, dnsDelegation = delegation ?? "default", dnsOverrideDomain = name, responseStatus = 200,
    };

    [Fact]
    public async Task Host_delegation_is_validated_and_normalised()
    {
        await using var api = ApiHost.Start();
        await ConfigureProvider(api);
        async Task Bad(object body, string field, string? contains = null)
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", body, role: "operator");
            var p = await Json(r);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, p.ToJsonString());
            var errors = Errors(p, field);
            Assert.NotEmpty(errors);
            if (contains is not null) Assert.Contains(contains, errors.Single());
        }
        async Task<JsonNode> Ok(object body)
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", body, role: "operator");
            var b = await Json(r);
            Assert.True(r.StatusCode == HttpStatusCode.OK, b.ToJsonString());
            return b["item"]!;
        }

        await Bad(Host(["a.example.com"], delegation: "custom"), "dnsOverrideDomain");
        await Bad(Host(["a.example.com"], delegation: "custom", name: "*.validation.example.net"), "dnsOverrideDomain", "wildcard");
        await Bad(Host(["a.example.com"], delegation: "custom", name: "bad name.example.net"), "dnsOverrideDomain");
        await Bad(Host(["a.example.com"], delegation: "custom", name: "10.0.0.1"), "dnsOverrideDomain");
        await Bad(Host(["a.example.com"], delegation: "custom", name: "validation"), "dnsOverrideDomain");
        await Bad(Host(["a.example.com"], delegation: "custom", name: "-bad.example.net"), "dnsOverrideDomain");
        // delegation on an ACME host that uses the HTTP challenge
        await Bad(Host(["a.example.com"], challenge: "http", delegation: "off"), "dnsDelegation");
        await Bad(Host(["a.example.com"], challenge: "http", delegation: "custom", name: "_acme-challenge.validation.example.net"), "dnsDelegation");
        await Bad(Host(["a.example.com"], challenge: "default", delegation: "custom", name: "_acme-challenge.validation.example.net"), "dnsDelegation");

        var custom = await Ok(Host(["a.example.com"], delegation: "custom", name: " _ACME-Challenge.Shop.Validation.Example.Net. "));
        Assert.Equal("custom", custom["dnsDelegation"]!.GetValue<string>());
        Assert.Equal("_acme-challenge.shop.validation.example.net", custom["dnsOverrideDomain"]!.GetValue<string>());
        // underscores anywhere, acme-dns style names
        Assert.Equal("d420c923-bbd7-4056-ab64-c3ca54c9b3cf.auth.acme-dns.io",
            (await Ok(Host(["b.example.com"], delegation: "custom", name: "d420c923-bbd7-4056-ab64-c3ca54c9b3cf.auth.acme-dns.io")))["dnsOverrideDomain"]!.GetValue<string>());
        // a name given without "custom" is dropped
        var off = await Ok(Host(["c.example.com"], delegation: "off", name: "_acme-challenge.validation.example.net"));
        Assert.Equal("off", off["dnsDelegation"]!.GetValue<string>());
        Assert.Null(off["dnsOverrideDomain"]);
        // an HTTP-challenge host with a wildcard uses DNS for the wildcard, so it may delegate
        var wildcard = await Ok(Host(["d.example.com", "*.d.example.com"], challenge: "http", delegation: "custom", name: "_acme-challenge.wild.example.net"));
        Assert.Equal("custom", wildcard["dnsDelegation"]!.GetValue<string>());
        // non-ACME hosts drop the delegation silently (like acmeChallenge)
        var internalHost = await Ok(Host(["e.example.com"], tls: "internal", delegation: "custom", name: "_acme-challenge.validation.example.net"));
        Assert.Equal("default", internalHost["dnsDelegation"]!.GetValue<string>());
        Assert.Null(internalHost["dnsOverrideDomain"]);

        // with DNS as the settings' default, a "default" challenge host may delegate; PUT validates the same way
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { defaultAcmeChallenge = "dns" })).StatusCode);
        var viaDefault = await Ok(Host(["f.example.com"], challenge: "default", delegation: "off"));
        var put = await api.SendAsync(HttpMethod.Put, $"/api/hosts/{viaDefault["id"]!.GetValue<string>()}",
            Host(["f.example.com"], challenge: "http", delegation: "off"), role: "operator");
        Assert.NotEmpty(Errors(await Json(put), "dnsDelegation"));

        var viewer = await Json(await api.SendAsync(HttpMethod.Get, $"/api/hosts/{custom["id"]!.GetValue<string>()}", role: "viewer"));
        Assert.Equal("_acme-challenge.shop.validation.example.net", viewer["dnsOverrideDomain"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_default_delegation_name_is_validated_and_normalised()
    {
        await using var api = ApiHost.Start();
        foreach (var bad in new[] { "*.validation.example.net", "bad name.example.net", "1.2.3.4", "validation", "a..example.net" })
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsOverrideDomain = bad });
            var p = await Json(r);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, bad + ": " + p.ToJsonString());
            Assert.NotEmpty(Errors(p, "dnsOverrideDomain"));
        }
        var ok = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsOverrideDomain = "_ACME-challenge.Validation.Example.Net." }));
        Assert.Equal("_acme-challenge.validation.example.net", ok["item"]!["dnsOverrideDomain"]!.GetValue<string>());
        var cleared = await Json(await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsOverrideDomain = "" }));
        Assert.Null(cleared["item"]!["dnsOverrideDomain"]);
    }

    [Fact]
    public async Task Delegation_check_validates_its_input()
    {
        await using var api = ApiHost.Start();
        await ConfigureProvider(api);
        async Task<JsonNode> Check(object body, HttpStatusCode expected, string role = "viewer")
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check", body, role: role);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {text}");
            return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
        }

        await Check(new { domains = new[] { "a.example.com" }, target = "_acme-challenge.v.example.net" }, HttpStatusCode.Unauthorized, role: "anonymous");
        await Check(new { hostId = "nope" }, HttpStatusCode.NotFound);
        var http = (await Json(await api.SendAsync(HttpMethod.Post, "/api/hosts", Host(["h.example.com"], challenge: "http"))))["item"]!["id"]!.GetValue<string>();
        Assert.NotEmpty(Errors(await Check(new { hostId = http }, HttpStatusCode.BadRequest), "hostId"));
        var noName = (await Json(await api.SendAsync(HttpMethod.Post, "/api/hosts", Host(["n.example.com"]))))["item"]!["id"]!.GetValue<string>();
        Assert.NotEmpty(Errors(await Check(new { hostId = noName }, HttpStatusCode.BadRequest), "target"));
        Assert.NotEmpty(Errors(await Check(new { domains = Array.Empty<string>(), target = "_acme-challenge.v.example.net" }, HttpStatusCode.BadRequest), "domains"));
        Assert.NotEmpty(Errors(await Check(new { domains = new[] { "bad domain!" }, target = "_acme-challenge.v.example.net" }, HttpStatusCode.BadRequest), "domains"));
        Assert.NotEmpty(Errors(await Check(new { domains = new[] { "10.0.0.1" }, target = "_acme-challenge.v.example.net" }, HttpStatusCode.BadRequest), "domains"));
        Assert.NotEmpty(Errors(await Check(new { domains = new[] { "a.example.com" } }, HttpStatusCode.BadRequest), "target"));
        Assert.NotEmpty(Errors(await Check(new { domains = new[] { "a.example.com" }, target = "*.v.example.net" }, HttpStatusCode.BadRequest), "target"));
        Assert.NotEmpty(Errors(await Check(new { domains = Enumerable.Range(0, 101).Select(i => $"h{i}.example.com").ToArray(), target = "v.example.net" }, HttpStatusCode.BadRequest), "domains"));
    }

    [Fact]
    public async Task Delegation_check_uses_the_settings_resolvers_never_caches_and_reports_unreachable_resolvers()
    {
        await using var dns = new DnsTestServer([("example.test", false)], "unused-key", RandomNumberGenerator.GetBytes(32));
        await using var api = ApiHost.Start();
        await ConfigureProvider(api, new { dnsResolvers = new[] { dns.Endpoint }, dnsOverrideDomain = "_acme-challenge.validation.example.net" });
        var host = (await Json(await api.SendAsync(HttpMethod.Post, "/api/hosts", Host(["shop.example.test", "*.shop.example.test"]))))["item"]!["id"]!.GetValue<string>();

        async Task<JsonNode> Check(object body)
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check", body, role: "viewer");
            var b = await Json(r);
            Assert.True(r.StatusCode == HttpStatusCode.OK, b.ToJsonString());
            return b;
        }

        var before = await Check(new { hostId = host });
        Assert.Equal([dns.Endpoint], before["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.All(before["checks"]!.AsArray(), c => Assert.Equal("missing", c!["status"]!.GetValue<string>()));
        Assert.All(before["checks"]!.AsArray(), c => Assert.Equal("_acme-challenge.shop.example.test", c!["recordName"]!.GetValue<string>()));
        Assert.All(before["checks"]!.AsArray(), c => Assert.Equal("_acme-challenge.validation.example.net", c!["expectedTarget"]!.GetValue<string>()));
        // The record is created: the next check sees it at once (no cache).
        dns.AddCname("_acme-challenge.shop.example.test", "_ACME-Challenge.Validation.Example.Net.");
        var after = await Check(new { hostId = host });
        Assert.All(after["checks"]!.AsArray(), c => Assert.Equal("ok", c!["status"]!.GetValue<string>()));
        // one CNAME query per check: the wildcard and the base name share the record; the second went to the server again
        Assert.Equal([0, 1], dns.Queries.Where(q => q.Name == "_acme-challenge.shop.example.test." && q.Type == "CNAME").Select(q => q.Answers));

        // A resolver where nothing answers: per-domain error inside the 5 s budget, not a failed request.
        var closed = Net.FreeTcpPort();
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsResolvers = new[] { $"127.0.0.1:{closed}" } })).StatusCode);
        var sw = Stopwatch.StartNew();
        var dead = await Check(new { domains = new[] { "a.example.test", "b.example.test" }, target = "v.example.net" });
        sw.Stop();
        Assert.All(dead["checks"]!.AsArray(), c => Assert.Equal("error", c!["status"]!.GetValue<string>()));
        Assert.All(dead["checks"]!.AsArray(), c => Assert.False(string.IsNullOrEmpty(c!["detail"]?.GetValue<string>())));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");

        // A resolver host name that does not resolve.
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { dnsResolvers = new[] { "no-such-resolver.invalid:53" } })).StatusCode);
        var unresolved = await Check(new { domains = new[] { "a.example.test" }, target = "v.example.net" });
        Assert.Equal("error", unresolved["checks"]![0]!["status"]!.GetValue<string>());
        Assert.Contains("no-such-resolver.invalid", unresolved["checks"]![0]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delegation_check_reports_public_and_system_resolvers()
    {
        // The statuses depend on this machine's network; only the resolver choice and the time budget are asserted.
        await using var api = ApiHost.Start();
        var sw = Stopwatch.StartNew();
        var r = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check",
            new { domains = new[] { "delegation-check.invalid" }, target = "_acme-challenge.validation.invalid", publicResolvers = true }, role: "viewer");
        var pub = await Json(r);
        Assert.True(r.StatusCode == HttpStatusCode.OK, pub.ToJsonString());
        Assert.Equal(["1.1.1.1:53", "8.8.8.8:53"], pub["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
        r = await api.SendAsync(HttpMethod.Post, "/api/dns/delegation-check",
            new { domains = new[] { "delegation-check.invalid" }, target = "_acme-challenge.validation.invalid" }, role: "viewer");
        var sys = await Json(r);
        Assert.True(r.StatusCode == HttpStatusCode.OK, sys.ToJsonString());
        Assert.Equal(["system"], sys["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        foreach (var c in pub["checks"]!.AsArray().Concat(sys["checks"]!.AsArray()))
            Assert.Contains(c!["status"]!.GetValue<string>(), new[] { "missing", "error" });
    }
}
