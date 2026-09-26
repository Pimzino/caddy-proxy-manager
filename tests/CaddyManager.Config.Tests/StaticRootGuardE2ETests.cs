using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// SEC-4 / SEC-5: static roots that are (inside or above) a folder this server must never publish — its certificate store
/// (node-local), the shared storage folder, its data folder — are refused by the generator on every server, and a settings
/// change that would make an existing static root such a folder is refused by the API. Artifact: static-root-guard.json.
/// </summary>
public sealed class StaticRootGuardE2ETests
{
    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    /// <summary>
    /// Ways it could fail:
    /// (1) a host that reached the store without the API check (cluster replication to a node whose certificate store is
    ///     a custom folder) is served by file_server: privkey.pem of every certificate downloadable;
    /// (2) only the exact folder is refused, not a root inside the shared storage folder or above the data folder;
    /// (3) the refusal is silent (no warning) or serves something else (index, directory listing, fallback) instead of 403;
    /// (4) ordinary static roots outside those folders stop working;
    /// (5) the refusal is no protection because Caddy would not have served the file anyway (control: the same host with
    ///     a file_server loaded by hand serves the key).
    /// </summary>
    [CaddyFact]
    public async Task Generator_answers_403_for_static_roots_on_this_servers_certificate_store_and_storage()
    {
        var report = E2EArtifacts.Report(nameof(Generator_answers_403_for_static_roots_on_this_servers_certificate_store_and_storage));
        using var c = new LiveCaddy();
        var certStore = c.S.Env.WebRoot("node-certificates"); // node-local CertificateStorePath outside the data folder
        var shared = c.S.Env.WebRoot("shared-storage");
        var site = c.S.Env.WebRoot("site");
        File.WriteAllText(Path.Combine(site, "index.html"), "public site");
        c.UpdateSettings(s =>
        {
            s.CertificateStorePath = certStore;
            s.StorageBackend = StorageBackend.FileSystem;
            s.StoragePath = shared;
        });
        c.AddUploadedCertificate("replcert", TestCerts.SelfSigned(["replicated.test"])); // written to the custom store
        var keyFile = Path.Combine(certStore, "replcert", "privkey.pem");
        Assert.True(File.Exists(keyFile));
        Directory.CreateDirectory(Path.Combine(shared, "certificates"));
        File.WriteAllText(Path.Combine(shared, "certificates", "fake.key"), "-----BEGIN PRIVATE KEY-----shared");

        // Inserted like cluster replication does (no API check on this server). (1)(2)
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["store.test"], Tls = TlsMode.None, RootPath = certStore, Browse = true, Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["shared.test"], Tls = TlsMode.None, RootPath = Path.Combine(shared, "certificates"), Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["above.test"], Tls = TlsMode.None, RootPath = Path.GetDirectoryName(c.S.Paths.DataDir)!, Browse = true, Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["site.test"], Tls = TlsMode.None, RootPath = site, Compression = false });
        await c.StartAsync();
        var apply = await c.ApplyAsync();
        report["warnings"] = new JsonArray(apply.Warnings.Select(w => (JsonNode)w).ToArray());

        // (3)
        foreach (var host in new[] { "store.test", "shared.test", "above.test" })
            Assert.Contains(apply.Warnings, w => w.Contains($"'{host}'", StringComparison.Ordinal) && w.Contains("403", StringComparison.Ordinal));
        var results = new JsonObject();
        foreach (var (host, path) in new[] { ("store.test", "/replcert/privkey.pem"), ("store.test", "/"), ("shared.test", "/fake.key"), ("above.test", "/") })
        {
            var r = await c.HttpAsync(host, path);
            results[host + path] = r.Status;
            Assert.Equal(403, r.Status);
            Assert.DoesNotContain("PRIVATE KEY", r.Body);
            Assert.DoesNotContain("replcert", r.Body); // no directory listing
        }
        // (4)
        var ok = await c.HttpAsync("site.test", "/index.html");
        results["site.test/index.html"] = ok.Status;
        Assert.Equal(200, ok.Status);
        Assert.Equal("public site", ok.Body);

        // (5) control: with the refusal replaced by a file_server (what the generator emitted before), Caddy serves the key.
        var running = await c.RunningConfigAsync();
        Assert.True(ReplaceRefusal(running, "store.test", certStore), "403 route of store.test not found");
        await c.LoadRawAsync(running);
        var leaked = await Wait.For(async () => (await c.HttpAsync("store.test", "/replcert/privkey.pem")).Body.Contains("PRIVATE KEY", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        results["control: file_server on the store serves privkey.pem"] = leaked;
        Assert.True(leaked);
        report["responses"] = results;
        report["passed"] = true;
        E2EArtifacts.Write("static-root-guard.json", report);
    }

    /// <summary>
    /// Ways it could fail (SEC-5):
    /// (1) switching the storage to a shared folder at or inside an existing static root succeeds, and the local
    ///     certificates, ACME accounts and the internal CA key are copied into the published folder before anything checks;
    /// (2) the same for a new certificate store (CertificateStorePath) inside a static root;
    /// (3) the error does not name the conflicting host, or uses a field the UI cannot show;
    /// (4) hosts that are disabled, or roots unrelated to the new folders, block the change; a folder outside every
    ///     static root is refused.
    /// </summary>
    [Fact]
    public async Task Settings_refuse_a_storage_or_certificate_store_folder_inside_an_existing_static_root()
    {
        var report = E2EArtifacts.Report(nameof(Settings_refuse_a_storage_or_certificate_store_folder_inside_an_existing_static_root));
        await using var api = ApiHost.Start();
        var www = api.Env.WebRoot("www");
        var other = api.Env.WebRoot("other");
        // Local storage with an internal CA key that a switch to a shared folder would copy.
        var localKey = Path.Combine(api.Env.Paths.CaddyStorageDir, "pki", "authorities", "local", "root.key");
        Directory.CreateDirectory(Path.GetDirectoryName(localKey)!);
        File.WriteAllText(localKey, "-----BEGIN EC PRIVATE KEY-----local-ca");

        var post = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "static", domains = new[] { "www.example.com" }, tls = "none", rootPath = www });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var disabled = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "static", enabled = false, domains = new[] { "off.example.com" }, tls = "none", rootPath = other });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var disabledId = (await Json(disabled))["item"]!["id"]!.GetValue<string>();

        // (1)
        var sharedInWww = Path.Combine(www, "caddy-storage");
        var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = sharedInWww });
        var body = await Json(r);
        report["storageInsideRoot"] = body.DeepClone();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var storageErrors = body["errors"]!["storagePath"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(); // (3)
        Assert.Contains(storageErrors, e => e.Contains("www.example.com", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(sharedInWww, "pki", "authorities", "local", "root.key")));
        Assert.Equal(StorageBackend.Local, api.Store.GetSettings<CaddySettings>().StorageBackend);

        // (2)
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { certificateStorePath = Path.Combine(www, "certs") });
        body = await Json(r);
        report["certificateStoreInsideRoot"] = body.DeepClone();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains(body["errors"]!["certificateStorePath"]!.AsArray(), e => e!.GetValue<string>().Contains("www.example.com", StringComparison.Ordinal));
        Assert.Null(api.Store.GetSettings<CaddySettings>().CertificateStorePath);

        // (4) a folder inside the disabled host's root and a folder outside every root are accepted; the CA is copied
        var shared = Path.Combine(other, "caddy-storage");
        r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = shared });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Assert.True(File.Exists(Path.Combine(shared, "pki", "authorities", "local", "root.key")));
        // ...and enabling the host whose root now contains the shared storage is refused by the host API itself.
        r = await api.SendAsync(HttpMethod.Put, $"/api/hosts/{disabledId}", new { kind = "static", enabled = true, domains = new[] { "off.example.com" }, tls = "none", rootPath = other });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("shared Caddy storage", await r.Content.ReadAsStringAsync());
        report["passed"] = true;
        E2EArtifacts.Write("static-root-settings.json", report);
    }

    /// <summary>
    /// Ways it could fail (shared storage and certificate stores usually live on a share):
    /// (1) a UNC root equal to, inside or above the shared storage share or a UNC certificate store is accepted (for
    ///     administrators, or on a node through replication), because UNC roots skip the protected-folder comparison;
    /// (2) the comparison is case- or separator-sensitive (\\FS\Caddy vs //fs/caddy/);
    /// (3) ordinary UNC roots on another share, or next to the storage folder, are refused; operators get 400 instead of
    ///     the 403 "administrators only" for an ordinary UNC root.
    /// </summary>
    [Fact]
    public void Unc_static_roots_on_the_shared_storage_or_certificate_store_share_are_refused()
    {
        using var env = new TempEnv();
        var settings = new CaddySettings { StorageBackend = StorageBackend.FileSystem, StoragePath = @"\\fs01\caddy\storage", CertificateStorePath = @"\\fs01\certs" };
        string? Problem(string root) => Services.CaddyStorage.StaticRootProblem(root, settings, env.Paths)?.Message;
        Assert.Contains("the same as the shared Caddy storage", Problem(@"\\fs01\caddy\storage")); // (1)
        Assert.Contains("inside the shared Caddy storage", Problem(@"//FS01/Caddy/Storage/certificates/")); // (2)
        Assert.Contains("a parent of the shared Caddy storage", Problem(@"\\fs01\caddy"));
        Assert.Contains("certificate store", Problem(@"\\fs01\certs\abc123"));
        Assert.Null(Problem(@"\\fs01\web\site")); // (3)
        Assert.Null(Problem(@"\\fs01\caddy\www"));
        var operatorView = Validation.PathGuard.CheckStaticRoot(@"\\fs01\web\site", env.Paths, @"\\fs01\certs", isAdmin: false, @"\\fs01\caddy\storage");
        Assert.Equal(403, operatorView!.Status);
    }

    /// <summary>Replaces the host's 403 refusal route (generated by the root guard) with a file_server on the given root.</summary>
    private static bool ReplaceRefusal(JsonNode config, string host, string root)
    {
        JsonObject? hostRoute = null;
        void FindHost(JsonNode? n)
        {
            if (hostRoute is not null || n is null) return;
            if (n is JsonObject o)
            {
                if (o["match"] is JsonArray m && m.Any(x => x?["host"] is JsonArray hs && hs.Any(h => h?.GetValue<string>() == host)))
                {
                    hostRoute = o;
                    return;
                }
                foreach (var (_, v) in o) FindHost(v);
            }
            else if (n is JsonArray a) foreach (var v in a) FindHost(v);
        }
        FindHost(config);
        if (hostRoute is null) return false;
        var replaced = false;
        void Replace(JsonNode? n)
        {
            if (replaced || n is null) return;
            if (n is JsonObject o)
            {
                if (o["handler"]?.GetValue<string>() == "static_response" && o["status_code"] is JsonValue sc && sc.TryGetValue<int>(out var code) && code == 403)
                {
                    o.Clear();
                    o["handler"] = "file_server";
                    o["root"] = root;
                    replaced = true;
                    return;
                }
                foreach (var (_, v) in o.ToList()) Replace(v);
            }
            else if (n is JsonArray a) foreach (var v in a.ToList()) Replace(v);
        }
        Replace(hostRoute);
        return replaced;
    }
}
