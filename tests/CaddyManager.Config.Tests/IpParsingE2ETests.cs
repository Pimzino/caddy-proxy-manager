using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Services;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// IP address / CIDR parsing must agree with Caddy (Go netip), research #75. .NET's IPAddress.TryParse accepts
/// "10" (0.0.0.10), "10.1", "010.1.1.1" (octal → 8.1.1.1) and "0x0a.0.0.1"; Caddy rejects them and the WHOLE
/// config load fails, or a silently different address is used.
/// </summary>
public sealed class IpParsingE2ETests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    private static readonly string[] Valid =
    [
        "10.0.0.1", "10.0.0.0/8", "10.1.2.3/8", "0.0.0.0/0", "255.255.255.255", "192.168.1.0/24", "1.2.3.4/32",
        "::1", "::/0", "fd00::/8", "2001:db8::1/64", "2001:0db8:0000::1", "FE80::1", "::ffff:10.0.0.1",
    ];

    private static readonly string[] Invalid =
    [
        "10", "10.1", "1.2.3", "010.1.1.1", "01.02.03.04", "0x0a.0.0.1", "256.1.1.1", "1.2.3.4.5", "1.2.3.4/33",
        "1.2.3.4/033", "1.2.3.4/", "/8", "1.2.3.4/-1", "::ffff:010.0.0.1", "1.2.3.4 /8", "::1/129", "fe80::1%eth0/64",
    ];

    /// <summary>
    /// Ways it could fail: (1) a value our validation accepts makes `caddy validate` (and so every apply) fail;
    /// (2) a value Caddy would reject is accepted by the API for access-list rules, trusted proxies, bind addresses,
    /// upstream hosts or host domains; (3) an accepted value is emitted in a different spelling that means a
    /// different address (octal) — the emitted form must be canonical; (4) our validation rejects something Caddy
    /// accepts (over-strict); (5) the control does not show that Caddy rejects the raw bad values.
    /// </summary>
    [CaddyFact]
    public async Task Every_accepted_ip_value_passes_caddy_validate_and_the_rest_are_rejected_by_the_api()
    {
        var report = E2EArtifacts.Report(nameof(Every_accepted_ip_value_passes_caddy_validate_and_the_rest_are_rejected_by_the_api));
        await using var api = ApiHost.Start(installBinary: true);
        var c = api.Client;
        var config = api.App.Services.GetRequiredService<CaddyConfigService>();
        var rejected = new JsonArray();

        // (2) every bad value is refused wherever an address can be entered.
        foreach (var bad in Invalid)
        {
            var row = new JsonObject { ["value"] = bad };
            var al = await c.PostAsJsonAsync("/api/access-lists", new { name = "bad " + bad, rules = new[] { new { action = "allow", cidr = bad } } }, Json);
            row["accessListRule"] = (int)al.StatusCode;
            Assert.True(al.StatusCode == HttpStatusCode.BadRequest, $"access-list rule '{bad}' → {(int)al.StatusCode}");
            var tp = await c.PutAsJsonAsync("/api/settings/caddy", new { trustedProxies = new[] { bad } }, Json);
            row["trustedProxy"] = (int)tp.StatusCode;
            Assert.True(tp.StatusCode == HttpStatusCode.BadRequest, $"trusted proxy '{bad}' → {(int)tp.StatusCode}");
            if (!bad.Contains('/'))
            {
                var bind = await c.PutAsJsonAsync("/api/settings/caddy", new { bindAddresses = new[] { bad } }, Json);
                row["bindAddress"] = (int)bind.StatusCode;
                Assert.True(bind.StatusCode == HttpStatusCode.BadRequest, $"bind address '{bad}' → {(int)bind.StatusCode}");
                var up = await c.PostAsJsonAsync("/api/hosts", Proxy("up-" + Guid.NewGuid().ToString("N")[..6] + ".example.com", bad), Json);
                row["upstreamHost"] = (int)up.StatusCode;
                Assert.True(up.StatusCode == HttpStatusCode.BadRequest, $"upstream host '{bad}' → {(int)up.StatusCode}");
                var dom = await c.PostAsJsonAsync("/api/hosts", Proxy(bad, "127.0.0.1"), Json);
                row["hostDomain"] = (int)dom.StatusCode;
                Assert.True(dom.StatusCode == HttpStatusCode.BadRequest, $"host domain '{bad}' → {(int)dom.StatusCode}");
            }
            rejected.Add(row);
        }
        report["rejectedByApi"] = rejected;

        // (1)(4) every good value is accepted and the applied config passes `caddy validate` (the API applies through
        // CaddyConfigService; Caddy is not running, so it validates with the binary and fails the request otherwise).
        var al2 = await c.PostAsJsonAsync("/api/access-lists", new { name = "good", rules = Valid.Select(v => new { action = "allow", cidr = v }).ToArray() }, Json);
        Assert.Equal(HttpStatusCode.OK, al2.StatusCode);
        var alId = JsonNode.Parse(await al2.Content.ReadAsStringAsync())!["item"]!["id"]!.GetValue<string>();
        var tp2 = await c.PutAsJsonAsync("/api/settings/caddy", new { trustedProxies = Valid, bindAddresses = new[] { "127.0.0.1", "::1" } }, Json);
        Assert.True(tp2.StatusCode == HttpStatusCode.OK, await tp2.Content.ReadAsStringAsync());
        var accepted = new JsonArray();
        foreach (var ip in Valid.Where(v => !v.Contains('/')))
        {
            var h = await c.PostAsJsonAsync("/api/hosts", Proxy("h" + accepted.Count + ".example.com", ip, alId), Json);
            Assert.True(h.StatusCode == HttpStatusCode.OK, $"upstream '{ip}': {await h.Content.ReadAsStringAsync()}");
            accepted.Add(ip);
        }
        var ipDomain = await c.PostAsJsonAsync("/api/hosts", Proxy("10.0.0.1", "127.0.0.1"), Json);
        Assert.True(ipDomain.StatusCode == HttpStatusCode.OK, await ipDomain.Content.ReadAsStringAsync());
        report["acceptedUpstreams"] = accepted;

        var last = api.Store.Col<ConfigRevision>().Query().OrderByDescending(r => r.CreatedAt).First();
        Assert.True(last.Success, last.Error);
        var validate = await config.ValidateAsync(last.Json);
        Assert.True(validate.Valid, validate.Error);
        report["caddyValidate"] = new JsonObject { ["valid"] = validate.Valid, ["error"] = validate.Error };

        // (3) canonical spellings in the generated config
        var cfg = JsonNode.Parse(last.Json)!;
        var ranges = JsonWalk.Descendants(cfg).OfType<JsonObject>().Where(o => o["ranges"] is JsonArray).SelectMany(o => o["ranges"]!.AsArray().Select(r => r!.GetValue<string>())).Distinct().ToList();
        report["emittedRanges"] = new JsonArray(ranges.Select(r => (JsonNode)r).ToArray());
        Assert.Contains("2001:db8::1", ranges);
        Assert.Contains("fe80::1", ranges);
        Assert.DoesNotContain("2001:0db8:0000::1", ranges);
        Assert.DoesNotContain("FE80::1", ranges);

        // (5) CONTROL: Caddy itself rejects each raw bad value (so accepting them broke the whole config). The value
        // is used where the manager uses it: a remote_ip matcher (access lists) and the server's trusted_proxies.
        // (remote_ip alone reads "fe80::1%eth0/64" as address fe80::1 with the zone "eth0/64" - not a /64 range.)
        var control = new JsonArray();
        foreach (var bad in Invalid.Where(b => b.Trim().Length > 0))
        {
            var raw = new JsonObject
            {
                ["apps"] = new JsonObject
                {
                    ["http"] = new JsonObject
                    {
                        ["servers"] = new JsonObject
                        {
                            ["x"] = new JsonObject
                            {
                                ["listen"] = new JsonArray($":{Net.FreeTcpPort()}"),
                                ["trusted_proxies"] = new JsonObject { ["source"] = "static", ["ranges"] = new JsonArray(bad) },
                                ["routes"] = new JsonArray(new JsonObject
                                {
                                    ["match"] = new JsonArray(new JsonObject { ["remote_ip"] = new JsonObject { ["ranges"] = new JsonArray(bad) } }),
                                    ["handle"] = new JsonArray(new JsonObject { ["handler"] = "static_response" }),
                                }),
                            },
                        },
                    },
                },
            };
            var v = await config.ValidateAsync(raw.ToJsonString());
            control.Add(new JsonObject { ["value"] = bad, ["caddyValid"] = v.Valid, ["error"] = v.Error });
            Assert.False(v.Valid, $"Caddy accepts '{bad}', so rejecting it is over-strict");
        }
        report["controlCaddyRejectsRaw"] = control;

        E2EArtifacts.Write("ip-parsing.json", report);
    }

    private static object Proxy(string domain, string upstreamHost, string? accessListId = null) => new
    {
        kind = "proxy",
        enabled = true,
        domains = new[] { domain },
        tls = "none",
        accessListId,
        upstreams = new[] { new { scheme = "http", host = upstreamHost, port = 8080 } },
    };
}
