using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Backup;
using CaddyManager.Ops.Logs;
using LiteDB;

namespace CaddyManager.Ops.Tests;

public class LogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cpm-ops-tail", Guid.NewGuid().ToString("N"));
    public LogTailTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Tail_of_large_file_matches_last_lines()
    {
        var file = Path.Combine(_dir, "big.log");
        const int total = 300_000;
        using (var w = new StreamWriter(file, false, new UTF8Encoding(false)))
            for (var i = 0; i < total; i++) w.Write($"{i:D7} line with some padding text {(i % 7 == 0 ? "ERROR" : "info")} ü\n");
        Assert.True(new FileInfo(file).Length > 10_000_000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var last = LogTail.Read(file, 500);
        sw.Stop();
        Assert.Equal(500, last.Count);
        Assert.StartsWith($"{total - 500:D7} ", last[0]);
        Assert.StartsWith($"{total - 1:D7} ", last[^1]);
        Assert.EndsWith("ü", last[^1]);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"tail took {sw.ElapsedMilliseconds} ms");

        var capped = LogTail.Read(file, 1_000_000);
        Assert.Equal(LogTail.MaxLines, capped.Count);
        Assert.StartsWith($"{total - LogTail.MaxLines:D7} ", capped[0]);

        var errors = LogTail.Read(file, 3, "error");
        Assert.Equal(3, errors.Count);
        Assert.All(errors, l => Assert.Contains("ERROR", l));
        var expected = Enumerable.Range(0, total).Where(i => i % 7 == 0).TakeLast(3).Select(i => $"{i:D7}").ToList();
        Assert.Equal(expected, errors.Select(l => l[..7]).ToList());
    }

    [Theory]
    [InlineData("a\nb\nc\n", new[] { "b", "c" }, 2)]
    [InlineData("a\nb\nc", new[] { "b", "c" }, 2)]
    [InlineData("a\r\nb\r\nc\r\n", new[] { "a", "b", "c" }, 10)]
    [InlineData("\n\nx\n", new[] { "", "", "x" }, 10)]
    [InlineData("only", new[] { "only" }, 10)]
    [InlineData("", new string[0], 10)]
    public void Tail_edge_cases(string content, string[] expected, int lines)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(file, content);
        Assert.Equal(expected, LogTail.Read(file, lines));
    }

    [Fact]
    public void Lines_spanning_chunks_and_open_writer()
    {
        var file = Path.Combine(_dir, "long.log");
        var sb = new StringBuilder();
        sb.Append("first\n").Append('y', 50_000).Append('\n');
        for (var i = 0; i < 300; i++) sb.Append('z', 100).Append('\n');
        sb.Append('x', 150_000).Append("END\nlast\n");
        // keep a writer open (like Caddy does) while tailing
        using var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        writer.Flush();

        var lines = LogTail.Read(file, 1000);
        Assert.Equal(304, lines.Count);
        Assert.Equal("first", lines[0]);
        Assert.Equal(new string('y', 50_000), lines[1]); // spans a chunk boundary, below the line cap
        Assert.All(lines.Skip(2).Take(300), l => Assert.Equal(new string('z', 100), l));
        Assert.StartsWith("…", lines[302]); // over the line cap: tail kept
        Assert.EndsWith("xEND", lines[302]);
        Assert.True(lines[302].Length <= LogTail.MaxLineBytes + 1);
        Assert.Equal("last", lines[303]);

        Assert.Equal(["last"], LogTail.Read(file, 1));
    }

    [Fact]
    public void Missing_file_returns_empty() => Assert.Empty(LogTail.Read(Path.Combine(_dir, "nope.log"), 10));
}

public class LogEndpointTests
{
    [Fact]
    public async Task Logs_endpoints_tail_files_and_list_access_hosts()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        File.WriteAllLines(app.Paths.CaddyProcessLog, Enumerable.Range(1, 1000).Select(i => $"caddy {i}"));
        File.WriteAllLines(Path.Combine(app.Paths.AccessLogDir, "example.com.log"), ["{\"a\":1}", "{\"a\":2}"]);
        File.WriteAllText(Path.Combine(app.Paths.AccessLogDir, "example.com-2026-01-01T00-00-00.000.log"), "old");
        File.WriteAllText(Path.Combine(app.Paths.AccessLogDir, "api.example.org.log"), "x\n");
        File.WriteAllLines(Path.Combine(app.Paths.ManagerLogDir, "manager-20200101.log"), ["old manager"]);
        File.WriteAllLines(Path.Combine(app.Paths.ManagerLogDir, "manager-20990101.log"), ["newest manager", "WRN something"]);

        var caddy = await (await admin.GetAsync("api/logs/caddy?lines=5")).JsonAsync();
        Assert.Equal(["caddy 996", "caddy 997", "caddy 998", "caddy 999", "caddy 1000"],
            caddy.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(app.Paths.CaddyProcessLog, caddy.GetProperty("file").GetString());
        var filtered = await (await admin.GetAsync("api/logs/caddy?lines=500&q=caddy 10")).JsonAsync();
        Assert.Equal(12, filtered.GetProperty("lines").GetArrayLength()); // 10, 100-109, 1000

        var access = await (await admin.GetAsync("api/logs/access?host=example.com&lines=10")).JsonAsync();
        Assert.Equal(2, access.GetProperty("lines").GetArrayLength());
        Assert.Equal(["api.example.org", "example.com"], access.GetProperty("hosts").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var traversal = await (await admin.GetAsync("api/logs/access?host=../../db/manager")).JsonAsync();
        Assert.Equal(0, traversal.GetProperty("lines").GetArrayLength());

        var manager = await (await admin.GetAsync("api/logs/manager?q=wrn")).JsonAsync();
        Assert.Equal(["WRN something"], manager.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.EndsWith("manager-20990101.log", manager.GetProperty("file").GetString());
    }
}

public class BackupTests
{
    [Fact]
    public async Task Backup_zip_contains_db_certificates_config_and_manifest_and_restore_roundtrips()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        app.Store.Col<SiteHost>().Insert(new SiteHost { Domains = ["app.example.com"] });
        File.WriteAllText(app.Paths.CaddyConfigFile, "{\"apps\":{}}");
        var certDir = Path.Combine(app.Paths.DefaultCertificateStore, "abc123");
        Directory.CreateDirectory(certDir);
        File.WriteAllText(Path.Combine(certDir, "fullchain.pem"), "CERT");
        File.WriteAllText(Path.Combine(certDir, "privkey.pem"), "KEY");
        Directory.CreateDirectory(Path.Combine(app.Paths.CaddyStorageDir, "pki", "authorities", "local"));
        File.WriteAllText(Path.Combine(app.Paths.CaddyStorageDir, "pki", "authorities", "local", "root.crt"), "ROOT");

        var resp = await admin.GetAsync("api/backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".zip", resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("manager.db", names);
            Assert.Contains("manifest.json", names);
            Assert.Contains("caddy.json", names);
            Assert.Contains("certificates/abc123/fullchain.pem", names);
            Assert.Contains("certificates/abc123/privkey.pem", names);
            Assert.Contains("caddy-data/pki/authorities/local/root.crt", names);

            using var ms = zip.GetEntry("manifest.json")!.Open();
            var manifest = JsonDocument.Parse(ms).RootElement;
            Assert.Equal(AppPaths.ProductName, manifest.GetProperty("product").GetString());
            Assert.Equal(Environment.MachineName, manifest.GetProperty("machine").GetString());
            Assert.Equal(2, manifest.GetProperty("certificateFiles").GetInt32());

            // the DB copy is a consistent, openable LiteDB file with our data
            var dbCopy = Path.Combine(app.DataDir, "verify.db");
            zip.GetEntry("manager.db")!.ExtractToFile(dbCopy);
            using var db = new LiteDatabase(new ConnectionString { Filename = dbCopy, ReadOnly = true });
            Assert.Equal(1, db.GetCollection("User").Count());
            Assert.Equal(1, db.GetCollection("SiteHost").Count());
        }
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "backup");

        // ---- restore: invalid uploads are rejected
        var notZip = await Upload(admin, Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(HttpStatusCode.BadRequest, notZip.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, MakeZip(("manifest.json", "{\"product\":\"Other\"}")))).StatusCode);
        Assert.False(RestoreStager.HasPendingRestore(app.Paths));

        // ---- valid upload is staged
        var staged = await Upload(admin, bytes);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        Assert.True((await staged.JsonAsync()).GetProperty("restartRequired").GetBoolean());
        Assert.True(RestoreStager.HasPendingRestore(app.Paths));

        // ---- apply on a fresh data dir as the host would do before opening LiteStore
        var target = new AppPaths(Path.Combine(app.DataDir, "restored"));
        target.EnsureCreated();
        Directory.Move(RestoreStager.PendingDir(app.Paths), RestoreStager.PendingDir(target));
        File.WriteAllText(target.CaddyConfigFile, "old config");
        var outcome = RestoreStager.ApplyPendingRestore(target);
        Assert.NotNull(outcome);
        Assert.False(RestoreStager.LastOutcomeFailed, outcome);
        Assert.False(RestoreStager.HasPendingRestore(target));
        Assert.Equal("{\"apps\":{}}", File.ReadAllText(target.CaddyConfigFile));
        Assert.Equal("CERT", File.ReadAllText(Path.Combine(target.DefaultCertificateStore, "abc123", "fullchain.pem")));
        Assert.Equal("ROOT", File.ReadAllText(Path.Combine(target.CaddyStorageDir, "pki", "authorities", "local", "root.crt")));
        Assert.True(Directory.EnumerateDirectories(target.BackupDir, "pre-restore-*").Any());
        using (var restored = new Core.Infrastructure.LiteStore(target))
            Assert.Equal(TestApp.AdminEmail, restored.Col<User>().FindAll().Single().Email);
        Assert.Null(RestoreStager.ApplyPendingRestore(target));
    }

    private static Task<HttpResponseMessage> Upload(HttpClient c, byte[] data)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        return c.PostAsync("api/backup/restore", form);
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }
}

public class DashboardTests
{
    [Fact]
    public async Task Dashboard_aggregates_data()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        app.Store.Col<SiteHost>().Insert(new SiteHost { Kind = HostKind.Proxy, Domains = ["a.example.com"] });
        app.Store.Col<SiteHost>().Insert(new SiteHost { Kind = HostKind.Proxy, Domains = ["b.example.com"], Enabled = false });
        app.Store.Col<SiteHost>().Insert(new SiteHost { Kind = HostKind.Redirect, Domains = ["c.example.com"] });
        app.Store.Col<StreamHost>().Insert(new StreamHost { ListenPort = 2222 });
        app.Certificates.Certificates =
        [
            new CertificateInfo { Id = "1", Kind = CertificateKind.Custom, DaysRemaining = 3, Name = "soon" },
            new CertificateInfo { Id = "2", Kind = CertificateKind.Acme, DaysRemaining = 80 },
            new CertificateInfo { Id = "3", Kind = CertificateKind.Internal, DaysRemaining = 0 },
        ];
        app.Admin.Upstreams = [new UpstreamHealth { Address = "10.0.0.1:80" }, new UpstreamHealth { Address = "10.0.0.2:80", Healthy = false }];
        app.Readiness.LastReport = new ReadinessReport
        {
            RanAt = DateTime.UtcNow,
            Checks = [new ReadinessCheck { Id = "a", Status = CheckStatus.Pass }, new ReadinessCheck { Id = "b", Status = CheckStatus.Fail }],
        };
        app.Services.GetRequiredService<IEventSink>().Raise(EventSeverity.Info, "test", "hello");

        var d = await (await admin.GetAsync("api/dashboard")).JsonAsync();
        Assert.Equal("running", d.GetProperty("caddy").GetProperty("state").GetString());
        Assert.Equal("v2.11.4", d.GetProperty("binary").GetProperty("installed").GetProperty("version").GetString());
        var counts = d.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("proxy").GetInt32());
        Assert.Equal(1, counts.GetProperty("redirect").GetInt32());
        Assert.Equal(1, counts.GetProperty("streams").GetInt32());
        Assert.Equal(1, counts.GetProperty("hostsDisabled").GetInt32());
        Assert.Equal(3, counts.GetProperty("certificates").GetInt32());
        Assert.Equal(1, counts.GetProperty("certificatesExpiring").GetInt32());
        Assert.Equal(1, d.GetProperty("readiness").GetProperty("fail").GetInt32());
        Assert.Equal(2, d.GetProperty("upstreams").GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("upstreams").GetProperty("unhealthy").GetInt32());
        Assert.Equal("hello", d.GetProperty("recentEvents")[0].GetProperty("message").GetString());
        Assert.Equal(Environment.MachineName, d.GetProperty("system").GetProperty("hostname").GetString());
        Assert.Equal(0, d.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_renders_when_dependencies_fail_or_hang()
    {
        await using var app = await TestApp.StartAsync(o => o.DashboardPartTimeout = TimeSpan.FromMilliseconds(500));
        var admin = await app.SetupAdminAsync();
        app.CaddyHost.Throw = new InvalidOperationException("service control manager unavailable");
        app.Binary.Hang = true;
        app.Certificates.Throw = new IOException("share offline");
        app.Admin.Throw = new HttpRequestException("connection refused");
        app.Readiness.ThrowOnLastReport = new Exception("boom");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await admin.GetAsync("api/dashboard");
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        var d = await resp.JsonAsync();
        Assert.Equal("unknown", d.GetProperty("caddy").GetProperty("state").GetString());
        Assert.True(d.GetProperty("warnings").GetArrayLength() >= 4);
        Assert.Equal(JsonValueKind.Null, d.TryGetProperty("readiness", out var r) ? r.ValueKind : JsonValueKind.Null);
        Assert.Equal(0, d.GetProperty("upstreams").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Dashboard_works_without_config_or_platform_modules()
    {
        await using var app = await TestApp.StartAsync(registerFakes: false);
        var admin = await app.SetupAdminAsync();
        var resp = await admin.GetAsync("api/dashboard");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
