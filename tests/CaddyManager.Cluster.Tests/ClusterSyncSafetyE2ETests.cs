using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end, primary + node with real Caddy processes: replication never leaves the node's store holding configuration
/// its Caddy does not run and never replicates configuration the primary has not committed.
/// <list type="bullet">
/// <item>A bundle that needs a Caddy rebuild is staged until the rebuild succeeded; a second revision during the rebuild
/// takes its place; a failed rebuild restores the desired plugins, is retried with back-off and alerts once (no
/// Warning/Recovered pairs). Writes e2e-artifacts/cluster-rebuild-e2e.json.</item>
/// <item>The node applies a sync under the configuration mutation lock; the primary builds bundles only from committed
/// configuration; the server-details query never runs a sync inline. Writes e2e-artifacts/cluster-mutation-lock-e2e.json.</item>
/// <item>A certificate the primary cannot read for a moment is kept by the nodes; a custom ACME CA root file is replicated
/// as content and written to each node's data folder. Writes e2e-artifacts/cluster-material-e2e.json.</item>
/// </list>
/// </summary>
public sealed class ClusterSyncSafetyE2ETests(ITestOutputHelper output)
{
    private const string Plugin = "github.com/caddy-dns/cloudflare";

    [Fact]
    public async Task NodeRebuildIsStagedBackedOffAndNeverTouchesTheStore()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        await using var upstream = await ClusterE2ETests.Upstream.StartAsync();
        using var proxy = new HangingProxy();
        await RunAsync("cluster-rebuild-e2e.json", nameof(NodeRebuildIsStagedBackedOffAndNeverTouchesTheStore), async (report, step, managers) =>
        {
            var p = Manager.CreateAsync("primary-rb", clusterOptions: o => o.FailedSyncRetry = TimeSpan.FromSeconds(1));
            var n = Manager.CreateAsync("node-rb", clusterOptions: o =>
            {
                o.RebuildRetryBackoff = TimeSpan.FromSeconds(3);
                o.RebuildRetryMaxBackoff = TimeSpan.FromSeconds(30);
                o.PendingJobPoll = TimeSpan.FromMilliseconds(200);
            });
            var P = await Track(managers, p);
            var N = await Track(managers, n);
            var nodeId = await JoinAsync(P, N);
            var worker = P.Services.GetRequiredService<ClusterWorker>();
            var jobs = N.Services.GetRequiredService<IJobRunner>();
            int RebuildJobs() => jobs.Recent(100).Count(j => j.Kind == "caddy-install");
            // The node's outbound proxy accepts connections and never answers: the Caddy build download hangs (like a node
            // behind a proxy that does not let caddyserver.com through).
            var binary = N.Store.GetSettings<BinarySettings>();
            binary.OutboundProxy = $"http://127.0.0.1:{proxy.Port}";
            N.Store.SaveSettings(binary);
            // The node's store must never hold content its Caddy does not run (checked continuously in the background).
            var storeViolations = new List<string>();
            using var watching = new CancellationTokenSource();
            var watcher = Task.Run(async () =>
            {
                // Until the plugin requirement is dropped (last step) the node runs no revision containing the rebuild hosts.
                while (!watching.IsCancellationRequested)
                {
                    if (N.Store.Col<SiteHost>().FindAll().SelectMany(h => h.Domains).FirstOrDefault(d => d.StartsWith("rebuild")) is { } domain)
                        storeViolations.Add($"{DateTime.UtcNow:HH:mm:ss.fff} node store holds {domain} while it runs {N.Store.GetSettings<ClusterSettings>().AppliedRevision}");
                    await Task.Delay(50);
                }
            });

            string r1 = "";
            await step("a revision that needs a plugin is staged while Caddy is rebuilt: the store is untouched, no alert", async () =>
            {
                var plugins = P.Store.GetSettings<BinarySettings>();
                plugins.Plugins = [Plugin];
                P.Store.SaveSettings(plugins);
                await AddHost(P, "rebuild1.cluster.test", upstream.Port);
                r1 = (await worker.CurrentBundleAsync()).Revision;
                await Wait.UntilAsync(() => Task.FromResult(N.Store.GetSettings<ClusterSettings>().PendingRevision == r1 && jobs.IsRunning("caddy-install")),
                    TimeSpan.FromSeconds(30), () => "node pending on a running rebuild\n" + N.LogTail());
                Assert.DoesNotContain(N.Store.Col<SiteHost>().FindAll(), h => h.Domains.Contains("rebuild1.cluster.test"));
                Assert.Equal(new[] { Plugin }, N.Store.GetSettings<BinarySettings>().Plugins); // what the running job builds
                Assert.Empty(Events(P, "server-sync:" + nodeId));
                var summary = await Server(P, nodeId);
                Assert.Contains(summary.GetProperty("sync").GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("rebuilding Caddy"));
                return new JsonObject { ["pendingRevision"] = r1, ["status"] = summary.GetProperty("status").GetString(), ["rebuildJobs"] = RebuildJobs() };
            });

            string r2 = "";
            await step("a second revision during the rebuild takes the place of the first (pending, no failure, no alert)", async () =>
            {
                await AddHost(P, "rebuild2.cluster.test", upstream.Port);
                r2 = (await worker.CurrentBundleAsync()).Revision;
                Assert.NotEqual(r1, r2);
                // Before the fix the node stored R2, answered "job already running" (Failed) and the primary raised configFailure.
                await Wait.UntilAsync(() => Task.FromResult(N.Store.GetSettings<ClusterSettings>().PendingRevision == r2),
                    TimeSpan.FromSeconds(30), () => "second revision staged\n" + N.LogTail() + "\n" + P.LogTail());
                Assert.Empty(Events(P, "server-sync:" + nodeId));
                Assert.Equal(1, RebuildJobs());
                Assert.DoesNotContain(N.Store.Col<SiteHost>().FindAll(), h => h.Domains.Any(d => d.StartsWith("rebuild")));
                return new JsonObject { ["pendingRevision"] = r2, ["rebuildJobs"] = RebuildJobs(), ["syncEvents"] = 0 };
            });

            await step("the rebuild fails: one alert, desired plugins restored, retried with back-off — no Warning/Recovered pairs", async () =>
            {
                proxy.Stop(); // the hanging download fails; later attempts get "connection refused"
                var warning = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Warning)),
                    TimeSpan.FromSeconds(30), () => "server-sync warning after the failed rebuild\n" + N.LogTail() + "\n" + P.LogTail());
                var nodeState = N.Store.GetSettings<ClusterSettings>();
                Assert.Null(nodeState.PendingRevision);
                Assert.Contains("Rebuilding Caddy with the required plugins failed", nodeState.LastSyncError);
                Assert.Empty(N.Store.GetSettings<BinarySettings>().Plugins);
                // Let back-off retries happen (3 s, then 9 s): the primary re-pushes every second (FailedSyncRetry), the node
                // only rebuilds when the back-off allows it, and a retry that is pending is not reported as "works again".
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(200);
                var events = Events(P, "server-sync:" + nodeId);
                var jobsStarted = RebuildJobs();
                Assert.DoesNotContain(events, e => e.Severity == EventSeverity.Recovered);
                Assert.InRange(jobsStarted, 2, 3);
                Assert.True(events.Count(e => e.Severity == EventSeverity.Warning) <= 2,
                    "one warning per distinct error: " + string.Join(" | ", events.Select(e => e.Details)));
                return new JsonObject
                {
                    ["firstWarning"] = warning.Details, ["nodeError"] = nodeState.LastSyncError, ["rebuildJobsIn8s"] = jobsStarted,
                    ["warnings"] = events.Count(e => e.Severity == EventSeverity.Warning), ["recovered"] = 0,
                };
            });

            await step("the plugin requirement is removed on the primary: the node applies everything, Recovered", async () =>
            {
                watching.Cancel();
                await watcher;
                Assert.Empty(storeViolations);
                var plugins = P.Store.GetSettings<BinarySettings>();
                plugins.Plugins = [];
                P.Store.SaveSettings(plugins);
                worker.RequestSync();
                var synced = await WaitInSync(P, nodeId, null, "in sync without the plugin");
                var recovered = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Recovered)),
                    TimeSpan.FromSeconds(30), () => "server-sync Recovered");
                var (code, _) = await WaitHttp(N.HttpPort, "rebuild2.cluster.test", "/rebuilt");
                return new JsonObject
                {
                    ["revision"] = Rev(synced), ["recoveredEvent"] = recovered.Message, ["nodeServesRebuild2"] = code, ["storeViolations"] = storeViolations.Count,
                };
            });
            report["nodeProxy"] = "hanging TCP listener, then closed";
        });
    }

    [Fact]
    public async Task ReplicationRespectsTheConfigMutationLockAndDetailQueriesNeverSyncInline()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        await using var upstream = await ClusterE2ETests.Upstream.StartAsync();
        await RunAsync("cluster-mutation-lock-e2e.json", nameof(ReplicationRespectsTheConfigMutationLockAndDetailQueriesNeverSyncInline), async (report, step, managers) =>
        {
            // No background pushes: every push in this test is either explicit (Sync now) or queued by the detail query.
            void Quiet(ClusterOptions o)
            {
                o.HeartbeatInterval = TimeSpan.FromMinutes(10);
                o.SyncDebounce = TimeSpan.FromMinutes(10);
                o.SyncTimeout = TimeSpan.FromSeconds(60);
            }
            var p = Manager.CreateAsync("primary-ml", clusterOptions: Quiet);
            var n = Manager.CreateAsync("node-ml");
            var P = await Track(managers, p);
            var N = await Track(managers, n);
            report["testConfigMutationLock"] = P.UsesTestMutationLock;
            var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-ml", url = N.Url }).OkJsonAsync("POST /api/servers");
            var nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
            await N.Api.PostAsJsonAsync("api/cluster/join", new { token = added.GetProperty("joinToken").GetString() }).OkJsonAsync("join");
            await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("initial Sync now");
            var initial = await WaitInSync(P, nodeId, null, "initial sync");

            await step("server details of a stale node answer at once (no inline sync); the queued push waits for the node's mutation lock", async () =>
            {
                var nodeLock = await N.Services.GetRequiredService<IConfigMutationLock>().AcquireAsync();
                try
                {
                    await AddHost(P, "locked.cluster.test", upstream.Port);
                    var watch = Stopwatch.StartNew();
                    var detail = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}");
                    var detailMs = watch.ElapsedMilliseconds;
                    // Before the fix the viewer's request ran the sync itself and waited for the node's apply (here: the lock).
                    Assert.True(detailMs < 3000, $"GET /api/servers/{{id}} took {detailMs} ms");
                    Assert.False(detail.GetProperty("sync").GetProperty("inSync").GetBoolean());
                    // The push queued by the query reaches the node but waits for the lock: nothing is stored meanwhile.
                    var applied = N.Store.GetSettings<ClusterSettings>().AppliedRevision;
                    watch.Restart();
                    while (watch.Elapsed < TimeSpan.FromSeconds(2))
                    {
                        Assert.DoesNotContain(N.Store.Col<SiteHost>().FindAll(), h => h.Domains.Contains("locked.cluster.test"));
                        Assert.Equal(applied, N.Store.GetSettings<ClusterSettings>().AppliedRevision);
                        await Task.Delay(100);
                    }
                    nodeLock.Dispose();
                    var synced = await WaitInSync(P, nodeId, Rev(initial), "queued push completes after the lock is released");
                    var (code, _) = await WaitHttp(N.HttpPort, "locked.cluster.test", "/after-lock");
                    return new JsonObject { ["detailMs"] = detailMs, ["storeUnchangedWhileLockedMs"] = 2000, ["revision"] = Rev(synced), ["nodeServes"] = code };
                }
                finally
                {
                    nodeLock.Dispose();
                }
            });

            await step("a change being applied on the primary (mutation lock held) is not replicated; the last committed bundle is used", async () =>
            {
                var committed = Rev(await Server(P, nodeId));
                var primaryLock = await P.Services.GetRequiredService<IConfigMutationLock>().AcquireAsync();
                var uncommitted = new SiteHost
                {
                    Kind = HostKind.Response, Domains = ["uncommitted.cluster.test"], Tls = TlsMode.None, ResponseStatus = 200, ResponseBody = "uncommitted",
                };
                try
                {
                    // What a ConfigTransaction does between persist and apply/rollback.
                    P.Store.Col<SiteHost>().Insert(uncommitted);
                    var watch = Stopwatch.StartNew();
                    var pushed = await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("Sync now while a change is being applied");
                    var pushMs = watch.ElapsedMilliseconds;
                    Assert.Equal(committed, Rev(pushed));
                    Assert.DoesNotContain(N.Store.Col<SiteHost>().FindAll(), h => h.Domains.Contains("uncommitted.cluster.test"));
                    // Rolled back (Caddy rejected it on the primary).
                    P.Store.Col<SiteHost>().Delete(uncommitted.Id);
                    primaryLock.Dispose();
                    var after = await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("Sync now after the rollback");
                    Assert.Equal(committed, Rev(after));
                    Assert.DoesNotContain(N.Store.Col<SiteHost>().FindAll(), h => h.Domains.Contains("uncommitted.cluster.test"));
                    return new JsonObject { ["committedRevision"] = committed, ["pushedWhileLocked"] = Rev(pushed), ["pushMs"] = pushMs };
                }
                finally
                {
                    primaryLock.Dispose();
                }
            });
        });
    }

    [Fact]
    public async Task UnreadableCertificateIsKeptAndCustomAcmeRootIsReplicated()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        await using var upstream = await ClusterE2ETests.Upstream.StartAsync();
        await RunAsync("cluster-material-e2e.json", nameof(UnreadableCertificateIsKeptAndCustomAcmeRootIsReplicated), async (report, step, managers) =>
        {
            var p = Manager.CreateAsync("primary-mat");
            var n = Manager.CreateAsync("node-mat");
            var P = await Track(managers, p);
            var N = await Track(managers, n);
            var nodeId = await JoinAsync(P, N);
            var worker = P.Services.GetRequiredService<ClusterWorker>();

            await step("a certificate whose files the primary cannot read for a moment is kept (and served) by the node", async () =>
            {
                using var cert = SelfSigned("keep.cluster.test");
                var created = await P.Api.PostAsJsonAsync("api/certificates/pem", new
                {
                    name = "keep", certPem = cert.ExportCertificatePem(), keyPem = cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem(),
                }).OkJsonAsync("POST /api/certificates/pem");
                var certId = created.GetProperty("item").GetProperty("id").GetString()!;
                await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { "keep.cluster.test" }, tls = "custom", certificateId = certId, compression = false,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts (custom TLS)");
                var withCert = (await worker.CurrentBundleAsync()).Revision;
                await Wait.UntilAsync(async () => Rev(await Server(P, nodeId)) == withCert, SyncTimeout, () => "certificate replicated");
                await Wait.UntilAsync(async () => (await TlsGet(N.HttpsPort, "keep.cluster.test", "/tls")).Thumbprint == cert.Thumbprint,
                    TimeSpan.FromSeconds(30), () => "node serves the certificate");

                // The key file is briefly unreadable on the primary (renewal in progress, share unreachable...).
                var keyPath = P.Store.Col<Certificate>().FindById(certId).KeyPath;
                File.Move(keyPath, keyPath + ".away");
                var flagged = await Wait.ForValueAsync(async () =>
                {
                    var r = (await worker.CurrentBundleAsync()).Revision;
                    var s = await Server(P, nodeId);
                    return (r != withCert && Rev(s) == r && s.GetProperty("sync").GetProperty("inSync").GetBoolean(), s);
                }, SyncTimeout, () => "revision without readable material synced\n" + P.LogTail());
                Assert.Contains(flagged.GetProperty("sync").GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("could not be read on the primary"));
                // Before the fix the node deleted the certificate and dropped the site.
                var nodeCerts = await N.Api.GetFromJsonAsync<JsonElement>("api/certificates");
                Assert.Contains(nodeCerts.EnumerateArray(), c => c.GetProperty("id").GetString() == certId);
                var (thumbprint, body) = await TlsGet(N.HttpsPort, "keep.cluster.test", "/still-tls");
                Assert.Equal(cert.Thumbprint, thumbprint);
                Assert.Contains("upstream-ok /still-tls", body);

                File.Move(keyPath + ".away", keyPath);
                await Wait.UntilAsync(async () => Rev(await Server(P, nodeId)) == withCert, SyncTimeout, () => "readable again, original revision synced");
                Assert.Equal(cert.Thumbprint, (await TlsGet(N.HttpsPort, "keep.cluster.test", "/back")).Thumbprint);
                return new JsonObject
                {
                    ["certificateId"] = certId, ["revisionWithCertificate"] = withCert, ["revisionWhileUnreadable"] = Rev(flagged),
                    ["nodeKeptCertificate"] = true, ["servedThumbprint"] = thumbprint,
                };
            });

            await step("a custom ACME CA root is replicated as content into the node's data folder; a node-local path wins", async () =>
            {
                using var ca = SelfSigned("Cluster Test Root CA");
                var rootPem = ca.ExportCertificatePem();
                var primaryRoot = Path.Combine(P.DataDir, "corp-root.pem");
                await File.WriteAllTextAsync(primaryRoot, rootPem);
                await P.Api.PutAsJsonAsync("api/settings/caddy", new
                {
                    acmeCa = "custom", customAcmeDirectory = "https://127.0.0.1:1/acme/directory", customAcmeRootPath = primaryRoot,
                }).OkJsonAsync("PUT /api/settings/caddy (custom ACME CA)");
                await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "response", domains = new[] { "acme-root.cluster.test" }, tls = "acme", responseStatus = 200, responseBody = "acme",
                }).OkJsonAsync("POST /api/hosts (ACME)");
                var revision = (await worker.CurrentBundleAsync()).Revision;
                var synced = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    return (Rev(s) == revision && s.GetProperty("sync").GetProperty("inSync").GetBoolean(), s);
                }, SyncTimeout, () => "custom CA synced\n" + P.LogTail());
                var managed = Path.Combine(N.DataDir, "caddy", "cluster-acme-root.pem");
                var nodeSettings = await N.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy");
                // Before the fix the node had no root path (node-local, empty), so its Caddy did not trust the custom CA.
                Assert.Equal(managed, nodeSettings.GetProperty("customAcmeRootPath").GetString());
                Assert.Equal(rootPem, await File.ReadAllTextAsync(managed));
                var nodeConfig = await File.ReadAllTextAsync(N.Paths.CaddyConfigFile);
                Assert.Contains("trusted_roots_pem_files", nodeConfig);
                Assert.Contains(JsonSerializer.Serialize(managed).Trim('"'), nodeConfig);

                // A node administrator may point the node at a local copy: kept across syncs.
                var own = Path.Combine(N.DataDir, "own-root.pem");
                await File.WriteAllTextAsync(own, rootPem);
                await N.Api.PutAsJsonAsync("api/settings/caddy", new { customAcmeRootPath = own }).OkJsonAsync("PUT /api/settings/caddy (node-local root path)");
                await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("Sync now");
                var after = await N.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy");
                Assert.Equal(own, after.GetProperty("customAcmeRootPath").GetString());
                return new JsonObject
                {
                    ["revision"] = Rev(synced), ["nodeRootPath"] = managed, ["nodeConfigTrustsRoot"] = true, ["nodeLocalOverride"] = own,
                };
            });
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> JoinAsync(Manager p, Manager n)
    {
        var added = await p.Api.PostAsJsonAsync("api/servers", new { name = n.Name, url = n.Url }).OkJsonAsync("POST /api/servers");
        var nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
        await n.Api.PostAsJsonAsync("api/cluster/join", new { token = added.GetProperty("joinToken").GetString() }).OkJsonAsync("join");
        await WaitInSync(p, nodeId, null, "initial sync");
        return nodeId;
    }

    private static Task AddHost(Manager p, string domain, int upstreamPort) =>
        p.Api.PostAsJsonAsync("api/hosts", new
        {
            kind = "proxy", domains = new[] { domain }, tls = "none", compression = false,
            upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstreamPort } },
        }).OkJsonAsync("POST /api/hosts " + domain);

    private delegate Task StepRunner(string name, Func<Task<JsonObject>> body);

    private static async Task<Manager> Track(List<Manager> managers, Task<Manager> starting)
    {
        var m = await starting;
        lock (managers) managers.Add(m);
        return m;
    }

    /// <summary>
    /// Runs a test body with steps recorded into e2e-artifacts/<paramref name="artifact"/>; the managers the body registers
    /// (Track) are disposed afterwards, and their log tails are added to the report when the body failed.
    /// </summary>
    private async Task RunAsync(string artifact, string test, Func<JsonObject, StepRunner, List<Manager>, Task> body)
    {
        var report = E2EArtifacts.Report(nameof(ClusterSyncSafetyE2ETests) + "." + test);
        var steps = new JsonArray();
        report["steps"] = steps;
        var clock = Stopwatch.StartNew();
        var managers = new List<Manager>();
        async Task Step(string name, Func<Task<JsonObject>> run)
        {
            var sw = Stopwatch.StartNew();
            var entry = new JsonObject { ["step"] = name };
            steps.Add(entry);
            try
            {
                entry["verified"] = await run();
                entry["ok"] = true;
                output.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {name}");
            }
            catch
            {
                entry["ok"] = false;
                throw;
            }
            finally
            {
                entry["ms"] = sw.ElapsedMilliseconds;
            }
        }
        try
        {
            await body(report, Step, managers);
            report["result"] = "passed";
        }
        catch (Exception ex)
        {
            report["result"] = "failed";
            report["error"] = ex.ToString();
            foreach (var m in managers) report[m.Name + "Log"] = m.LogTail(150);
            throw;
        }
        finally
        {
            report["totalMs"] = clock.ElapsedMilliseconds;
            output.WriteLine("Artifact: " + E2EArtifacts.Write(artifact, report));
            foreach (var m in managers) await m.DisposeAsync();
        }
    }

    /// <summary>A TCP listener that accepts connections and never answers (an outbound proxy that lets nothing through).</summary>
    private sealed class HangingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = new();
        private readonly CancellationTokenSource _stop = new();

        public HangingProxy()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        lock (_clients) _clients.Add(client);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            });
        }

        public int Port { get; }

        /// <summary>Closes the listener and every accepted connection: waiting requests fail, new ones are refused.</summary>
        public void Stop()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var c in _clients) c.Dispose();
                _clients.Clear();
            }
        }

        public void Dispose() => Stop();
    }
}
