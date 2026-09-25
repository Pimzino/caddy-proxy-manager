using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Secrets never leak through Caddy's error messages: with a real Caddy that includes dns.providers.cloudflare, an API
/// token in an invalid format makes /load fail with an error that quotes the token; the manager's API response, stored
/// revision and raised event carry "***" instead. Artifact: secret-scrub.json.
/// </summary>
public sealed class SecretScrubE2ETests
{
    /// <summary>
    /// Ways it could fail:
    /// (1) Caddy's error no longer contains the token (then the scrubbing is untested: the control asserts it does);
    /// (2) the 422 detail, the failed ConfigRevision's error or the event details contain the token;
    /// (3) the token reaches viewers through the config preview;
    /// (4) the failed change is not rolled back (the host stays), or the stored secret is lost.
    /// </summary>
    [CloudflareCaddyFact]
    public async Task Cloudflare_token_in_caddy_errors_is_replaced_before_it_reaches_api_revisions_and_events()
    {
        var report = E2EArtifacts.Report(nameof(Cloudflare_token_in_caddy_errors_is_replaced_before_it_reaches_api_revisions_and_events));
        var caddyBin = E2ETools.CaddyCloudflare().Path!;
        report["caddyBinary"] = E2ETools.Run(caddyBin, "version")?.Trim();
        // Not a Cloudflare token format (^[A-Za-z0-9_-]{35,50}$ or cfut_/cfat_...): provisioning fails and quotes it.
        const string token = "not a token! cpm-secret-4711";
        try
        {
            await using var api = ApiHost.Start(installBinary: true, caddyBinary: caddyBin);
            using var caddy = new CaddyProcess(api.Env.Paths);
            var admin = api.Store.GetSettings<CaddySettings>().AdminListen;
            var adminClient = api.App.Services.GetRequiredService<CaddyAdminClient>().ForAddress(admin);
            await Wait.Until(() => adminClient.IsReachableAsync(), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + caddy.Output);

            var put = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new
            {
                httpPort = Net.FreeTcpPort(),
                httpsPort = Net.FreeTcpPort(),
                acmeCa = "letsEncryptStaging",
                dnsProvider = "cloudflare",
                dnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = token },
            });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.DoesNotContain(token, await put.Content.ReadAsStringAsync());

            var post = await api.SendAsync(HttpMethod.Post, "/api/hosts", new
            {
                kind = "response", domains = new[] { "scrub.example.com" }, tls = "acme", acmeChallenge = "dns", responseStatus = 200,
            });
            var postText = await post.Content.ReadAsStringAsync();
            report["apiStatus"] = (int)post.StatusCode;
            report["apiResponse"] = JsonNode.Parse(postText);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
            Assert.DoesNotContain("cpm-secret-4711", postText); // (2)
            Assert.Contains("***", postText);

            var revisions = JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, "/api/config/revisions")).Content.ReadAsStringAsync())!.AsArray();
            var failed = revisions.First(r => !r!["success"]!.GetValue<bool>())!;
            report["revisionError"] = failed["error"]!.DeepClone();
            Assert.DoesNotContain("cpm-secret-4711", failed["error"]!.GetValue<string>());
            Assert.Contains("***", failed["error"]!.GetValue<string>());

            var details = api.Events.Details.Where(d => d is not null).ToList();
            report["eventDetails"] = new JsonArray(details.Select(d => (JsonNode)d!).ToArray());
            Assert.Contains(details, d => d!.Contains("***", StringComparison.Ordinal));
            Assert.DoesNotContain(details, d => d!.Contains("cpm-secret-4711", StringComparison.Ordinal));

            // (1) control: the rejected config (admins read revisions unredacted) loaded straight into Caddy — the raw
            // error really contains the token, so the assertions above are meaningful.
            var revision = JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, $"/api/config/revisions/{failed["id"]!.GetValue<string>()}")).Content.ReadAsStringAsync())!;
            var raw = await Assert.ThrowsAsync<CaddyAdminException>(() => adminClient.LoadAsync(revision["json"]!.GetValue<string>()));
            report["rawCaddyErrorContainsToken"] = raw.Message.Contains(token, StringComparison.Ordinal);
            Assert.Contains(token, raw.Message);

            // (3) viewers: the failed revision (holding the config with the token) is redacted for them
            var viewerRevision = await (await api.SendAsync(HttpMethod.Get, $"/api/config/revisions/{failed["id"]!.GetValue<string>()}", role: "viewer")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("cpm-secret-4711", viewerRevision);
            Assert.Contains("cloudflare", viewerRevision);
            // (4) rolled back, secret kept
            Assert.Empty(JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, "/api/hosts")).Content.ReadAsStringAsync())!.AsArray());
            var settings = JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, "/api/settings/caddy", role: "viewer")).Content.ReadAsStringAsync())!;
            Assert.Equal(["api_token"], settings["dnsProviderSecretFields"]!.AsArray().Select(x => x!.GetValue<string>()));
            report["passed"] = true;
        }
        finally
        {
            E2EArtifacts.Write("secret-scrub.json", report);
        }
    }
}
