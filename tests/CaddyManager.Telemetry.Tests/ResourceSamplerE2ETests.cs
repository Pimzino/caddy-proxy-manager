using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Resources;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// End to end: the sampler runs against a real Caddy process under load with keep-alive connections held open, then
/// Caddy is killed. Checks the SPEC invariants of every sample and the Caddy/connection/request-rate values that depend on
/// the live process. Artifact: resource-samples.json.
/// </summary>
public sealed class ResourceSamplerE2ETests
{
    private const int HeldConnections = 8;

    [CaddyFact]
    public async Task SamplesRealCaddyUnderLoad_AndDropsCaddyValuesAfterStop()
    {
        var report = E2EArtifacts.Report(nameof(SamplesRealCaddyUnderLoad_AndDropsCaddyValuesAfterStop));
        using var env = new TempEnv();
        var port = Net.FreeTcpPort();
        var settings = env.Store.GetSettings<CaddySettings>();
        settings.HttpPort = port;
        settings.HttpsPort = Net.FreeTcpPort();
        env.Store.SaveSettings(settings);

        var config = StatsConfig.Build(env.Paths, port, new JsonArray(StatsConfig.StaticRoute(200, new string('x', 2048))));
        using var caddy = new CaddyProcess(env.Paths, config);
        await caddy.WaitForPortAsync(port, TimeSpan.FromSeconds(20));

        var host = new FakeCaddyHost
        {
            Status = new CaddyStatus { State = CaddyRunState.Running, ProcessId = caddy.Pid, StartedAt = caddy.StartedAt, Version = "v2.11.4", BinaryInstalled = true },
        };
        var binaries = new FakeBinaryManager(new InstalledBinary { Version = "v2.11.4", Plugins = ["github.com/caddy-dns/cloudflare"] });
        await using var sp = env.Services(o =>
        {
            o.SampleInterval = TimeSpan.FromMilliseconds(500);
            o.CaddyStatusCacheDuration = TimeSpan.FromMilliseconds(200);
            o.IngestInterval = TimeSpan.FromMilliseconds(200);
        }, host, binaries);
        var sampler = sp.GetRequiredService<ResourceSampler>();
        var ingester = sp.GetRequiredService<StatsIngesterService>();
        var telemetry = sp.GetRequiredService<IServerTelemetry>();
        await ingester.StartAsync(CancellationToken.None);
        await sampler.StartAsync(CancellationToken.None);

        // Keep-alive connections held open for the whole loaded phase.
        var held = new List<TcpClient>();
        for (var i = 0; i < HeldConnections; i++)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("GET /held HTTP/1.1\r\nHost: held.test\r\n\r\n"));
            var buf = new byte[4096];
            var read = 0;
            while (read < 2048) { var n = await s.ReadAsync(buf); if (n == 0) break; read += n; }
            held.Add(c);
        }

        // Load for ~5 s.
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 8, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        long sent = 0;
        var loadUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (_, ct) =>
        {
            while (DateTime.UtcNow < loadUntil)
            {
                using var r = await http.GetAsync("/load", ct);
                await r.Content.ReadAsByteArrayAsync(ct);
                Interlocked.Increment(ref sent);
            }
        });
        await Task.Delay(TimeSpan.FromSeconds(2.5)); // let the request-rate window (ends IngestInterval + 1 s back) cover the load
        var loaded = telemetry.GetSamples();
        var info = await telemetry.GetInfoAsync();

        foreach (var c in held) c.Dispose();
        caddy.Kill();
        // The host keeps reporting the dead PID: the sampler must notice the process is gone by itself.
        var killedAt = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var afterStop = telemetry.GetSamples(killedAt.AddMilliseconds(600));
        var since = telemetry.GetSamples(loaded[^1].At);

        await sampler.StopAsync(CancellationToken.None);
        await ingester.StopAsync(CancellationToken.None);

        var gcTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        report["requestsSent"] = sent;
        report["heldConnections"] = HeldConnections;
        report["gcTotalAvailableMemoryBytes"] = gcTotal;
        report["info"] = new JsonObject
        {
            ["hostname"] = info.Hostname, ["fqdn"] = info.Fqdn, ["os"] = info.Os, ["architecture"] = info.Architecture,
            ["managerVersion"] = info.ManagerVersion, ["caddyVersion"] = info.CaddyVersion, ["caddyState"] = info.CaddyState.ToString(),
            ["caddyPlugins"] = new JsonArray(info.CaddyPlugins.Select(p => (JsonNode)p).ToArray()),
            ["processorCount"] = info.ProcessorCount, ["totalMemoryBytes"] = info.TotalMemoryBytes,
            ["systemUptimeSeconds"] = info.SystemUptimeSeconds, ["managerUptimeSeconds"] = info.ManagerUptimeSeconds,
            ["ipAddresses"] = new JsonArray(info.IpAddresses.Select(p => (JsonNode)p).ToArray()), ["domain"] = info.Domain,
        };
        report["loadedSamples"] = new JsonArray(loaded.Select(Json).ToArray());
        report["afterStopSamples"] = new JsonArray(afterStop.Select(Json).ToArray());
        E2EArtifacts.Write("resource-samples.json", report);

        Assert.True(loaded.Count >= 8, $"expected ≥ 8 samples, got {loaded.Count}");
        Assert.True(loaded.Zip(loaded.Skip(1)).All(p => p.First.At < p.Second.At), "samples oldest first");
        foreach (var s in loaded.Concat(afterStop))
        {
            Assert.InRange(s.CpuPercent, 0, 100);
            Assert.True(s.MemoryTotalBytes > 0);
            Assert.InRange(s.MemoryUsedBytes, 1, s.MemoryTotalBytes);
            Assert.InRange(s.MemoryTotalBytes, gcTotal * 0.9, gcTotal * 1.1);
            Assert.True(s.ManagerMemoryBytes > 0);
            Assert.InRange(s.ManagerCpuPercent, 0, 100);
            Assert.True(s.NetworkRxBytesPerSec >= 0 && s.NetworkTxBytesPerSec >= 0);
            Assert.Contains(s.Disks, d => d.Label.Contains("Data (") && d.TotalBytes > 0 && d.FreeBytes <= d.TotalBytes);
        }
        Assert.Contains(loaded, s => s.CpuPercent > 0);
        foreach (var s in loaded)
        {
            Assert.NotNull(s.CaddyCpuPercent);
            Assert.InRange(s.CaddyCpuPercent!.Value, 0, 100);
            Assert.True(s.CaddyMemoryBytes > 0);
        }
        Assert.Contains(loaded, s => s.CaddyCpuPercent > 0);
        // The held keep-alive connections (plus the load connections) are established to Caddy's HTTP port.
        Assert.Contains(loaded, s => s.ActiveConnections >= HeldConnections);
        // Request rate (from the stats log timestamps) agrees with what the client achieved on average.
        var achieved = sent / 5.0;
        report["achievedRequestsPerSecond"] = Math.Round(achieved, 1);
        Assert.Contains(loaded, s => s.RequestsPerSecond > achieved * 0.3);
        Assert.All(loaded, s => Assert.InRange(s.RequestsPerSecond, 0, achieved * 3));
        Assert.NotEmpty(afterStop);
        Assert.All(afterStop, s => Assert.Null(s.CaddyCpuPercent));
        Assert.All(afterStop, s => Assert.Null(s.CaddyMemoryBytes));
        Assert.All(since, s => Assert.True(s.At > loaded[^1].At));

        Assert.Equal(Environment.ProcessorCount, info.ProcessorCount);
        Assert.InRange(info.TotalMemoryBytes, gcTotal * 0.9, gcTotal * 1.1);
        Assert.Equal("v2.11.4", info.CaddyVersion);
        Assert.Equal(CaddyRunState.Running, info.CaddyState);
        Assert.Equal(["github.com/caddy-dns/cloudflare"], info.CaddyPlugins);
        Assert.False(string.IsNullOrWhiteSpace(info.Hostname));
        Assert.Equal(env.Paths.DataDir, info.DataDir);
        Assert.True(info.SystemUptimeSeconds > 0);
        E2EArtifacts.Write("resource-samples.json", report);
    }

    private static JsonNode Json(ResourceSample s) => new JsonObject
    {
        ["at"] = s.At.ToString("O"),
        ["cpu"] = s.CpuPercent,
        ["memUsed"] = s.MemoryUsedBytes,
        ["memTotal"] = s.MemoryTotalBytes,
        ["rx"] = s.NetworkRxBytesPerSec,
        ["tx"] = s.NetworkTxBytesPerSec,
        ["caddyCpu"] = s.CaddyCpuPercent,
        ["caddyMem"] = s.CaddyMemoryBytes,
        ["managerCpu"] = s.ManagerCpuPercent,
        ["managerMem"] = s.ManagerMemoryBytes,
        ["connections"] = s.ActiveConnections,
        ["rps"] = s.RequestsPerSecond,
        ["disks"] = new JsonArray(s.Disks.Select(d => (JsonNode)$"{d.Name} [{d.Label}] {d.FreeBytes}/{d.TotalBytes}").ToArray()),
    };
}
