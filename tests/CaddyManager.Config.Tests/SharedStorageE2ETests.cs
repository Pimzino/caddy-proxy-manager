using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Shared file-system storage with real Caddy processes: two instances configured with the same StoragePath act as one
/// certificate cluster, and switching an existing server from local to shared storage keeps its certificates and internal
/// CA. Artifact: shared-storage.json.
/// </summary>
public sealed class SharedStorageE2ETests
{
    /// <summary>
    /// Ways it could fail:
    /// (1) the generated storage still points at each server's local folder (two different internal roots);
    /// (2) the second instance creates its own CA instead of loading the shared one (certificates chain to different roots);
    /// (3) Caddy cannot use the folder (config rejected, or certificates written elsewhere);
    /// (4) switching Local → FileSystem through the settings API does not copy certificates/, acme/, pki/ — the internal
    ///     root changes and every client that trusted it breaks — or overwrites data that already exists there;
    /// (5) the copy is not reported, or the certificate inventory / internal-root download still read the old folder.
    /// </summary>
    [CaddyFact]
    public async Task Two_servers_share_one_internal_ca_and_switching_to_shared_storage_keeps_it()
    {
        var report = E2EArtifacts.Report(nameof(Two_servers_share_one_internal_ca_and_switching_to_shared_storage_keeps_it));
        var shared = Path.Combine(Path.GetTempPath(), "cpm-shared-storage", Guid.NewGuid().ToString("N")[..10]);
        using var cleanup = new DirCleanup(Path.GetDirectoryName(shared)!);
        try
        {
            // ---- part 1: two Caddy processes, distinct ports, one shared storage folder
            void UseShared(CaddySettings s) { s.StorageBackend = StorageBackend.FileSystem; s.StoragePath = shared; }
            using var one = new LiveCaddy(UseShared);
            using var two = new LiveCaddy(UseShared);
            var host = new SiteHost { Id = "shared1", Kind = HostKind.Response, Domains = ["shared.test"], Tls = TlsMode.Internal, ResponseBody = "shared", ResponseStatus = 200 };
            one.Add(host);
            two.Add(host);
            await one.StartAsync();
            await one.ApplyAsync();
            var running = await one.RunningConfigAsync();
            Assert.Equal("file_system", running["storage"]!["module"]!.GetValue<string>()); // (1)
            Assert.Equal(shared, running["storage"]!["root"]!.GetValue<string>());
            var r1 = await TlsProbe.UntilAsync(one.HttpsPort, "shared.test", r => r.Status == 200, TimeSpan.FromSeconds(30));
            Assert.Equal(200, r1.Status);
            await two.StartAsync();
            await two.ApplyAsync();
            var r2 = await TlsProbe.UntilAsync(two.HttpsPort, "shared.test", r => r.Status == 200, TimeSpan.FromSeconds(30));
            Assert.Equal(200, r2.Status);

            var rootPath = Path.Combine(shared, "pki", "authorities", "local", "root.crt");
            Assert.True(File.Exists(rootPath), "no internal root in the shared folder"); // (3)
            var root = X509Certificate2.CreateFromPem(File.ReadAllText(rootPath));
            var intermediate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(shared, "pki", "authorities", "local", "intermediate.crt")));
            var v1 = Verify(r1.Certificate!, root, intermediate);
            var v2 = Verify(r2.Certificate!, root, intermediate);
            report["cluster"] = new JsonObject
            {
                ["sharedFolder"] = shared,
                ["root"] = root.Subject + " " + root.Thumbprint,
                ["server1"] = r1.ToJson(),
                ["server1ChainsToSharedRoot"] = v1,
                ["server2"] = r2.ToJson(),
                ["server2ChainsToSharedRoot"] = v2,
                ["server1LocalPki"] = Directory.Exists(Path.Combine(one.S.Paths.CaddyStorageDir, "pki")),
                ["server2LocalPki"] = Directory.Exists(Path.Combine(two.S.Paths.CaddyStorageDir, "pki")),
                ["sharedCertificates"] = new JsonArray(Directory.EnumerateDirectories(Path.Combine(shared, "certificates"), "*", SearchOption.AllDirectories)
                    .Select(d => (JsonNode)Path.GetRelativePath(shared, d).Replace('\\', '/')).ToArray()),
            };
            Assert.True(v1 && v2, "certificates do not chain to the shared root: " + report["cluster"]!.ToJsonString()); // (2)
            Assert.False(Directory.Exists(Path.Combine(one.S.Paths.CaddyStorageDir, "pki")));
            Assert.False(Directory.Exists(Path.Combine(two.S.Paths.CaddyStorageDir, "pki")));

            // ---- part 2: an existing server with local storage switches to a shared folder through the settings API
            var target = Path.Combine(Path.GetDirectoryName(shared)!, "switch-" + Guid.NewGuid().ToString("N")[..6]);
            await using var api = ApiHost.Start(installBinary: true);
            using var caddy = new CaddyProcess(api.Env.Paths);
            var adminPort = int.Parse(api.Store.GetSettings<CaddySettings>().AdminListen.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            await Wait.Until(() => Task.FromResult(CanConnect(adminPort)), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + caddy.Output);
            var httpsPort = Net.FreeTcpPort();
            var put = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { httpPort = Net.FreeTcpPort(), httpsPort });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var post = await api.SendAsync(HttpMethod.Post, "/api/hosts", new { kind = "response", domains = new[] { "copy.test" }, tls = "internal", responseStatus = 200, responseBody = "copy" });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var before = await TlsProbe.UntilAsync(httpsPort, "copy.test", r => r.Status == 200, TimeSpan.FromSeconds(30));
            Assert.Equal(200, before.Status);
            var localRoot = File.ReadAllText(Path.Combine(api.Env.Paths.CaddyStorageDir, "pki", "authorities", "local", "root.crt"));

            var switchResponse = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = target });
            var switchBody = JsonNode.Parse(await switchResponse.Content.ReadAsStringAsync())!;
            Assert.True(switchResponse.StatusCode == HttpStatusCode.OK, switchBody.ToJsonString());
            var warnings = switchBody["apply"]!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
            var copyWarning = warnings.FirstOrDefault(w => w.StartsWith("Copied", StringComparison.Ordinal));
            Assert.NotNull(copyWarning); // (5)
            Assert.Contains("certificates/", copyWarning);
            Assert.Contains("pki/", copyWarning);
            Assert.Equal(localRoot, File.ReadAllText(Path.Combine(target, "pki", "authorities", "local", "root.crt"))); // (4)
            var after = await TlsProbe.UntilAsync(httpsPort, "copy.test", r => r.Status == 200, TimeSpan.FromSeconds(30));
            var sameRoot = X509Certificate2.CreateFromPem(localRoot);
            var afterIntermediate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(target, "pki", "authorities", "local", "intermediate.crt")));
            var afterOk = after.Certificate is not null && Verify(after.Certificate, sameRoot, afterIntermediate);
            var inventory = JsonNode.Parse(await (await api.SendAsync(HttpMethod.Get, "/api/certificates")).Content.ReadAsStringAsync())!.AsArray();
            var internalRoot = await api.SendAsync(HttpMethod.Get, "/api/certificates/internal-root");
            report["switch"] = new JsonObject
            {
                ["target"] = target,
                ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray()),
                ["copiedFolders"] = new JsonArray(Directory.EnumerateDirectories(target).Select(d => (JsonNode)Path.GetFileName(d)).ToArray()),
                ["rootKept"] = true,
                ["handshakeAfter"] = after.ToJson(),
                ["chainsToOriginalRoot"] = afterOk,
                ["inventoryPaths"] = new JsonArray(inventory.Where(c => c!["kind"]!.GetValue<string>() != "custom").Select(c => (JsonNode)(c!["certPath"]?.GetValue<string>() ?? "")).ToArray()),
                ["internalRootDownload"] = (int)internalRoot.StatusCode,
            };
            Assert.True(afterOk, "after the switch the certificate does not chain to the original internal root");
            Assert.All(inventory.Where(c => c!["kind"]!.GetValue<string>() != "custom"), c => Assert.StartsWith(target, c!["certPath"]!.GetValue<string>()));
            Assert.Equal(HttpStatusCode.OK, internalRoot.StatusCode);
            Assert.Equal(localRoot.Trim(), (await internalRoot.Content.ReadAsStringAsync()).Trim());

            // Switching back and forth never overwrites existing folders at the destination.
            var marker = Path.Combine(target, "certificates", "cpm-marker.txt");
            File.WriteAllText(marker, "keep");
            Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "local" })).StatusCode);
            var again = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { storageBackend = "fileSystem", storagePath = target });
            var againBody = JsonNode.Parse(await again.Content.ReadAsStringAsync())!;
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.DoesNotContain(againBody["apply"]!["warnings"]!.AsArray(), w => w!.GetValue<string>().StartsWith("Copied", StringComparison.Ordinal));
            Assert.True(File.Exists(marker));
            report["secondSwitchCopiedNothing"] = true;
            report["passed"] = true;
        }
        finally
        {
            E2EArtifacts.Write("shared-storage.json", report);
        }
    }

    private static bool Verify(X509Certificate2 leaf, X509Certificate2 root, X509Certificate2 intermediate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
