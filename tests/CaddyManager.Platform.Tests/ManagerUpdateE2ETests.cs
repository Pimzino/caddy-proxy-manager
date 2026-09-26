// Caddy Proxy Manager self-update check, end to end through the HTTP API (settings → GET/POST /api/system/manager-update,
// the dashboard overview and the background UpdateChecker), with GitHub replaced by a fake that serves real recorded data
// (Fixtures/github-releases-caddy-proxy-manager.json: GET /repos/Pimzino/caddy-proxy-manager/releases with
// application/vnd.github.full+json, v1.1.0 … v1.0.0).
//
// Ways the feature can fail, each checked below:
//  1. The blank repository setting disables the check (old behaviour) instead of meaning the official repository.
//  2. The request goes to /releases/latest (one release, no changelog) or asks for a media type without body_html.
//  3. Drafts or pre-releases (listed by /releases) are offered as updates.
//  4. Releases are listed in GitHub's order or by string comparison instead of by version, or older/equal versions leak
//     into NewerReleases.
//  5. Asset digests ("sha256:…") are dropped or kept with the prefix / upper case, so the UI cannot show the hash.
//  6. body_html is lost, so the UI has to fall back to raw Markdown.
//  7. Every page load hits GitHub (no cache), burning the 60 requests/hour anonymous limit.
//  8. A forced check that hits the rate limit returns 5xx and/or forgets the data it already had.
//  9. A viewer can trigger GitHub requests (POST check is Operator-only).
// 10. A custom repository URL (https://github.com/owner/repo.git) is stored or requested un-normalised.
// 11. Turning checks off still contacts GitHub (the API, the dashboard overview or the background checker).
// 12. The background checker raises the event more than once per version or omits the MSI download link.
// 13. The settings PUT/GET drops CheckManagerUpdates.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Platform.Background;
using CaddyManager.Platform.Binary;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Platform.Tests;

/// <summary>Stands in for api.github.com: records every request and answers with <see cref="Respond"/>.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, string Accept)> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!.ToString(), string.Join(", ", request.Headers.Accept.Select(a => a.ToString()))));
        return Task.FromResult(Respond(request));
    }

    // IHttpClientFactory disposes expired primary handlers; this one lives as long as the test.
    protected override void Dispose(bool disposing) { }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

[Trait("Category", "E2E")]
public class ManagerUpdateE2ETests
{
    private const string OfficialListUrl = "https://api.github.com/repos/Pimzino/caddy-proxy-manager/releases?per_page=30";
    private const string ForkListUrl = "https://api.github.com/repos/contoso/cpm-fork/releases?per_page=30";
    private const string MediaType = "application/vnd.github.full+json";

    /// <summary>The recorded release list plus a newer pre-release and a newer draft that must never be offered.</summary>
    private static string FixtureWithUnstableReleases()
    {
        var list = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-releases-caddy-proxy-manager.json")))!.AsArray();
        JsonNode Unstable(string tag, bool draft, bool pre)
        {
            var r = list[0]!.DeepClone();
            r["tag_name"] = tag;
            r["name"] = $"Caddy Proxy Manager {tag}";
            r["draft"] = draft;
            r["prerelease"] = pre;
            r["html_url"] = $"https://github.com/Pimzino/caddy-proxy-manager/releases/tag/{tag}";
            return r;
        }
        list.Insert(0, Unstable("v9.0.0-beta.1", draft: false, pre: true));
        list.Insert(0, Unstable("v9.1.0", draft: true, pre: false));
        return list.ToJsonString();
    }

    private static async Task<ManagerUpdateInfo> ReadInfo(HttpResponseMessage resp, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ManagerUpdateInfo>(JsonDefaults.Api, ct))!;
    }

    private static JsonObject Summary(ManagerUpdateInfo i) => new()
    {
        ["enabled"] = i.Enabled,
        ["repo"] = i.Repo,
        ["repoIsDefault"] = i.RepoIsDefault,
        ["currentVersion"] = i.CurrentVersion,
        ["updateAvailable"] = i.UpdateAvailable,
        ["latest"] = i.Latest?.Version,
        ["newerReleases"] = new JsonArray(i.NewerReleases.Select(r => (JsonNode)JsonValue.Create(r.Version)!).ToArray()),
        ["checkedAt"] = i.CheckedAt?.ToString("O"),
        ["error"] = i.Error,
    };

    [Fact]
    public async Task ChecksTheOfficialRepositoryAndReportsTheChangelogSinceTheInstalledVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = FixtureWithUnstableReleases();
        var github = new FakeGitHub();
        HttpResponseMessage Ok(HttpRequestMessage req) => req.RequestUri!.AbsolutePath.EndsWith("/releases/latest")
            ? FakeGitHub.Json("""{"tag_name":"v2.11.4","html_url":"https://github.com/caddyserver/caddy/releases/tag/v2.11.4","body":"notes","body_html":"<p>notes</p>"}""")
            : FakeGitHub.Json(fixture);
        github.Respond = Ok;
        await using var api = new PlatformApiHost(12319, s => s.AddHttpClient("default").ConfigurePrimaryHttpMessageHandler(() => github));
        var client = api.Client;
        var report = E2EArtifacts.Report(nameof(ChecksTheOfficialRepositoryAndReportsTheChangelogSinceTheInstalledVersion));
        var steps = new JsonArray();
        report["steps"] = steps;

        // --- settings: blank repository = the official one, checks on by default (13)
        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/settings/binary", ct))!;
        Assert.True(settings["checkManagerUpdates"]!.GetValue<bool>());
        Assert.Null(settings["managerReleaseRepo"]);

        // --- first GET asks GitHub once, at the list endpoint, with body_html (1, 2)
        var info = await ReadInfo(await client.GetAsync("/api/system/manager-update", ct), ct);
        steps.Add(new JsonObject { ["step"] = "GET manager-update (official repo)", ["response"] = Summary(info) });
        var first = Assert.Single(github.Requests);
        Assert.Equal(OfficialListUrl, first.Url);
        Assert.Contains(MediaType, first.Accept);

        Assert.True(info.Enabled);
        Assert.Equal(CaddyBinaryManager.DefaultManagerReleaseRepo, info.Repo);
        Assert.True(info.RepoIsDefault);
        Assert.Equal("https://github.com/Pimzino/caddy-proxy-manager/releases", info.ReleasesUrl);
        Assert.Equal(CaddyBinaryManager.ManagerVersion.TrimStart('v'), info.CurrentVersion);
        Assert.Null(info.Error);
        Assert.NotNull(info.CheckedAt);

        // Newest stable release: v1.1.0 (the injected v9.1.0 draft and v9.0.0-beta.1 pre-release are skipped) (3, 5, 6)
        var latest = info.Latest!;
        Assert.Equal("1.1.0", latest.Version);
        Assert.Equal("Caddy Proxy Manager v1.1.0", latest.Name);
        Assert.Equal("https://github.com/Pimzino/caddy-proxy-manager/releases/tag/v1.1.0", latest.Url);
        Assert.Contains("<table>", latest.NotesHtml);
        Assert.False(string.IsNullOrEmpty(latest.Notes));
        var msi = Assert.Single(latest.Assets, a => a.Name.EndsWith(".msi", StringComparison.Ordinal));
        Assert.Equal("CaddyProxyManager-1.1.0-x64.msi", msi.Name);
        Assert.Equal("df6006f9210d195e96eb7351c925758d5d082672090ca3f5670d510b5e4af462", msi.Sha256);
        Assert.Equal("https://github.com/Pimzino/caddy-proxy-manager/releases/download/v1.1.0/CaddyProxyManager-1.1.0-x64.msi", msi.DownloadUrl);
        Assert.True(msi.Size > 0);
        Assert.Equal(3, latest.Assets.Count);

        // Changelog since the installed version: exactly the stable fixture releases newer than it, newest first (4).
        // The test build is Directory.Build.props' <Version> (1.0.0 today); when that reaches 1.1.0, re-record the fixture.
        string[] stable = ["1.1.0", "1.0.2", "1.0.1", "1.0.0"];
        var expectedNewer = stable.Where(v => CaddyVersion.IsNewer(v, info.CurrentVersion)).ToList();
        Assert.True(expectedNewer.Count >= 2,
            $"Installed version {info.CurrentVersion} is not older than the recorded releases; re-record Fixtures/github-releases-caddy-proxy-manager.json.");
        Assert.Equal(expectedNewer, info.NewerReleases.Select(r => r.Version).ToList());
        Assert.True(info.UpdateAvailable);
        Assert.DoesNotContain(info.NewerReleases, r => r.Version.StartsWith('9'));
        Assert.All(info.NewerReleases, r => Assert.NotNull(r.NotesHtml));

        // --- cached: a second GET and the dashboard overview do not ask GitHub for the manager again (7)
        await ReadInfo(await client.GetAsync("/api/system/manager-update", ct), ct);
        var overview = (await client.GetFromJsonAsync<BinaryOverview>("/api/caddy/binary", JsonDefaults.Api, ct))!;
        Assert.Equal("1.1.0", overview.ManagerLatestVersion);
        Assert.Equal(latest.Url, overview.ManagerLatestUrl);
        Assert.True(overview.ManagerUpdateAvailable);
        Assert.Equal(1, github.Requests.Count(r => r.Url == OfficialListUrl));
        // The Caddy check uses the same media type (its notes get HTML too).
        var caddyRequest = Assert.Single(github.Requests, r => r.Url.EndsWith("/repos/caddyserver/caddy/releases/latest"));
        Assert.Contains(MediaType, caddyRequest.Accept);
        Assert.Equal("<p>notes</p>", overview.Latest?.NotesHtml);

        // --- background checker: one event per version, with the MSI link (12)
        var checker = api.App.Services.GetRequiredService<UpdateChecker>();
        Assert.Equal("1.1.0", await checker.CheckManagerAsync(ct));
        Assert.Equal("1.1.0", await checker.CheckManagerAsync(ct));
        var ev = Assert.Single(api.Events.Events, e => e.Key == "manager-update-available:1.1.0");
        Assert.Equal("updateAvailable", ev.AlertRule);
        Assert.Contains(msi.DownloadUrl, ev.Details);
        Assert.Contains(msi.Sha256!, ev.Details);
        Assert.Contains(latest.Url, ev.Details);
        steps.Add(new JsonObject { ["step"] = "UpdateChecker.CheckManagerAsync x2", ["eventMessage"] = ev.Message, ["eventDetails"] = ev.Details });
        var checkedAt = (await ReadInfo(await client.GetAsync("/api/system/manager-update", ct), ct)).CheckedAt;

        // --- rate limited on a forced check: 200 with the error and the data GitHub gave earlier (8)
        var reset = DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeSeconds();
        github.Respond = _ =>
        {
            var r = FakeGitHub.Json("""{"message":"API rate limit exceeded for 203.0.113.7.","documentation_url":"https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"}""",
                HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return r;
        };
        var before = github.Requests.Count;
        var limited = await ReadInfo(await client.PostAsync("/api/system/manager-update/check", null, ct), ct);
        steps.Add(new JsonObject { ["step"] = "POST check while rate limited (403)", ["response"] = Summary(limited) });
        Assert.Equal(before + 1, github.Requests.Count);
        Assert.Contains("rate limit", limited.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.1.0", limited.Latest?.Version);
        Assert.Equal(expectedNewer, limited.NewerReleases.Select(r => r.Version).ToList());
        Assert.True(limited.UpdateAvailable);
        Assert.Equal(checkedAt, limited.CheckedAt); // time of the last successful answer
        Assert.Contains(api.Audit.Entries, e => e.StartsWith("checked-updates manager") && e.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

        // Not forced while the limit is in effect: no request, the error and the cached data are shown (7, 8)
        before = github.Requests.Count;
        var afterLimit = await ReadInfo(await client.GetAsync("/api/system/manager-update", ct), ct);
        Assert.Equal(before, github.Requests.Count);
        Assert.Contains("rate limit", afterLimit.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.1.0", afterLimit.Latest?.Version);

        // --- viewers can read but not trigger a check (9)
        using (var viewerPost = new HttpRequestMessage(HttpMethod.Post, "/api/system/manager-update/check"))
        {
            viewerPost.Headers.Add("X-Test-Role", "viewer");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(viewerPost, ct)).StatusCode);
        }
        using (var viewerGet = new HttpRequestMessage(HttpMethod.Get, "/api/system/manager-update"))
        {
            viewerGet.Headers.Add("X-Test-Role", "viewer");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(viewerGet, ct)).StatusCode);
        }
        Assert.Equal(before, github.Requests.Count);

        // --- custom repository given as a clone URL: normalised, stored and requested as owner/repo (10)
        github.Respond = Ok;
        settings["managerReleaseRepo"] = "https://github.com/contoso/cpm-fork.git";
        var put = await client.PutAsJsonAsync("/api/settings/binary", settings, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        Assert.Equal("contoso/cpm-fork", saved["managerReleaseRepo"]!.GetValue<string>());
        Assert.True(saved["checkManagerUpdates"]!.GetValue<bool>());
        Assert.Contains(api.Audit.Entries, e => e.Contains("managerReleaseRepo=contoso/cpm-fork") && e.Contains("checkManagerUpdates=True"));
        var fork = await ReadInfo(await client.PostAsync("/api/system/manager-update/check", null, ct), ct);
        steps.Add(new JsonObject { ["step"] = "POST check (contoso/cpm-fork)", ["response"] = Summary(fork) });
        Assert.Equal(ForkListUrl, github.Requests.Last().Url);
        Assert.Contains(MediaType, github.Requests.Last().Accept);
        Assert.Equal("contoso/cpm-fork", fork.Repo);
        Assert.False(fork.RepoIsDefault);
        Assert.Equal("https://github.com/contoso/cpm-fork/releases", fork.ReleasesUrl);
        Assert.Null(fork.Error);
        Assert.Equal("1.1.0", fork.Latest?.Version);

        // --- checks off: nothing asks GitHub for the manager any more (11, 13)
        settings = (await client.GetFromJsonAsync<JsonObject>("/api/settings/binary", ct))!;
        settings["checkManagerUpdates"] = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/binary", settings, ct)).StatusCode);
        settings = (await client.GetFromJsonAsync<JsonObject>("/api/settings/binary", ct))!;
        Assert.False(settings["checkManagerUpdates"]!.GetValue<bool>());
        Assert.Equal("contoso/cpm-fork", settings["managerReleaseRepo"]!.GetValue<string>());
        before = github.Requests.Count;
        var off = await ReadInfo(await client.GetAsync("/api/system/manager-update", ct), ct);
        var offForced = await ReadInfo(await client.PostAsync("/api/system/manager-update/check", null, ct), ct);
        Assert.Null(await checker.CheckManagerAsync(ct));
        overview = (await client.GetFromJsonAsync<BinaryOverview>("/api/caddy/binary", JsonDefaults.Api, ct))!;
        steps.Add(new JsonObject { ["step"] = "GET + POST manager-update with checks off", ["response"] = Summary(offForced) });
        Assert.DoesNotContain(github.Requests.Skip(before), r => r.Url.Contains("/releases?"));
        foreach (var i in new[] { off, offForced })
        {
            Assert.False(i.Enabled);
            Assert.False(i.UpdateAvailable);
            Assert.Null(i.Latest);
            Assert.Empty(i.NewerReleases);
            Assert.Null(i.Error);
            Assert.Equal("contoso/cpm-fork", i.Repo);
        }
        Assert.Null(overview.ManagerLatestVersion);
        Assert.False(overview.ManagerUpdateAvailable);

        report["requests"] = new JsonArray(github.Requests.Select(r => (JsonNode)new JsonObject { ["url"] = r.Url, ["accept"] = r.Accept }).ToArray());
        report["managerListRequests"] = github.Requests.Count(r => r.Url.Contains("/releases?"));
        report["auditEntries"] = new JsonArray(api.Audit.Entries.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        var path = E2EArtifacts.Write("manager-update-check.json", report);
        TestContext.Current.SendDiagnosticMessage($"Artifact: {path}");
    }
}
