using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>Skips unless the Caddy binary is present and this machine can speak QUIC (msquic).</summary>
public sealed class Http3FactAttribute : FactAttribute
{
    public Http3FactAttribute()
    {
        if (CaddyBinary.Path is null) Skip = "Caddy binary not found (.dev/bin/caddy or CPM_TEST_CADDY).";
#pragma warning disable CA2252, CA1416
        else if (!QuicConnection.IsSupported) Skip = "QUIC is not available (Windows Server 2022+/Windows 11, or libmsquic on macOS/Linux).";
#pragma warning restore CA2252, CA1416
    }
}

/// <summary>
/// End-to-end: browser-like HTTP/3 client → real Caddy (HTTP/3 enabled) → HTTP/2 HTTPS backend, recording exactly how
/// each request reached the backend. Regression for Nutanix Prism answering 503 "upstream connect error" when a GET
/// that arrived over HTTP/3 was forwarded as HEADERS without END_STREAM plus an empty DATA frame.
/// Failure modes covered: (1) bodiless GET over HTTP/3 reaches the backend with a body stream; (2) small POST body
/// altered; (3) large upload truncated or stalled by buffering; (4) HTTP/1.1 and HTTP/2 clients change behaviour;
/// (5) the generated config is rejected by Caddy; (6) the control proves the test detects the bug.
/// Writes a JSON artifact of every observation (CPM_E2E_ARTIFACTS or ./e2e-artifacts).
/// </summary>
public sealed class Http3UpstreamE2ETests
{
    private sealed record Seen(string Method, string Path, string Protocol, bool? CanHaveBody, string? ContentLength, int BodyBytes, string BodySha256);

    [Http3Fact]
    public async Task Http3_client_requests_reach_an_http2_upstream_like_http2_requests()
    {
        await using var backend = RawHttp2Backend.Start();
        using var s = new ConfigServices(installBinary: true);
        var httpsPort = Net.FreeTcpPort();
        s.Store.SaveSettings(new CaddySettings
        {
            AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}", HttpPort = Net.FreeTcpPort(), HttpsPort = httpsPort, EnableHttp3 = true,
        });
        var host = Build.Proxy("localhost", backend.Port, TlsMode.Internal);
        host.Upstreams[0].Scheme = UpstreamScheme.Https;
        host.UpstreamTlsInsecure = true;
        host.ForceHttps = false;
        s.Store.Col<SiteHost>().Insert(host);

        var generated = s.Config.Generate();
        if (Environment.GetEnvironmentVariable("CPM_E2E_DUMP") is { Length: > 0 } dump) File.WriteAllText(dump, generated.ToJson());
        Assert.Contains("\"request_buffers\"", generated.ToJson()); // (6b) the fix is present while HTTP/3 is on

        s.Config.EnsureBootConfig();
        using var caddy = new CaddyProcess(s.Paths);
        var admin = s.Provider.GetRequiredService<CaddyAdminClient>();
        for (var i = 0; i < 100 && !await admin.IsReachableAsync(); i++) await Task.Delay(200);
        var apply = await s.Config.ApplyAsync("http3 e2e");
        Assert.True(apply.Success, apply.Error + "\n" + caddy.Output); // (5)

        var report = new JsonObject { ["caddy"] = caddy.Output.Split('\n').FirstOrDefault(l => l.Contains("version")) ?? "", ["scenarios"] = new JsonArray() };
        var base_ = $"https://localhost:{httpsPort}";
        var small = RandomNumberGenerator.GetBytes(100);
        var large = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);

        // Wait until Caddy has the internal certificate and serves HTTP/3.
        // Wait until the internal certificate is served (HTTP/2), then until HTTP/3 answers. A fresh client per HTTP/3
        // attempt: .NET remembers a failed QUIC connection for the lifetime of a handler.
        Exception? last = null;
        var ready = false;
        for (var i = 0; i < 50 && !ready; i++)
        {
            try { using var c = Client(HttpVersion.Version20); using var r = await c.GetAsync(base_ + "/warmup"); ready = true; }
            catch (Exception e) { last = e; await Task.Delay(200); }
        }
        Assert.True(ready, $"Caddy never answered over HTTPS: {last?.InnerException?.Message ?? last?.Message}\n{ReadShared(s.Paths.CaddyProcessLog)}");
        ready = false;
        for (var i = 0; i < 50 && !ready; i++)
        {
            try { using var c = Client(HttpVersion.Version30); using var r = await c.GetAsync(base_ + "/warmup"); ready = r.Version == HttpVersion.Version30; }
            catch (Exception e) { last = e; await Task.Delay(200); }
        }
        Assert.True(ready, $"Caddy never answered over HTTP/3: {last?.InnerException?.Message ?? last?.Message}\n{ReadShared(s.Paths.CaddyProcessLog)}");
        backend.Drain();

        async Task<Seen> Send(string label, Version version, HttpMethod method, string path, byte[]? body = null)
        {
            using var c = Client(version);
            using var req = new HttpRequestMessage(method, base_ + path) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (body is not null) req.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
            using var res = await c.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{label}: {(int)res.StatusCode} {text}");
            Assert.Equal(version, res.Version); // the client really used the protocol under test
            var seen = await backend.NextAsync();
            ((JsonArray)report["scenarios"]!).Add(new JsonObject
            {
                ["scenario"] = label, ["clientProtocol"] = $"HTTP/{version}", ["upstreamProtocol"] = seen.Protocol,
                ["method"] = method.Method, ["upstreamHeadersEndStream"] = seen.CanHaveBody == false,
                ["upstreamBodyBytes"] = seen.BodyBytes, ["bodyIntact"] = body is null ? seen.BodyBytes == 0 : seen.BodySha256 == Sha(body),
            });
            return seen;
        }

        // (1) the Prism case: bodiless GET over HTTP/3 must reach the HTTP/2 upstream with END_STREAM on HEADERS.
        var getH3 = await Send("GET over HTTP/3", HttpVersion.Version30, HttpMethod.Get, "/api/get-h3");
        Assert.Equal("HTTP/2", getH3.Protocol);
        Assert.False(getH3.CanHaveBody, "GET over HTTP/3 reached the upstream with a body stream (HEADERS without END_STREAM).");
        // (2) small body intact, (3) large upload intact (streams past the 4 KB buffer).
        var postSmall = await Send("POST 100 B over HTTP/3", HttpVersion.Version30, HttpMethod.Post, "/api/post-small", small);
        Assert.Equal(Sha(small), postSmall.BodySha256);
        var postLarge = await Send("POST 1 MB over HTTP/3", HttpVersion.Version30, HttpMethod.Post, "/api/post-large", large);
        Assert.Equal(Sha(large), postLarge.BodySha256);
        // (4) HTTP/2 and HTTP/1.1 clients unchanged.
        Assert.False((await Send("GET over HTTP/2", HttpVersion.Version20, HttpMethod.Get, "/api/get-h2")).CanHaveBody);
        Assert.False((await Send("GET over HTTP/1.1", HttpVersion.Version11, HttpMethod.Get, "/api/get-h1")).CanHaveBody);
        Assert.Equal(Sha(large), (await Send("POST 1 MB over HTTP/2", HttpVersion.Version20, HttpMethod.Post, "/api/post-large-h2", large)).BodySha256);

        // (6) Control: the same config without request_buffers reproduces the bug, so this test can detect it.
        var json = JsonNode.Parse(generated.ToJson())!;
        foreach (var rp in Descendants(json).OfType<JsonObject>().Where(o => o["handler"]?.GetValue<string>() == "reverse_proxy").ToList())
            rp.Remove("request_buffers");
        await admin.LoadAsync(json.ToJsonString());
        var control = await Send("CONTROL: GET over HTTP/3 without the fix", HttpVersion.Version30, HttpMethod.Get, "/api/get-h3-control");
        Assert.True(control.CanHaveBody, "Control did not reproduce the HTTP/3 framing bug; the regression test would not catch it.");

        WriteArtifact("http3-upstream-framing.json", report);
        await admin.StopAsync();
    }

    [Fact]
    public void No_request_buffers_while_http3_is_off_and_http3_is_off_by_default()
    {
        Assert.False(new CaddySettings().EnableHttp3);
        using var env = new TempEnv();
        var h = Build.Proxy("app.example.com");
        var off = CaddyConfigGenerator.Generate(Build.Input(env.Paths, new CaddySettings(), hosts: [h])).ToJson();
        Assert.DoesNotContain("request_buffers", off);
        Assert.DoesNotContain("\"h3\"", off);
        var on = JsonNode.Parse(CaddyConfigGenerator.Generate(Build.Input(env.Paths, new CaddySettings { EnableHttp3 = true }, hosts: [h])).ToJson());
        var proxies = Descendants(on).OfType<JsonObject>().Where(o => o["handler"]?.GetValue<string>() == "reverse_proxy").ToList();
        Assert.NotEmpty(proxies);
        Assert.All(proxies, rp => Assert.Equal(CaddyConfigGenerator.Http3RequestBufferBytes, rp["request_buffers"]!.GetValue<int>()));
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode? n)
    {
        if (n is null) yield break;
        yield return n;
        if (n is JsonObject o) foreach (var (_, v) in o) foreach (var d in Descendants(v)) yield return d;
        if (n is JsonArray a) foreach (var v in a) foreach (var d in Descendants(v)) yield return d;
    }

    private static HttpClient Client(Version version) => new(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        UseProxy = false,
    })
    { DefaultRequestVersion = version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = TimeSpan.FromSeconds(30) };

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "(no Caddy log)";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static void WriteArtifact(string name, JsonNode report)
    {
        var dir = Environment.GetEnvironmentVariable("CPM_E2E_ARTIFACTS") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// HTTPS backend that only speaks HTTP/2 (like Nutanix Prism's Envoy on 9440) and records, per request, the raw
    /// framing: whether HEADERS carried END_STREAM, how many DATA bytes followed, and their SHA-256. Kestrel cannot be
    /// used as the witness: it reports "no body" whenever the empty DATA frame arrives before the app looks.
    /// Requests are attributed in completion order (the test sends one request at a time).
    /// </summary>
    private sealed class RawHttp2Backend : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Threading.Channels.Channel<Seen> _completed = System.Threading.Channels.Channel.CreateUnbounded<Seen>();
        public int Port { get; }

        private RawHttp2Backend(System.Net.Sockets.TcpListener l, X509Certificate2 cert, int port) { _listener = l; _cert = cert; Port = port; }

        public static RawHttp2Backend Start()
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=backend", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = X509CertificateLoader.LoadPkcs12(req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)).Export(X509ContentType.Pfx), null);
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var b = new RawHttp2Backend(l, cert, ((IPEndPoint)l.LocalEndpoint).Port);
            _ = b.AcceptLoop();
            return b;
        }

        /// <summary>Next completed request (waits up to 10 s).</summary>
        public async Task<Seen> NextAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _completed.Reader.ReadAsync(cts.Token);
        }

        public void Drain() { while (_completed.Reader.TryRead(out _)) { } }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Serve(c);
            }
        }

        private sealed class StreamState { public bool HeadersEndStream; public int Bytes; public IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }

        private async Task Serve(System.Net.Sockets.TcpClient client)
        {
            using var _ = client;
            await using var ssl = new System.Net.Security.SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http2],
                }, _cts.Token);
                var preface = new byte[24];
                await ssl.ReadExactlyAsync(preface, _cts.Token);
                var write = new SemaphoreSlim(1);
                async Task Send(byte type, byte flags, int stream, byte[] payload)
                {
                    var f = new byte[9 + payload.Length];
                    f[0] = (byte)(payload.Length >> 16); f[1] = (byte)(payload.Length >> 8); f[2] = (byte)payload.Length;
                    f[3] = type; f[4] = flags;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(5), stream);
                    payload.CopyTo(f, 9);
                    await write.WaitAsync(); try { await ssl.WriteAsync(f); await ssl.FlushAsync(); } finally { write.Release(); }
                }
                static byte[] WindowIncrement(int n) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, n); return b; }
                await Send(0x4, 0, 0, []);                                   // our SETTINGS (defaults)
                await Send(0x8, 0, 0, WindowIncrement(16 * 1024 * 1024));   // generous connection window for uploads
                var streams = new Dictionary<int, StreamState>();
                var head = new byte[9];
                while (true)
                {
                    await ssl.ReadExactlyAsync(head, _cts.Token);
                    var len = (head[0] << 16) | (head[1] << 8) | head[2];
                    var type = head[3];
                    var flags = head[4];
                    var id = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(5)) & 0x7fffffff;
                    var payload = new byte[len];
                    await ssl.ReadExactlyAsync(payload, _cts.Token);
                    var endStream = (flags & 0x1) != 0;
                    switch (type)
                    {
                        case 0x4 when (flags & 0x1) == 0: await Send(0x4, 0x1, 0, []); break;   // ack SETTINGS
                        case 0x6 when (flags & 0x1) == 0: await Send(0x6, 0x1, 0, payload); break; // PING ack
                        case 0x1: // HEADERS
                            streams[id] = new StreamState { HeadersEndStream = endStream };
                            await Send(0x8, 0, id, WindowIncrement(16 * 1024 * 1024));
                            if (endStream) await Complete(id);
                            break;
                        case 0x0 when streams.TryGetValue(id, out var st): // DATA (Go never pads)
                            st.Bytes += len;
                            st.Hash.AppendData(payload);
                            if (len > 0) { await Send(0x8, 0, 0, WindowIncrement(len)); }
                            if (endStream) await Complete(id);
                            break;
                    }
                }

                async Task Complete(int id)
                {
                    var st = streams[id];
                    streams.Remove(id);
                    _completed.Writer.TryWrite(new Seen("", "", "HTTP/2", !st.HeadersEndStream, null, st.Bytes, Convert.ToHexString(st.Hash.GetHashAndReset())));
                    await Send(0x1, 0x5, id, [0x88]); // HEADERS :status 200 (HPACK static index 8), END_HEADERS | END_STREAM
                }
            }
            catch { /* connection closed */ }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _cert.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
