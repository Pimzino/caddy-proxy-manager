using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end checks, against the real Caddy binary, of the console features for what Caddy v2.11.8 added: the access
/// log level per request (log_level variable, SiteHost.AccessLogLevels) and the {time.now.rfc3339} placeholder.
/// https://github.com/caddyserver/caddy/releases/tag/v2.11.8
/// </summary>
public sealed class Caddy2118FeaturesE2ETests
{
    /// <summary>
    /// "Log level by path" (SiteHost.AccessLogLevels).
    /// Ways it could fail:
    /// (1) a request that matches a Debug rule still appears in the host's access log;
    /// (2) it disappears from the traffic statistics log too (the statistics sink only records INFO and above), so the
    ///     Traffic page undercounts;
    /// (3) a Warn or Error rule does not change the level of the entry;
    /// (4) the first matching rule does not win (a later vars handler overwrites an earlier one);
    /// (5) requests that match no rule change level;
    /// (6) matching is case-sensitive, although Caddy's path matcher and the docs say it is not;
    /// (7) Caddy rejects the generated configuration;
    /// (8) an older Caddy gets the rules without a warning (it ignores the variable, so the field silently does nothing);
    /// (9) the rules are generated although the host has no access log;
    /// (10) the API stores rules Caddy cannot use: an empty path, a path with a space or without a leading / or *, the
    ///      same path twice (in another case), more than the limit, or an unknown level; (11) a padded path is stored
    ///      untrimmed, or "accessLogLevels": null fails instead of meaning no rules.
    /// </summary>
    [CaddyFact]
    public async Task Access_log_levels_by_path_set_the_entry_level_and_keep_requests_in_statistics()
    {
        var report = E2EArtifacts.Report(nameof(Access_log_levels_by_path_set_the_entry_level_and_keep_requests_in_statistics));
        using var c = new LiveCaddy();
        var host = new SiteHost
        {
            Kind = HostKind.Response, Domains = ["lvl.test"], Tls = TlsMode.None, ResponseStatus = 200, ResponseBody = "ok", AccessLog = true,
            AccessLogLevels =
            [
                new AccessLogLevelRule { Path = "/api/private/*", Level = AccessLogLevel.Error },
                new AccessLogLevelRule { Path = "/api/*", Level = AccessLogLevel.Warn },
                new AccessLogLevelRule { Path = "/healthz", Level = AccessLogLevel.Debug },
                new AccessLogLevelRule { Path = "*.PHP", Level = AccessLogLevel.Warn },
            ],
        };
        c.Add(host);
        await c.StartAsync();
        var apply = await c.ApplyAsync(); // (7)
        Assert.DoesNotContain(apply.Warnings, w => w.Contains("log level", StringComparison.OrdinalIgnoreCase));

        var hostLog = Path.Combine(c.S.Paths.AccessLogDir, "lvl.test.log");
        string[] targets = ["/healthz", "/HEALTHZ", "/api/x", "/api/private/y", "/Index.php", "/other"];
        foreach (var t in targets) Assert.Equal(200, (await c.HttpAsync("lvl.test", t)).Status);
        await Wait.Until(() => Task.FromResult(Uris(hostLog).ContainsKey("/other") && Uris(c.S.Paths.StatsLogFile).ContainsKey("/HEALTHZ")),
            TimeSpan.FromSeconds(10), () => "host log:\n" + string.Join('\n', Lines(hostLog)) + "\nstats:\n" + string.Join('\n', Lines(c.S.Paths.StatsLogFile)));
        var inHostLog = Uris(hostLog);
        var inStats = Uris(c.S.Paths.StatsLogFile);
        report["hostLogLevels"] = new JsonObject(inHostLog.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        report["statsLevels"] = new JsonObject(inStats.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

        Assert.False(inHostLog.ContainsKey("/healthz"));                 // (1)
        Assert.False(inHostLog.ContainsKey("/HEALTHZ"));                 // (1)(6)
        Assert.Equal("debug", inStats["/healthz"]);                      // (2)
        Assert.Equal("debug", inStats["/HEALTHZ"]);                      // (2)(6)
        Assert.Equal("warn", inHostLog["/api/x"]);                       // (3)
        Assert.Equal("error", inHostLog["/api/private/y"]);              // (3)(4)
        Assert.Equal("warn", inHostLog["/Index.php"]);                   // (6)
        Assert.Equal("info", inHostLog["/other"]);                       // (5)
        Assert.Equal(targets.Order(StringComparer.Ordinal), inStats.Keys.Order(StringComparer.Ordinal)); // (2) every request counted

        // (8) a Caddy that does not know the variable gets no rules, and a warning.
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]) with { InstalledVersion = "v2.11.7" });
        report["warningsV2117"] = new JsonArray(old.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.DoesNotContain("\"log_level\"", old.Config.ToJsonString());
        Assert.Contains(old.Warnings, w => w.Contains("lvl.test") && w.Contains(CaddyConfigGenerator.LogLevelMinVersion));
        // (9)
        host.AccessLog = false;
        var off = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]));
        Assert.DoesNotContain("\"log_level\"", off.Config.ToJsonString());

        // (10) through the API.
        await using var api = ApiHost.Start(installBinary: true);
        object Body(object rules) => new { kind = "response", domains = new[] { "lvl-api.test" }, tls = "none", responseStatus = 200, accessLog = true, accessLogLevels = rules };
        var refused = new JsonArray();
        report["refusedByApi"] = refused;
        async Task Refused(string what, object rules)
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body(rules));
            var text = await r.Content.ReadAsStringAsync();
            refused.Add(new JsonObject { ["case"] = what, ["status"] = (int)r.StatusCode, ["response"] = text.Length > 300 ? text[..300] : text });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Empty(api.Store.Col<SiteHost>().FindAll());
        }
        await Refused("empty path", new[] { new { path = " ", level = "debug" } });
        await Refused("path with a space", new[] { new { path = "/a b", level = "debug" } });
        await Refused("path without a leading / or *", new[] { new { path = "healthz", level = "debug" } });
        await Refused("same path twice", new[] { new { path = "/Healthz", level = "debug" }, new { path = "/healthz", level = "warn" } });
        await Refused("too many rules", Enumerable.Range(0, CaddyConfigGenerator.MaxAccessLogLevelRules + 1).Select(i => new { path = "/p" + i, level = "debug" }).ToArray());
        await Refused("unknown level", new[] { new { path = "/healthz", level = "verbose" } });
        var ok = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body(new[] { new { path = " /healthz ", level = "debug" }, new { path = "*", level = "warn" } }));
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var stored = api.Store.Col<SiteHost>().FindAll().Single().AccessLogLevels;
        Assert.Equal(["/healthz:Debug", "*:Warn"], stored.Select(r => $"{r.Path}:{r.Level}")); // (11)
        var none = await api.SendAsync(HttpMethod.Post, "/api/hosts",
            JsonNode.Parse("""{"kind":"response","domains":["lvl-null.test"],"tls":"none","responseStatus":200,"accessLog":true,"accessLogLevels":null}"""));
        Assert.True(none.StatusCode == HttpStatusCode.OK, await none.Content.ReadAsStringAsync()); // (11)
        Assert.Empty(api.Store.Col<SiteHost>().FindAll().Single(x => x.Domains.Contains("lvl-null.test")).AccessLogLevels);

        E2EArtifacts.Write("access-log-levels.json", report);
    }

    /// <summary>
    /// Caddyfile import of the log_level directive (Caddy v2.11.8), through the API and the real `caddy adapt`.
    /// Ways it could fail: (1) the routes stay raw advanced routes instead of becoming the host's rules; (2) the order is
    /// kept as in the Caddyfile, although Caddy lets the LAST matching log_level win and the host's rules use the first
    /// match, so an overlapping path gets another level; (3) a named matcher with several paths loses all but one;
    /// (4) a log_level without a matcher is lost (it applies to every request: "*"); (5) the drafts are refused when
    /// committed.
    /// </summary>
    [CaddyFact]
    public async Task Caddyfile_log_level_directives_import_as_log_level_rules()
    {
        var report = E2EArtifacts.Report(nameof(Caddyfile_log_level_directives_import_as_log_level_rules));
        await using var api = ApiHost.Start(installBinary: true);
        const string caddyfile = """
            lvl.test {
            	log
            	log_level INFO
            	@probe path /healthz /ready
            	log_level @probe DEBUG
            	log_level /api/* warn
            	log_level /api/private/* ERROR
            	reverse_proxy 127.0.0.1:8080
            }
            """;
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/config/caddyfile/import") { Content = JsonContent.Create(new { caddyfile }, options: JsonDefaults.Api) };
        req.Headers.Add("X-Test-Role", "admin");
        var resp = await api.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, text);
        var raw = JsonNode.Parse(text)!.AsObject();
        report["import"] = raw.DeepClone();
        var draft = raw["drafts"]!.Deserialize<List<SiteHost>>(JsonDefaults.Api)!.Single();
        Assert.True(draft.AccessLog);
        Assert.Null(draft.AdvancedRoutesJson); // (1)
        // (2)(3)(4): the broadest rule (INFO for "*") comes first in the Caddyfile and so wins last in the host's list.
        Assert.Equal(["/api/private/*:Error", "/api/*:Warn", "/healthz:Debug", "/ready:Debug", "*:Info"], draft.AccessLogLevels.Select(r => $"{r.Path}:{r.Level}"));

        var commit = await api.Client.PostAsJsonAsync("/api/config/caddyfile/import/commit", new { hosts = raw["drafts"] }, JsonDefaults.Api); // (5)
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        Assert.Equal(5, api.Store.Col<SiteHost>().FindAll().Single().AccessLogLevels.Count);
        E2EArtifacts.Write("caddyfile-log-level-import.json", report);
    }

    /// <summary>
    /// {time.now.rfc3339} (new in v2.11.8) in the fields that take Caddy placeholders: a response header and the body
    /// of a fixed response.
    /// Ways it could fail: (1) the console escapes it as literal text, so the client gets "{time.now.rfc3339}";
    /// (2) Caddy replaces it with something that is not an RFC 3339 timestamp, or with nothing; (3) the time is not now.
    /// </summary>
    [CaddyFact]
    public async Task Rfc3339_time_placeholder_is_replaced_in_headers_and_responses()
    {
        var report = E2EArtifacts.Report(nameof(Rfc3339_time_placeholder_is_replaced_in_headers_and_responses));
        using var c = new LiveCaddy();
        c.Add(new SiteHost
        {
            Kind = HostKind.Response, Domains = ["time.test"], Tls = TlsMode.None, ResponseStatus = 200, ResponseBody = "now={time.now.rfc3339}",
            ResponseHeaders = [new HeaderOp { Action = HeaderAction.Set, Name = "X-Generated", Value = "{time.now.rfc3339}" }],
        });
        await c.StartAsync();
        await c.ApplyAsync();
        var r = await c.HttpAsync("time.test", "/");
        var header = r.Header("X-Generated");
        report["header"] = header;
        report["body"] = r.Body;
        Assert.Equal(200, r.Status);
        var rfc3339 = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$");
        foreach (var value in new[] { header, r.Body.Trim()["now=".Length..] })
        {
            Assert.NotNull(value);
            Assert.Matches(rfc3339, value);                                                           // (1)(2)
            Assert.InRange(DateTimeOffset.Parse(value!), DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(2)); // (3)
        }
        E2EArtifacts.Write("time-rfc3339-placeholder.json", report);
    }

    /// <summary>Request URI → level of its entries in a JSON access log.</summary>
    private static Dictionary<string, string> Uris(string file)
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
