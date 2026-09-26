using System.Text.Json.Nodes;
using CaddyManager.Config.Services;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// SEC-1: ISecretScrubber on lines the real Caddy wrote to caddy.log. A proxy host with a dead upstream makes Caddy log
/// every failed request at error level (http.log.error, request uri and headers included) into caddy.log — the same
/// file and format as a DNS provider error that quotes its API key or request URL (libdns/duckdns, namesilo,
/// namecheap put the key in the query string). Artifact: caddy-log-scrub.json.
/// </summary>
public sealed class CaddyLogScrubE2ETests
{
    /// <summary>
    /// Ways it could fail:
    /// (1) Caddy no longer writes the request into caddy.log (the control asserts the raw file holds every form, so the
    ///     scrubbing assertions are meaningful);
    /// (2) a form of a configured secret survives: raw, JSON-escaped by zap (\" and \\), percent-encoded (RFC 3986,
    ///     upper- or lower-case hex), query-encoded ("+" for spaces), HTML-form-encoded;
    /// (3) a secret that was configured earlier (rotated away) is still in caddy.log and no longer scrubbed;
    /// (4) a credential-like query parameter of a secret never configured here (e.g. from before a restart) survives;
    /// (5) scrubbing garbles the rest of the line (host, status, logger must stay readable) or breaks on a line without
    ///     secrets;
    /// (6) the scrubber is not the one registered as ISecretScrubber for other modules, or does not pick up a secret
    ///     saved after its first use (stale cache); CaddyConfigService.Scrub (apply errors) misses the query forms.
    /// </summary>
    [CaddyFact]
    public async Task Configured_and_former_secrets_in_real_caddy_log_lines_are_replaced_in_every_encoding()
    {
        var report = E2EArtifacts.Report(nameof(Configured_and_former_secrets_in_real_caddy_log_lines_are_replaced_in_every_encoding));
        // Characters that every encoding changes: space, +, /, =, &, non-ASCII; the Redis password has a quote and a backslash.
        const string token = "cpm tok+en/=&ä-4711-secret";
        const string redisPassword = "pa\"ss\\word-cpm-0815";
        const string rotatedToken = "old-duck-token-cpm-2025";
        const string unknownKey = "never-configured-cpm-9999";
        using var c = new LiveCaddy();
        var protector = c.S.Provider.GetRequiredService<ISecretProtector>();
        c.UpdateSettings(s =>
        {
            s.DnsProvider = "duckdns";
            s.DnsProviderSecretsProtected = protector.Protect(new JsonObject { ["api_token"] = rotatedToken }.ToJsonString());
        });
        var scrubber = c.S.Provider.GetRequiredService<ISecretScrubber>();
        Assert.Same(c.S.Provider.GetRequiredService<SecretScrubber>(), scrubber); // (6)
        Assert.Equal("x ***", scrubber.Scrub("x " + rotatedToken)); // first use caches the old token
        // Rotate: the new token and a Redis password are saved after the scrubber's first use (6); the old one stays in caddy.log (3).
        c.UpdateSettings(s =>
        {
            s.DnsProviderSecretsProtected = protector.Protect(new JsonObject { ["api_token"] = token }.ToJsonString());
            s.RedisPasswordProtected = protector.Protect(redisPassword);
        });

        var dead = Net.FreeTcpPort();
        c.Add(Build.Proxy("scrub.test", dead));
        await c.StartAsync();
        await c.ApplyAsync();

        var percent = Uri.EscapeDataString(token);
        var lowerHex = System.Text.RegularExpressions.Regex.Replace(percent, "%[0-9A-F]{2}", m => m.Value.ToLowerInvariant());
        var query = percent.Replace("%20", "+", StringComparison.Ordinal);
        var form = System.Net.WebUtility.UrlEncode(token);
        var targets = new[]
        {
            $"/update?domains=scrub&token={query}&verbose=true",      // Go url.Values.Encode (libdns/duckdns)
            $"/p/{percent}/x",                                        // RFC 3986 percent-encoding
            $"/lower?k={lowerHex}",                                   // lower-case hex
            $"/form?k={form}",                                        // HTML form encoding
            $"/old?domains=x&token={rotatedToken}",                   // (3)
            $"/api?command=dnsAddRecord&version=1&key={unknownKey}",  // (4) NameSilo-style key parameter
        };
        foreach (var t in targets)
        {
            var r = await c.HttpAsync("scrub.test", t, ("X-Api-Key", redisPassword));
            Assert.Equal(502, r.Status);
        }
        var clean = await c.HttpAsync("scrub.test", "/plain");
        Assert.Equal(502, clean.Status);

        List<string> Lines() => c.ProcessLog().Split('\n').Where(l => l.Contains("\"logger\":\"http.log.error", StringComparison.Ordinal)).ToList();
        await Wait.Until(() => Task.FromResult(Lines().Count >= targets.Length + 1), TimeSpan.FromSeconds(10),
            () => "caddy.log has no error entries for the requests:\n" + c.ProcessLog());
        var raw = Lines();
        var rawText = string.Join('\n', raw);
        report["rawLines"] = new JsonArray(raw.Select(l => (JsonNode)l).ToArray());
        E2EArtifacts.Write("caddy-log-scrub.json", report);
        // (1) control: Caddy really wrote each form into caddy.log
        foreach (var form0 in new[] { query, percent, lowerHex, form, rotatedToken, unknownKey, "pa\\\"ss\\\\word-cpm-0815" })
            Assert.Contains(form0, rawText);

        var scrubbed = raw.Select(scrubber.Scrub).ToList();
        report["scrubbedLines"] = new JsonArray(scrubbed.Select(l => (JsonNode)l).ToArray());
        var scrubbedText = string.Join('\n', scrubbed);
        // (2)(3)(4)
        foreach (var leak in new[] { "tok+en", "tok%2Ben", "tok%2ben", "4711-secret", "pa\\\"ss", "word-cpm-0815", rotatedToken, unknownKey })
            Assert.DoesNotContain(leak, scrubbedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token=***", scrubbedText);
        Assert.Contains("key=***", scrubbedText);
        // (5) the rest stays readable and valid JSON; a line without secrets is unchanged
        foreach (var line in scrubbed)
        {
            var n = JsonNode.Parse(line)!;
            Assert.Equal(502, n["status"]!.GetValue<int>());
            Assert.Equal("scrub.test", n["request"]!["host"]!.GetValue<string>());
        }
        var plainLine = raw.Single(l => l.Contains("\"uri\":\"/plain\"", StringComparison.Ordinal));
        Assert.Equal(plainLine, scrubber.Scrub(plainLine)); // sent without the header: nothing to scrub
        Assert.All(scrubbed.Where(l => !l.Contains("\"uri\":\"/plain\"", StringComparison.Ordinal)),
            l => Assert.Contains("\"X-Api-Key\":[\"***\"]", l)); // the zap-escaped password as a whole
        Assert.Equal("{\"level\":\"info\",\"msg\":\"nothing secret\"}", scrubber.Scrub("{\"level\":\"info\",\"msg\":\"nothing secret\"}"));
        // (6) apply errors use the same rules
        Assert.DoesNotContain(unknownKey, c.S.Config.Scrub("Get \"https://www.namesilo.com/api/dnsListRecords?key=" + unknownKey + "\": dial tcp: i/o timeout"));
        Assert.DoesNotContain("4711-secret", c.S.Config.Scrub("url: [https://www.duckdns.org/update?token=" + query + "]"));
        report["passed"] = true;
        E2EArtifacts.Write("caddy-log-scrub.json", report);
    }
}
