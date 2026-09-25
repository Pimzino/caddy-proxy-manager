using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

/// <summary>PfxFile and WindowsStore certificate sources, sync status and events.</summary>
public sealed class CertificateSourceTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "cpm-certsrc-" + Guid.NewGuid().ToString("N")[..8]);

    public CertificateSourceTests() => Directory.CreateDirectory(_outside);

    public void Dispose()
    {
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<JsonObject> Body(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    private string WritePfx(X509Certificate2 cert, string password, string name = "site.pfx")
    {
        var path = Path.Combine(_outside, name);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password)!);
        return path;
    }

    private static X509Certificate2 Leaf(string pem) => X509Certificate2.CreateFromPem(pem);

    // ------------------------------------------------------------------ PFX file

    [Fact]
    public async Task Pfx_file_source_end_to_end()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        using var first = TestCerts.SelfSigned(["pfx.example.com"]);
        var pfxPath = WritePfx(first, "pfx-pass");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/certificates/pfx-path", new { pfxPath, pfxPassword = "wrong" }, Json)).StatusCode);
        var created = await c.PostAsJsonAsync("/api/certificates/pfx-path", new { name = "From PFX", pfxPath, pfxPassword = "pfx-pass" }, Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var raw = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pfxPasswordProtected", raw);
        Assert.DoesNotContain("pfx-pass", raw);
        var item = JsonNode.Parse(raw)!["item"]!;
        var id = item["id"]!.GetValue<string>();
        Assert.Equal("pfxFile", item["source"]!.GetValue<string>());
        Assert.Equal(pfxPath, item["sourcePath"]!.GetValue<string>());
        Assert.True(item["hasPfxPassword"]!.GetValue<bool>());
        Assert.NotNull(item["lastSyncedAt"]);
        var certPath = item["certPath"]!.GetValue<string>();
        Assert.Equal(Path.Combine(api.Env.Paths.DefaultCertificateStore, id, "fullchain.pem"), certPath);
        Assert.Equal(first.Thumbprint, Leaf(File.ReadAllText(certPath)).Thumbprint);
        Assert.Contains("PRIVATE KEY", File.ReadAllText(item["keyPath"]!.GetValue<string>()));
        var stored = api.Store.Col<Certificate>().FindById(id);
        Assert.Equal("pfx-pass", api.App.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.PfxPasswordProtected!));

        var inventory = await c.GetFromJsonAsync<JsonArray>("/api/certificates", Json);
        Assert.Equal("pfxFile", inventory!.Single(x => x!["id"]!.GetValue<string>() == id)!["source"]!.GetValue<string>());

        // A host uses it; the PFX is renewed in place (win-acme style) → the running watcher re-converts it.
        var host = new { kind = "response", domains = new[] { "pfx.example.com" }, tls = "custom", certificateId = id };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/hosts", host, Json)).StatusCode);
        using var renewed = TestCerts.SelfSigned(["pfx.example.com", "www.pfx.example.com"]);
        WritePfx(renewed, "pfx-pass");
        File.SetLastWriteTimeUtc(pfxPath, DateTime.UtcNow.AddMinutes(1));
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline && api.Store.Col<Certificate>().FindById(id).Thumbprint != renewed.Thumbprint)
        {
            api.App.Services.GetRequiredService<CertificateWatcher>().Poke();
            await Task.Delay(500);
        }
        Assert.Equal(renewed.Thumbprint, api.Store.Col<Certificate>().FindById(id).Thumbprint);
        Assert.Equal(renewed.Thumbprint, Leaf(File.ReadAllText(certPath)).Thumbprint);
        Assert.Contains(api.Store.Col<ConfigRevision>().FindAll(), r => r.Reason.Contains("From PFX"));

        // Deleting removes the converted files (never the source PFX).
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/hosts/{api.Store.Col<SiteHost>().FindAll().Single().Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/certificates/{id}")).StatusCode);
        Assert.False(Directory.Exists(Path.GetDirectoryName(certPath)));
        Assert.True(File.Exists(pfxPath));
    }

    [Fact]
    public async Task Pfx_watcher_reconverts_on_change_reports_errors_and_recovery()
    {
        using var env = new TempEnv();
        var events = new RecordingEventSink();
        var sync = TestSync.Create(env, events: events);
        using var first = TestCerts.SelfSigned(["w.example.com"]);
        var pfxPath = WritePfx(first, "");
        var cert = new Certificate { Id = "p1", Name = "watched", Source = CertificateSource.PfxFile, SourcePath = pfxPath };
        env.Store.Col<Certificate>().Insert(cert);
        Assert.True((await sync.SyncAsync("p1"))!.FilesWritten); // initial conversion
        env.Store.Col<SiteHost>().Insert(new SiteHost { Id = "h1", Domains = ["w.example.com"], Tls = TlsMode.Custom, CertificateId = "p1" });

        var config = new CountingConfigService();
        using var watcher = new CertificateWatcher(env.Store, config, sync, NullLogger<CertificateWatcher>.Instance);
        Assert.False(await watcher.CheckAsync(CancellationToken.None)); // snapshot

        // same certificate re-exported (file touched): files unchanged → no reload
        WritePfx(first, "");
        File.SetLastWriteTimeUtc(pfxPath, DateTime.UtcNow.AddMinutes(1));
        Assert.False(await watcher.CheckAsync(CancellationToken.None));
        Assert.Empty(config.Reasons);

        using var renewed = TestCerts.SelfSigned(["w.example.com"]);
        WritePfx(renewed, "");
        File.SetLastWriteTimeUtc(pfxPath, DateTime.UtcNow.AddMinutes(2));
        Assert.True(await watcher.CheckAsync(CancellationToken.None));
        Assert.Single(config.Reasons);
        var stored = env.Store.Col<Certificate>().FindById("p1");
        Assert.Equal(renewed.Thumbprint, stored.Thumbprint);
        Assert.Equal(renewed.Thumbprint, Leaf(File.ReadAllText(stored.CertPath)).Thumbprint);

        File.Delete(pfxPath);
        Assert.False(await watcher.CheckAsync(CancellationToken.None));
        stored = env.Store.Col<Certificate>().FindById("p1");
        Assert.Contains("cannot be read", stored.LastSyncError);
        Assert.Contains(events.Events, e => e.Severity == EventSeverity.Warning && e.Key == "cert-sync:p1" && e.AlertRule == "certificateExpiry");
        Assert.True(File.Exists(stored.CertPath)); // last good copy stays in place

        WritePfx(renewed, "");
        Assert.False(await watcher.CheckAsync(CancellationToken.None)); // unchanged certificate: nothing to reload
        Assert.Null(env.Store.Col<Certificate>().FindById("p1").LastSyncError);
        Assert.Contains(events.Events, e => e.Severity == EventSeverity.Recovered && e.Key == "cert-sync:p1");
    }

    [Fact]
    public async Task File_path_sync_errors_raise_events_and_the_sync_endpoint_reports_them()
    {
        await using var api = ApiHost.Start();
        using var cert = TestCerts.SelfSigned(["fp.example.com"]);
        var certPath = Path.Combine(_outside, "fp.pem");
        var keyPath = Path.Combine(_outside, "fp.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(cert));
        var created = await Body(await api.Client.PostAsJsonAsync("/api/certificates/path", new { name = "fp", certPath, keyPath }, Json));
        var id = created["item"]!["id"]!.GetValue<string>();

        var ok = await api.Client.PostAsync($"/api/certificates/{id}/sync", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False((await Body(ok))["changed"]!.GetValue<bool>());

        File.Delete(keyPath);
        var failed = await api.Client.PostAsync($"/api/certificates/{id}/sync", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Contains("cannot be read", (await Body(failed))["detail"]!.GetValue<string>());
        Assert.Contains(api.Events.Events, e => e.Key == $"cert-sync:{id}" && e.Severity == EventSeverity.Warning);
        var inv = await api.Client.GetFromJsonAsync<JsonArray>("/api/certificates", Json);
        Assert.Contains("Last synchronisation failed", inv!.Single(x => x!["id"]!.GetValue<string>() == id)!["error"]!.GetValue<string>());

        File.WriteAllText(keyPath, TestCerts.KeyPem(cert));
        Assert.Equal(HttpStatusCode.OK, (await api.Client.PostAsync($"/api/certificates/{id}/sync", null)).StatusCode);
        Assert.Contains(api.Events.Events, e => e.Key == $"cert-sync:{id}" && e.Severity == EventSeverity.Recovered);
        Assert.Contains(api.Audit.Entries, e => e.Action == "synced");

        using var up = TestCerts.SelfSigned(["up.example.com"]);
        var uploaded = await Body(await api.Client.PostAsJsonAsync("/api/certificates/pem", new { certPem = up.ExportCertificatePem(), keyPem = TestCerts.KeyPem(up) }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client.PostAsync($"/api/certificates/{uploaded["item"]!["id"]!.GetValue<string>()}/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Repointing_to_a_pfx_keeps_the_stored_password_when_omitted()
    {
        await using var api = ApiHost.Start();
        using var cert = TestCerts.SelfSigned(["rp.example.com"]);
        var pfx = WritePfx(cert, "pw1", "a.pfx");
        var created = await Body(await api.Client.PostAsJsonAsync("/api/certificates/pfx-path", new { pfxPath = pfx, pfxPassword = "pw1" }, Json));
        var id = created["item"]!["id"]!.GetValue<string>();

        using var other = TestCerts.SelfSigned(["rp.example.com"]);
        var moved = WritePfx(other, "pw1", "b.p12");
        var r = await api.Client.PostAsJsonAsync($"/api/certificates/{id}/replace", new { pfxPath = moved }, Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var item = (await Body(r))["item"]!;
        Assert.Equal(moved, item["sourcePath"]!.GetValue<string>());
        Assert.Equal(other.Thumbprint, item["thumbprint"]!.GetValue<string>());

        // re-point to PEM files → the converted files in the store are removed
        var certPath = Path.Combine(_outside, "c.crt");
        var keyPath = Path.Combine(_outside, "c.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(cert));
        var toFiles = await api.Client.PostAsJsonAsync($"/api/certificates/{id}/replace", new { certPath, keyPath }, Json);
        Assert.Equal(HttpStatusCode.OK, toFiles.StatusCode);
        var files = (await Body(toFiles))["item"]!;
        Assert.Equal("filePath", files["source"]!.GetValue<string>());
        Assert.False(files["hasPfxPassword"]!.GetValue<bool>());
        Assert.False(Directory.Exists(Path.Combine(api.Env.Paths.DefaultCertificateStore, id)));
    }

    // ------------------------------------------------------------------ Windows store

    private static StoreCertificateInfo Info(string thumb, string cn, string[] dns, DateTime notBefore, DateTime notAfter, bool key = true, bool serverAuth = true) => new()
    {
        Thumbprint = thumb, Subject = "CN=" + cn, CommonName = cn, DnsNames = dns.ToList(), NotBefore = notBefore, NotAfter = notAfter,
        HasPrivateKey = key, ServerAuth = serverAuth,
    };

    [Fact]
    public void Windows_store_selection()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = Info("AA", "app.corp.test", ["app.corp.test"], now.AddDays(-300), now.AddDays(60));
        var newer = Info("BB", "app.corp.test", ["app.corp.test"], now.AddDays(-5), now.AddDays(360));
        var future = Info("CC", "app.corp.test", ["app.corp.test"], now.AddDays(5), now.AddDays(400));
        var expired = Info("DD", "app.corp.test", ["app.corp.test"], now.AddDays(-800), now.AddDays(-1));
        var noKey = Info("EE", "app.corp.test", ["app.corp.test"], now.AddDays(-1), now.AddDays(500), key: false);
        var clientAuth = Info("FF", "app.corp.test", ["app.corp.test"], now.AddDays(-1), now.AddDays(500), serverAuth: false);
        var wildcard = Info("99", "corp wildcard", ["*.corp.test"], now.AddDays(-10), now.AddDays(100));
        var all = new[] { old, newer, future, expired, noKey, clientAuth, wildcard };

        Assert.Equal("BB", WindowsStoreSelector.Select(all, null, "app.corp.test", now, "LocalMachine\\My").Thumbprint);
        Assert.Equal("BB", WindowsStoreSelector.Select(all, null, "CN=APP.corp.test", now, "LocalMachine\\My").Thumbprint);
        Assert.Equal("99", WindowsStoreSelector.Select(all, null, "other.corp.test", now, "LocalMachine\\My").Thumbprint);
        Assert.Equal("AA", WindowsStoreSelector.Select(all, "a a", null, now, "LocalMachine\\My").Thumbprint); // pin, even if older
        Assert.Contains("no private key", Assert.Throws<CertificateImportException>(() => WindowsStoreSelector.Select(all, "ee", null, now, "LocalMachine\\My")).Message);
        Assert.Contains("No certificate with thumbprint", Assert.Throws<CertificateImportException>(() => WindowsStoreSelector.Select(all, "12", null, now, "LocalMachine\\My")).Message);
        Assert.Contains("No certificate for", Assert.Throws<CertificateImportException>(() => WindowsStoreSelector.Select(all, null, "nope.test", now, "LocalMachine\\My")).Message);
        var onlyExpired = Assert.Throws<CertificateImportException>(() => WindowsStoreSelector.Select([expired], null, "app.corp.test", now, "LocalMachine\\My")).Message;
        Assert.Contains("none is currently valid", onlyExpired);
    }

    [Fact]
    public void Real_store_is_empty_off_windows()
    {
        var source = new WindowsCertificateStoreSource();
        Assert.Equal(OperatingSystem.IsWindows(), source.IsSupported);
        if (OperatingSystem.IsWindows()) return;
        Assert.Empty(source.List("LocalMachine", "My"));
        Assert.Contains("only available", Assert.Throws<CertificateImportException>(() => source.Export("LocalMachine", "My", "AA")).Message);
    }

    [Fact]
    public void Certificate_helpers_read_names_usage_and_templates()
    {
        using var cert = TestCerts.SelfSigned(["a.example.com", "b.example.com"]);
        Assert.Equal(["a.example.com", "b.example.com"], WindowsCertificateStoreSource.DnsNames(cert));
        Assert.True(WindowsCertificateStoreSource.AllowsServerAuth(cert));
        Assert.Null(WindowsCertificateStoreSource.TemplateName(cert));
        var parsed = CertificateParser.FromCertificate(cert, [], "not exportable");
        Assert.Equal(cert.Thumbprint, parsed.Metadata.Thumbprint);
        Assert.Contains("PRIVATE KEY", parsed.PrivateKeyPem);
    }

    [Fact]
    public async Task Windows_store_source_follows_renewals_and_reapplies_only_on_change()
    {
        var store = new FakeWindowsStore();
        using var first = TestCerts.SelfSigned(["app.corp.test"], DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(30));
        store.Add(first);
        using var unrelated = TestCerts.SelfSigned(["other.corp.test"]);
        store.Add(unrelated);
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<IWindowsCertificateSource>(store));
        var c = api.Client;

        var list = await c.GetFromJsonAsync<JsonArray>("/api/certificates/windows-store?location=LocalMachine&store=My", Json);
        Assert.Equal(2, list!.Count);
        Assert.NotNull(list[0]!["exportable"]);
        Assert.Null(list[0]!["serverAuth"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/certificates/windows-store?location=Elsewhere")).StatusCode);

        var bad = await c.PostAsJsonAsync("/api/certificates/windows-store", new { thumbprint = first.Thumbprint, subject = "app.corp.test" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var created = await c.PostAsJsonAsync("/api/certificates/windows-store", new { name = "Corp app", subject = "app.corp.test" }, Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var item = (await Body(created))["item"]!;
        var id = item["id"]!.GetValue<string>();
        Assert.Equal("windowsStore", item["source"]!.GetValue<string>());
        Assert.Equal("LocalMachine", item["storeLocation"]!.GetValue<string>());
        Assert.Equal("My", item["storeName"]!.GetValue<string>());
        Assert.Equal("app.corp.test", item["storeSubject"]!.GetValue<string>());
        Assert.Equal(first.Thumbprint, item["thumbprint"]!.GetValue<string>());
        var certPath = item["certPath"]!.GetValue<string>();
        Assert.Equal(first.Thumbprint, Leaf(File.ReadAllText(certPath)).Thumbprint);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/hosts", new { kind = "response", domains = new[] { "app.corp.test" }, tls = "custom", certificateId = id }, Json)).StatusCode);

        // unchanged: no export, no reload
        var exports = store.Exports;
        var config = new CountingConfigService();
        var sync = api.App.Services.GetRequiredService<CertificateSyncService>();
        using var watcher = new CertificateWatcher(api.Store, config, sync, NullLogger<CertificateWatcher>.Instance);
        Assert.False(await watcher.SyncWindowsStoreAsync(CancellationToken.None));
        Assert.Equal(exports, store.Exports);
        Assert.NotNull(api.Store.Col<Certificate>().FindById(id).LastSyncedAt);

        // AD CS autoenrollment renewed the certificate → the newest valid one is exported and the config re-applied
        using var renewed = TestCerts.SelfSigned(["app.corp.test"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        store.Add(renewed);
        Assert.True(await watcher.SyncWindowsStoreAsync(CancellationToken.None));
        Assert.Single(config.Reasons);
        Assert.Equal(renewed.Thumbprint, api.Store.Col<Certificate>().FindById(id).Thumbprint);
        Assert.Equal(renewed.Thumbprint, Leaf(File.ReadAllText(certPath)).Thumbprint);

        // /sync endpoint: a newer, non-exportable certificate gives a clear error and keeps the last good copy
        using var locked = TestCerts.SelfSigned(["app.corp.test"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(400));
        store.Add(locked, exportable: false);
        var failed = await c.PostAsync($"/api/certificates/{id}/sync", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        var detail = (await Body(failed))["detail"]!.GetValue<string>();
        Assert.Contains("not exportable", detail);
        Assert.Contains("template", detail);
        Assert.Equal(renewed.Thumbprint, Leaf(File.ReadAllText(certPath)).Thumbprint);
        Assert.Contains(api.Events.Events, e => e.Key == $"cert-sync:{id}" && e.Severity == EventSeverity.Warning);

        // pinned by thumbprint: renewals are not followed
        var pinned = await c.PostAsJsonAsync("/api/certificates/windows-store", new { thumbprint = first.Thumbprint.ToLowerInvariant() }, Json);
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        var pinnedItem = (await Body(pinned))["item"]!;
        Assert.Equal(first.Thumbprint, pinnedItem["storeThumbprint"]!.GetValue<string>());
        var pinnedSync = await c.PostAsync($"/api/certificates/{pinnedItem["id"]!.GetValue<string>()}/sync", null);
        Assert.Equal(HttpStatusCode.OK, pinnedSync.StatusCode);
        Assert.False((await Body(pinnedSync))["changed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Windows_store_endpoints_report_unsupported_platforms()
    {
        var store = new FakeWindowsStore { IsSupported = false };
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<IWindowsCertificateSource>(store));
        Assert.Empty((await api.Client.GetFromJsonAsync<JsonArray>("/api/certificates/windows-store", Json))!);
        var r = await api.Client.PostAsJsonAsync("/api/certificates/windows-store", new { subject = "a.example.com" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("only available", (await Body(r))["detail"]!.GetValue<string>());
    }
}
