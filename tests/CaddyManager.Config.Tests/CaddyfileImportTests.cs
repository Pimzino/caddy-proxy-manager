using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Import;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>Caddyfile import: real Caddyfiles adapted by the real binary, mapped to drafts and committed.</summary>
public sealed class CaddyfileImportTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    private const string Everything = """
        {
        	email ops@example.com
        }

        app.example.com, www.app.example.com {
        	encode gzip zstd
        	header {
        		X-Frame-Options DENY
        		-Server
        		+X-Extra one
        	}
        	reverse_proxy 10.0.0.5:8080 10.0.0.6:8080 {
        		lb_policy least_conn
        		health_uri /healthz
        		health_interval 10s
        		health_timeout 2s
        		health_status 200
        		header_up Host {upstream_hostport}
        		header_up X-Real-IP {remote_host}
        	}
        	log
        }

        secure.example.com {
        	tls internal
        	reverse_proxy https://backend.internal:8443 {
        		transport http {
        			tls_insecure_skip_verify
        		}
        	}
        	handle_path /api/* {
        		reverse_proxy 10.0.0.9:9000
        	}
        	basic_auth /admin/* {
        		bob $2a$14$Zkx19XLiW6VYouLHR5NmfOFU0z2GTNmpkT/5qqR7hx4IjWJPDhjvG
        	}
        }

        http://old.example.com {
        	redir https://new.example.com{uri} permanent
        }

        static.example.com {
        	root * /srv/www
        	try_files {path} /index.html
        	file_server browse
        }

        maint.example.com {
        	respond "Down for maintenance" 503
        }

        custom.example.com {
        	tls /etc/ssl/custom.pem /etc/ssl/custom.key
        	reverse_proxy localhost:3000
        }

        php.example.com {
        	root * /srv/php
        	php_fastcgi 127.0.0.1:9000
        	file_server
        }

        :8080 {
        	respond "port only"
        }
        """;

    private const string Mixed = """
        (common) {
        	encode zstd
        }

        http://plain.example.com, plain.example.com {
        	import common
        	reverse_proxy https://intranet.corp.test {
        		lb_policy cookie
        	}
        }

        *.apps.example.com {
        	tls internal
        	handle /health {
        		respond "ok" 200
        	}
        	handle {
        		reverse_proxy 10.1.0.1:80 {
        			header_up -X-Forwarded-Host
        			header_up X-Env prod
        		}
        	}
        	redir /old /new 302
        }

        static-only.example.com {
        	respond 204
        }
        """;

    private static SiteHost Draft(CaddyfileImportResult r, string domain) => r.Drafts.Single(d => d.Domains.Contains(domain));

    private static async Task<(CaddyfileImportResult Result, JsonObject Raw)> ImportAsync(ApiHost api, string caddyfile, string role = "admin")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/config/caddyfile/import") { Content = JsonContent.Create(new { caddyfile }, options: Json) };
        req.Headers.Add("X-Test-Role", role);
        var resp = await api.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, resp.StatusCode + " " + text);
        var raw = JsonNode.Parse(text)!.AsObject();
        var drafts = raw["drafts"]!.Deserialize<List<SiteHost>>(Json)!;
        var unmapped = raw["unmapped"]!.Deserialize<List<string>>(Json)!;
        var warnings = raw["warnings"]!.Deserialize<List<string>>(Json)!;
        return (new CaddyfileImportResult(drafts, unmapped, warnings), raw);
    }

    [CaddyFact]
    public async Task Import_maps_a_feature_rich_caddyfile()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var (r, _) = await ImportAsync(api, Everything);
        Assert.Equal(7, r.Drafts.Count);
        Assert.Empty(api.Store.Col<SiteHost>().FindAll()); // nothing saved

        var app = Draft(r, "app.example.com");
        Assert.Equal(HostKind.Proxy, app.Kind);
        Assert.Equal(["app.example.com", "www.app.example.com"], app.Domains);
        Assert.Equal(TlsMode.Acme, app.Tls);
        Assert.Equal(["10.0.0.5:8080", "10.0.0.6:8080"], app.Upstreams.Select(u => $"{u.Host}:{u.Port}").ToArray());
        Assert.Equal(LoadBalancingPolicy.LeastConn, app.LoadBalancing);
        Assert.True(app.HealthCheck.Enabled);
        Assert.Equal("/healthz", app.HealthCheck.Path);
        Assert.Equal(10, app.HealthCheck.IntervalSeconds);
        Assert.Equal(2, app.HealthCheck.TimeoutSeconds);
        Assert.Equal(200, app.HealthCheck.ExpectStatus);
        Assert.Equal("{upstream}", app.UpstreamHostHeader);
        Assert.Contains(app.RequestHeaders, h => h.Name == "X-Real-Ip" && h.Value == "{http.request.remote.host}");
        Assert.True(app.Compression);
        Assert.True(app.AccessLog);
        Assert.Contains(app.ResponseHeaders, h => h is { Action: HeaderAction.Set, Name: "X-Frame-Options", Value: "DENY" });
        Assert.Contains(app.ResponseHeaders, h => h is { Action: HeaderAction.Delete, Name: "Server" });
        Assert.Contains(app.ResponseHeaders, h => h is { Action: HeaderAction.Add, Name: "X-Extra", Value: "one" });
        Assert.Null(app.AdvancedRoutesJson);

        var secure = Draft(r, "secure.example.com");
        Assert.Equal(TlsMode.Internal, secure.Tls);
        Assert.Equal(UpstreamScheme.Https, secure.Upstreams.Single().Scheme);
        Assert.Equal("backend.internal", secure.Upstreams.Single().Host);
        Assert.True(secure.UpstreamTlsInsecure);
        var loc = secure.Locations.Single();
        Assert.Equal("/api", loc.Path);
        Assert.True(loc.StripPrefix);
        Assert.Equal("10.0.0.9", loc.Upstreams.Single().Host);
        Assert.Contains("\"authentication\"", secure.AdvancedRoutesJson); // basic_auth stays in force
        Assert.Contains("/admin/*", secure.AdvancedRoutesJson);
        Assert.Contains(r.Warnings, w => w.Contains("basic_auth") && w.Contains("access list"));

        var old = Draft(r, "old.example.com");
        Assert.Equal(HostKind.Redirect, old.Kind);
        Assert.Equal(TlsMode.None, old.Tls);
        Assert.Equal("https://new.example.com", old.RedirectTarget);
        Assert.True(old.PreservePath);
        Assert.Equal(301, old.RedirectCode);

        var stat = Draft(r, "static.example.com");
        Assert.Equal(HostKind.Static, stat.Kind);
        Assert.Equal("/srv/www", stat.RootPath);
        Assert.True(stat.Browse);
        Assert.True(stat.SpaFallback);
        Assert.Null(stat.AdvancedRoutesJson);

        var maint = Draft(r, "maint.example.com");
        Assert.Equal(HostKind.Response, maint.Kind);
        Assert.Equal(503, maint.ResponseStatus);
        Assert.Equal("Down for maintenance", maint.ResponseBody);

        var custom = Draft(r, "custom.example.com");
        Assert.Equal(TlsMode.Acme, custom.Tls);
        Assert.Contains("/etc/ssl/custom.pem", custom.Notes);
        Assert.Contains(r.Warnings, w => w.Contains("custom.example.com") && w.Contains("/etc/ssl/custom.key"));

        var php = Draft(r, "php.example.com");
        Assert.Equal(HostKind.Static, php.Kind);
        Assert.Equal("/srv/php", php.RootPath);
        Assert.Contains("fastcgi", php.AdvancedRoutesJson);
        Assert.Contains(r.Unmapped, u => u.StartsWith("php.example.com:") && u.Contains("fastcgi"));

        Assert.Contains(r.Unmapped, u => u.Contains(":8080"));
        Assert.Contains(r.Warnings, w => w.Contains("without host names"));
        Assert.Contains(r.Warnings, w => w.Contains("ACME settings"));
    }

    [CaddyFact]
    public async Task Import_handles_snippets_handle_blocks_wildcards_and_dual_http_https_sites()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var (r, _) = await ImportAsync(api, Mixed);
        Assert.Equal(3, r.Drafts.Count);

        var plain = Draft(r, "plain.example.com");
        Assert.Equal(TlsMode.Acme, plain.Tls);
        Assert.False(plain.ForceHttps); // served on http:// too
        Assert.True(plain.Compression);
        Assert.Equal(UpstreamScheme.Https, plain.Upstreams.Single().Scheme);
        Assert.Equal(443, plain.Upstreams.Single().Port);
        Assert.Equal(LoadBalancingPolicy.Cookie, plain.LoadBalancing);

        var wild = Draft(r, "*.apps.example.com");
        Assert.Equal(TlsMode.Internal, wild.Tls);
        Assert.Equal(HostKind.Proxy, wild.Kind);
        Assert.Equal("10.1.0.1", wild.Upstreams.Single().Host);
        Assert.Contains(wild.RequestHeaders, h => h is { Action: HeaderAction.Delete, Name: "X-Forwarded-Host" });
        Assert.Contains(wild.RequestHeaders, h => h is { Action: HeaderAction.Set, Name: "X-Env", Value: "prod" });
        var advanced = JsonNode.Parse(wild.AdvancedRoutesJson!)!.AsArray();
        Assert.Contains(advanced, a => a!.ToJsonString().Contains("\"/health\""));
        Assert.Contains(advanced, a => a!.ToJsonString().Contains("\"/old\""));

        var only = Draft(r, "static-only.example.com");
        Assert.Equal(HostKind.Response, only.Kind);
        Assert.Equal(204, only.ResponseStatus);
    }

    [CaddyFact]
    public async Task Commit_creates_all_hosts_transactionally_and_rejects_conflicts()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var (_, raw) = await ImportAsync(api, Everything);
        var drafts = raw["drafts"]!.AsArray();

        var commit = await api.Client.PostAsJsonAsync("/api/config/caddyfile/import/commit", new { hosts = drafts }, Json);
        var text = await commit.Content.ReadAsStringAsync();
        Assert.True(commit.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        Assert.Equal(7, body["created"]!.GetValue<int>());
        Assert.True(body["apply"]!["success"]!.GetValue<bool>()); // validated by `caddy validate` (Caddy not running)
        Assert.Equal(7, api.Store.Col<SiteHost>().Count());
        Assert.Equal(7, api.Audit.Entries.Count(e => e.Action == "created" && e.ObjectType == "host"));
        Assert.Contains("secure.example.com", File.ReadAllText(api.Env.Paths.CaddyConfigFile));

        // the same import again: every domain conflicts → 409, nothing created
        var again = await api.Client.PostAsJsonAsync("/api/config/caddyfile/import/commit", new { hosts = drafts }, Json);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("app.example.com", JsonNode.Parse(await again.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>());
        Assert.Equal(7, api.Store.Col<SiteHost>().Count());

        // import review warns about the existing hosts
        var (reviewed, _) = await ImportAsync(api, Everything);
        Assert.Contains(reviewed.Warnings, w => w.Contains("already served by the existing host"));
    }

    [CaddyFact]
    public async Task Commit_validates_every_host_and_rolls_back_when_caddy_rejects()
    {
        await using var api = ApiHost.Start(installBinary: true);
        async Task<HttpResponseMessage> Commit(object hosts) => await api.Client.PostAsJsonAsync("/api/config/caddyfile/import/commit", new { hosts }, Json);

        var invalid = await Commit(new object[]
        {
            new { kind = "response", domains = new[] { "ok.example.com" }, tls = "none" },
            new { kind = "proxy", domains = Array.Empty<string>(), upstreams = new[] { new { host = "127.0.0.1", port = 81 } } },
            new { kind = "static", domains = new[] { "s.example.com" }, rootPath = @"C:\Windows" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!["errors"]!.AsObject();
        Assert.NotNull(errors["hosts[1].domains"]);
        Assert.NotNull(errors["hosts[2].rootPath"]);

        var dup = await Commit(new object[]
        {
            new { kind = "response", domains = new[] { "twice.example.com" }, tls = "none" },
            new { kind = "response", domains = new[] { "twice.example.com" }, tls = "none" },
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var rejected = await Commit(new object[]
        {
            new { kind = "response", domains = new[] { "fine.example.com" }, tls = "none" },
            new { kind = "response", domains = new[] { "broken.example.com" }, tls = "none", advancedRoutesJson = """[{"handle":[{"handler":"no_such_handler"}]}]""" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Empty(api.Store.Col<SiteHost>().FindAll());

        Assert.Equal(HttpStatusCode.BadRequest, (await Commit(Array.Empty<object>())).StatusCode);
    }

    [CaddyFact]
    public async Task Import_is_administrator_only_and_reports_invalid_caddyfiles()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/config/caddyfile/import") { Content = JsonContent.Create(new { caddyfile = "a.test {\n respond hi\n}" }, options: Json) };
        req.Headers.Add("X-Test-Role", "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(req)).StatusCode);
        var commit = new HttpRequestMessage(HttpMethod.Post, "/api/config/caddyfile/import/commit") { Content = JsonContent.Create(new { hosts = Array.Empty<object>() }, options: Json) };
        commit.Headers.Add("X-Test-Role", "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(commit)).StatusCode);

        var bad = await api.Client.PostAsJsonAsync("/api/config/caddyfile/import", new { caddyfile = "a.test {\n not_a_directive\n}\n" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client.PostAsJsonAsync("/api/config/caddyfile/import", new { caddyfile = " " }, Json)).StatusCode);
    }

    [Fact]
    public void Durations_and_unreadable_input()
    {
        Assert.Equal(10, CaddyfileImporter.Seconds(JsonValue.Create(10_000_000_000L)));
        Assert.Equal(90, CaddyfileImporter.Seconds(JsonValue.Create("1m30s")));
        Assert.Equal(1, CaddyfileImporter.Seconds(JsonValue.Create("500ms")));
        Assert.Null(CaddyfileImporter.Seconds(JsonValue.Create("soon")));
        var r = CaddyfileImporter.Import("not json", []);
        Assert.Empty(r.Drafts);
        Assert.NotEmpty(r.Warnings);
    }
}
