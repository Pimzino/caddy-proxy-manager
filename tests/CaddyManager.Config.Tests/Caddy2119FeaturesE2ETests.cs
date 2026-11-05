using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end checks, against the real Caddy binary, of what Caddy v2.11.9 changed for the console: the time format of
/// plain-text directory listings (browse plain_time_format, SiteHost.BrowsePlainTimeFormat), path matchers with
/// placeholders (caddy PR #8343), the Caddyfile's refusal of conflicting client_auth (#8306) and try_files entries that
/// match a directory only when they end in "/" (#8352).
/// https://github.com/caddyserver/caddy/releases/tag/v2.11.9
/// </summary>
public sealed class Caddy2119FeaturesE2ETests
{
    /// <summary>
    /// "Time format of plain-text listings" (SiteHost.BrowsePlainTimeFormat).
    /// Ways it could fail:
    /// (1) the format is not passed to Caddy, so the listing still shows Caddy's "January 2, 2006 at 15:04:05";
    /// (2) Caddy rejects the generated configuration;
    /// (3) the time written is not the file's modification time (zone or layout mix-up);
    /// (4) a host without a format loses Caddy's default, or the HTML listing changes;
    /// (5) an older Caddy, which refuses the unknown field and with it the whole configuration, gets it anyway, or gets
    ///     no warning;
    /// (6) the API stores a format Caddy would print literally ("YYYY-MM-DD"), one with a tab (it would break the
    ///     tab-separated listing) or one that is too long; (7) a padded format is stored untrimmed, or a blank one is
    ///     not stored as "no format";
    /// (8) the Caddyfile import drops plain_time_format, or drops other browse options without a warning.
    /// </summary>
    [CaddyFact]
    public async Task Plain_text_listings_use_the_configured_time_format()
    {
        var report = E2EArtifacts.Report(nameof(Plain_text_listings_use_the_configured_time_format));
        using var c = new LiveCaddy();
        var root = c.S.Env.WebRoot();
        var file = Path.Combine(root, "report.csv");
        File.WriteAllText(file, "a,b\n");
        var changed = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, changed);
        const string layout = "2006-01-02T15:04:05Z07:00";
        var host = new SiteHost { Kind = HostKind.Static, Domains = ["fmt.test"], Tls = TlsMode.None, RootPath = root, Browse = true, BrowsePlainTimeFormat = layout, Compression = false };
        c.Add(host);
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["plain.test"], Tls = TlsMode.None, RootPath = root, Browse = true, Compression = false });
        await c.StartAsync();
        var apply = await c.ApplyAsync(); // (2)
        Assert.DoesNotContain(apply.Warnings, w => w.Contains("plain-text listings"));

        var formatted = await c.HttpAsync("fmt.test", "/", ("Accept", "text/plain"));
        var standard = await c.HttpAsync("plain.test", "/", ("Accept", "text/plain"));
        var html = await c.HttpAsync("fmt.test", "/");
        report["formattedListing"] = formatted.Body;
        report["defaultListing"] = standard.Body;
        Assert.Equal(200, formatted.Status);
        var line = formatted.Body.Split('\n').Single(l => l.StartsWith("report.csv\t"));
        var written = line.Split('\t')[2];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(Z|[+-]\d{2}:\d{2})$", written);    // (1)
        Assert.Equal(new DateTimeOffset(changed), DateTimeOffset.Parse(written));                  // (3)
        var standardLine = standard.Body.Split('\n').Single(l => l.StartsWith("report.csv\t"));
        Assert.Matches(@"^[A-Z][a-z]+ \d{1,2}, \d{4} at \d{2}:\d{2}:\d{2}$", standardLine.Split('\t')[2]); // (4)
        Assert.Equal(200, html.Status);
        Assert.Contains("<html", html.Body, StringComparison.OrdinalIgnoreCase);                  // (4) the same HTML as without a format
        static string NoNonces(string body) => Regex.Replace(body, "nonce=\"[^\"]*\"", "");
        Assert.Equal(NoNonces((await c.HttpAsync("plain.test", "/")).Body), NoNonces(html.Body));

        // (5)
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]) with { InstalledVersion = "v2.11.8" });
        report["warningsV2118"] = new JsonArray(old.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.DoesNotContain("plain_time_format", old.Config.ToJsonString());
        Assert.Contains(old.Warnings, w => w.Contains("fmt.test") && w.Contains(CaddyConfigGenerator.PlainTimeFormatMinVersion));

        // (6)(7) through the API.
        await using var api = ApiHost.Start(installBinary: true);
        object Body(string domain, string? format) => new { kind = "static", domains = new[] { domain }, tls = "none", rootPath = root, browse = true, browsePlainTimeFormat = format };
        var refused = new JsonArray();
        report["refusedByApi"] = refused;
        foreach (var (what, format) in new[] { ("no element of the reference time", "YYYY-MM-DD"), ("a tab", "2006\t01"), ("too long", "2006-01-02 " + new string('x', CaddyConfigGenerator.MaxPlainTimeFormatLength)) })
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body("refused.test", format));
            var text = await r.Content.ReadAsStringAsync();
            refused.Add(new JsonObject { ["case"] = what, ["status"] = (int)r.StatusCode, ["response"] = text.Length > 300 ? text[..300] : text });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("browsePlainTimeFormat", text);
            Assert.Empty(api.Store.Col<SiteHost>().FindAll());
        }
        foreach (var (domain, format) in new[] { ("padded.test", "  02/01/2006 15:04 MST "), ("blank.test", "   ") })
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body(domain, format));
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
        var stored = api.Store.Col<SiteHost>().FindAll().ToDictionary(h => h.Domains[0], h => h.BrowsePlainTimeFormat);
        Assert.Equal("02/01/2006 15:04 MST", stored["padded.test"]); // (7)
        Assert.Null(stored["blank.test"]);

        // (8) Caddyfile import, through the API and the real `caddy adapt`.
        const string caddyfile = """
            files.test {
            	root * C:\Sites\files
            	file_server {
            		browse {
            			plain_time_format "2006-01-02 15:04"
            			file_limit 500
            		}
            	}
            }
            """;
        var imported = await api.SendAsync(HttpMethod.Post, "/api/config/caddyfile/import", new { caddyfile });
        var importText = await imported.Content.ReadAsStringAsync();
        Assert.True(imported.StatusCode == HttpStatusCode.OK, importText);
        var raw = JsonNode.Parse(importText)!.AsObject();
        report["import"] = raw.DeepClone();
        var draft = raw["drafts"]!.Deserialize<List<SiteHost>>(JsonDefaults.Api)!.Single();
        Assert.True(draft.Browse);
        Assert.Equal("2006-01-02 15:04", draft.BrowsePlainTimeFormat);
        Assert.Contains(raw["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("'file_limit'"));

        E2EArtifacts.Write("browse-plain-time-format.json", report);
    }

    /// <summary>
    /// Placeholders in the path of a "Log level by path" rule and of a location (caddy PR #8343).
    /// Ways it could fail:
    /// (1) Caddy lower-cases the pattern before replacing it, so {env.NAME} reads another (missing) variable and the
    ///     rule never matches (Caddy up to v2.11.8);
    /// (2) the replaced value is compared case-sensitively, so a request in other letter case is missed;
    /// (3) a location whose path has a placeholder never gets requests, or Strip path prefix leaves the prefix;
    /// (4) requests that do not match go to the location or change level;
    /// (5) the generator warns with v2.11.9, or says nothing for a Caddy that cannot match such paths.
    /// </summary>
    [CaddyFact]
    public async Task Path_placeholders_match_in_log_level_rules_and_locations()
    {
        var report = E2EArtifacts.Report(nameof(Path_placeholders_match_in_log_level_rules_and_locations));
        // Caddy inherits the test process environment (CaddyProcess). Names unique to this test.
        Environment.SetEnvironmentVariable("CPM_E2E_2119_PROBE", "Probe/Ready");
        Environment.SetEnvironmentVariable("CPM_E2E_2119_API", "Backend");
        await using var main = await EchoUpstream.StartAsync();
        await using var backend = await EchoUpstream.StartAsync();
        using var c = new LiveCaddy();
        var host = Build.Proxy("ph.test", main.Port);
        host.AccessLog = true;
        host.AccessLogLevels = [new AccessLogLevelRule { Path = "/{env.CPM_E2E_2119_PROBE}", Level = AccessLogLevel.Warn }];
        host.Locations = [new ProxyLocation { Path = "/{env.CPM_E2E_2119_API}", StripPrefix = true, Upstreams = [new Upstream { Host = "127.0.0.1", Port = backend.Port }] }];
        c.Add(host);
        await c.StartAsync();
        var apply = await c.ApplyAsync();
        report["warnings"] = new JsonArray(apply.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.DoesNotContain(apply.Warnings, w => w.Contains("placeholders"));                     // (5)

        var hostLog = Path.Combine(c.S.Paths.AccessLogDir, "ph.test.log");
        string[] targets = ["/Probe/Ready", "/probe/READY", "/other"];
        foreach (var t in targets) Assert.Equal(200, (await c.HttpAsync("ph.test", t)).Status);
        var viaLocation = await c.HttpAsync("ph.test", "/backend/items?x=1");
        var notLocation = await c.HttpAsync("ph.test", "/backends");
        report["viaLocation"] = viaLocation.Body;
        report["notLocation"] = notLocation.Body;
        await Wait.Until(() => Task.FromResult(Levels(hostLog).ContainsKey("/other")), TimeSpan.FromSeconds(10), () => string.Join('\n', Lines(hostLog)));
        var levels = Levels(hostLog);
        report["levels"] = new JsonObject(levels.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        Assert.Equal("warn", levels["/Probe/Ready"]);                                               // (1)
        Assert.Equal("warn", levels["/probe/READY"]);                                               // (2)
        Assert.Equal("info", levels["/other"]);                                                     // (4)
        Assert.Contains("path=/items?x=1\n", viaLocation.Body);                                     // (3)
        Assert.Contains("host=ph.test", viaLocation.Body);
        Assert.Contains("path=/backends\n", notLocation.Body);                                      // (4) the default upstream
        Assert.Contains("path=/other\n", (await c.HttpAsync("ph.test", "/other")).Body);

        // (5) a Caddy before v2.11.9 gets the routes, and a warning for each path with a placeholder.
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]) with { InstalledVersion = "v2.11.8" });
        report["warningsV2118"] = new JsonArray(old.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.Equal(2, old.Warnings.Count(w => w.Contains("ph.test") && w.Contains(CaddyConfigGenerator.PathPlaceholderMinVersion)));
        E2EArtifacts.Write("path-placeholders.json", report);
    }

    /// <summary>
    /// Caddyfiles whose site blocks for the same name set different client_auth (caddy PR #8306).
    /// Ways it could fail: (1) the import (or the adapt of Caddyfile mode) accepts the Caddyfile, so one block's client
    /// certificate requirement is silently dropped; (2) Caddy's reason does not reach the user; (3) drafts or hosts are
    /// created anyway; (4) the same client_auth in both blocks, or in only one block, is refused too.
    /// </summary>
    [CaddyFact]
    public async Task Caddyfile_with_conflicting_client_auth_is_refused_with_caddys_reason()
    {
        var report = E2EArtifacts.Report(nameof(Caddyfile_with_conflicting_client_auth_is_refused_with_caddys_reason));
        await using var api = ApiHost.Start(installBinary: true);
        static string Caddyfile(string first, string second) => $$"""
            mtls.test {
            	{{first}}
            	reverse_proxy /public/* 127.0.0.1:8080
            }
            mtls.test:443 {
            	{{second}}
            	reverse_proxy /admin* 127.0.0.1:8081
            }
            """;
        const string request = "tls {\n\t\tclient_auth {\n\t\t\tmode request\n\t\t}\n\t}";
        const string require = "tls {\n\t\tclient_auth {\n\t\t\tmode require\n\t\t}\n\t}";

        var results = new JsonObject();
        report["results"] = results;
        foreach (var url in new[] { "/api/config/caddyfile/import", "/api/config/caddyfile/adapt" })
        {
            var r = await api.SendAsync(HttpMethod.Post, url, new { caddyfile = Caddyfile(request, require) });
            var text = await r.Content.ReadAsStringAsync();
            results[url + " conflicting"] = new JsonObject { ["status"] = (int)r.StatusCode, ["response"] = text };
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);                        // (1)
            Assert.Contains("conflicting client auth configuration", text);                         // (2)
            Assert.DoesNotContain("drafts", text);                                                  // (3)
        }
        Assert.Empty(api.Store.Col<SiteHost>().FindAll());                                          // (3)
        foreach (var (what, first, second) in new[] { ("same in both", require, require), ("only in one", "", require) })
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/config/caddyfile/adapt", new { caddyfile = Caddyfile(first, second) });
            var text = await r.Content.ReadAsStringAsync();
            results[what] = new JsonObject { ["status"] = (int)r.StatusCode };
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{what}: {text}");                      // (4)
        }
        E2EArtifacts.Write("caddyfile-client-auth-conflict.json", report);
    }

    /// <summary>
    /// try_files matches a directory only through an entry that ends in "/" (caddy PR #8352), which the single-page
    /// application fallback relies on.
    /// Ways it could fail: (1) "/sub/" with its own index.html gets the SPA index instead of the folder's index;
    /// (2) deep links stop falling back to the SPA index; (3) the control: without the "{path}/" entry, v2.11.9 sends
    /// "/sub/" to the SPA index. If the control stops reproducing, Caddy changed again and the docs (troubleshooting,
    /// try_files in Caddyfiles) must be checked.
    /// </summary>
    [CaddyFact]
    public async Task Spa_fallback_serves_folder_indexes_with_directory_only_try_files_entries()
    {
        var report = E2EArtifacts.Report(nameof(Spa_fallback_serves_folder_indexes_with_directory_only_try_files_entries));
        using var c = new LiveCaddy();
        var root = c.S.Env.WebRoot();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "index.html"), "spa index");
        File.WriteAllText(Path.Combine(root, "sub", "index.html"), "sub index");
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["spa.test"], Tls = TlsMode.None, RootPath = root, SpaFallback = true, Compression = false });
        await c.StartAsync();
        await c.ApplyAsync();
        Assert.Equal("sub index", (await c.HttpAsync("spa.test", "/sub/")).Body);                   // (1)
        Assert.Equal("spa index", (await c.HttpAsync("spa.test", "/deep/route")).Body);             // (2)

        var control = await c.RunningConfigAsync();
        var tryFiles = control.ToJsonString();
        foreach (var m in JsonWalk.Descendants(control).OfType<JsonObject>().Where(o => o["try_files"] is JsonArray))
            m["try_files"] = new JsonArray("{http.request.uri.path}", "/index.html");
        Assert.NotEqual(tryFiles, control.ToJsonString());
        await c.LoadRawAsync(control);
        var withoutSlashEntry = await c.HttpAsync("spa.test", "/sub/");
        report["controlSubWithoutSlashEntry"] = withoutSlashEntry.Body;
        Assert.Equal("spa index", withoutSlashEntry.Body);                                          // (3)
        E2EArtifacts.Write("spa-try-files-directories.json", report);
    }

    /// <summary>Request URI → level of its entries in a JSON access log.</summary>
    private static Dictionary<string, string> Levels(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in Lines(file))
            if (JsonNode.Parse(l) is JsonObject o && o["request"]?["uri"]?.GetValue<string>() is { } uri)
                map[uri] = o["level"]!.GetValue<string>();
        return map;
    }

    private static List<string> Lines(string file)
    {
        if (!File.Exists(file)) return [];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
