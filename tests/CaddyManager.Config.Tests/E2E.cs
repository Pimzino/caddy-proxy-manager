using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Certificates;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Verifiable, repeatable output of the end-to-end tests: one JSON file per test in CPM_E2E_ARTIFACTS (CI uploads it)
/// or ./e2e-artifacts next to the test binaries. Every report records the exact Caddy binary tested (`caddy version`).
/// </summary>
public static class E2EArtifacts
{
    private static string? _caddyVersion;

    public static string Directory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CPM_E2E_ARTIFACTS") is { Length: > 0 } d
                ? d
                : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Output of `caddy version` for the binary under test (e.g. "v2.11.4 h1:XKxk...").</summary>
    public static string CaddyVersion
    {
        get
        {
            if (_caddyVersion is not null) return _caddyVersion;
            var bin = CaddyBinary.Path;
            if (bin is null) return _caddyVersion = "(no caddy binary)";
            using var p = Process.Start(new ProcessStartInfo(bin, "version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return _caddyVersion = output;
        }
    }

    /// <summary>New report with the common header (test, UTC time, OS, runtime, Caddy version).</summary>
    public static JsonObject Report(string test) => new()
    {
        ["test"] = test,
        ["utc"] = DateTime.UtcNow.ToString("O"),
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["caddyVersion"] = CaddyVersion,
    };

    public static string Write(string fileName, JsonObject report)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

/// <summary>
/// A real Caddy process driven through the manager's own services (generator, CaddyConfigService, admin client) on
/// free loopback ports. Disposing kills the process and deletes the data directory.
/// </summary>
public sealed class LiveCaddy : IDisposable
{
    public ConfigServices S { get; } = new(installBinary: true);
    public int HttpPort { get; }
    public int HttpsPort { get; }
    public int AdminPort { get; }
    public CaddyAdminClient Admin => S.Provider.GetRequiredService<CaddyAdminClient>();
    public CaddyProcess? Process { get; private set; }

    public LiveCaddy(Action<CaddySettings>? configure = null)
    {
        HttpPort = Net.FreeTcpPort();
        HttpsPort = Net.FreeTcpPort();
        AdminPort = Net.FreeTcpPort();
        var settings = new CaddySettings
        {
            AdminListen = $"127.0.0.1:{AdminPort}", HttpPort = HttpPort, HttpsPort = HttpsPort, LogLevel = "info", EnableHttp3 = false,
        };
        configure?.Invoke(settings);
        S.Store.SaveSettings(settings);
    }

    public void Add(SiteHost h) => S.Store.Col<SiteHost>().Insert(h);

    /// <summary>Stores an uploaded (PEM) certificate the way the certificates page does and returns its row.</summary>
    public Certificate AddUploadedCertificate(string id, X509Certificate2 cert)
    {
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), TestCerts.KeyPem(cert));
        var files = new CertificateFileStore(S.Store, S.Paths, NullLogger<CertificateFileStore>.Instance);
        var (cp, kp) = files.Write(id, parsed);
        var row = new Certificate
        {
            Id = id, Name = id, Source = CertificateSource.Uploaded, CertPath = cp, KeyPath = kp,
            Subjects = parsed.Metadata.Subjects, NotAfter = parsed.Metadata.NotAfter, Thumbprint = parsed.Metadata.Thumbprint,
        };
        S.Store.Col<Certificate>().Insert(row);
        return row;
    }

    public void UpdateSettings(Action<CaddySettings> change)
    {
        var s = S.Store.GetSettings<CaddySettings>();
        change(s);
        S.Store.SaveSettings(s);
    }

    /// <summary>Boot config, `caddy run`, wait for the admin API.</summary>
    public async Task StartAsync()
    {
        S.Config.EnsureBootConfig();
        Process = new CaddyProcess(S.Paths);
        await Wait.Until(() => Admin.IsReachableAsync(), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + Process.Output);
    }

    /// <summary>Applies the model through CaddyConfigService (generate + POST /load) and asserts success.</summary>
    public async Task<ApplyResult> ApplyAsync(string reason = "e2e")
    {
        var r = await S.Config.ApplyAsync(reason);
        Assert.True(r.Success, r.Error + "\n" + ProcessLog());
        return r;
    }

    public string ProcessLog()
    {
        var path = S.Paths.CaddyProcessLog;
        if (!File.Exists(path)) return Process?.Output ?? "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd() + (Process?.Output ?? "");
    }

    /// <summary>Plain-HTTP request with the given Host header (which may carry a port) and a raw request target.</summary>
    public Task<RawHttp.Response> HttpAsync(string host, string target, params (string Name, string Value)[] headers) =>
        RawHttp.GetAsync(HttpPort, host, target, headers);

    /// <summary>The config Caddy is running (GET /config/).</summary>
    public async Task<JsonNode> RunningConfigAsync() => JsonNode.Parse((await Admin.GetConfigAsync())!)!;

    /// <summary>Loads a hand-modified copy of a generated config straight into Caddy (controls that reproduce a bug).</summary>
    public Task LoadRawAsync(JsonNode config) => Admin.LoadAsync(config.ToJsonString());

    /// <summary>
    /// HTTPS request to 127.0.0.1:HttpsPort with the given SNI (null = no SNI, as when a browser uses the IP).
    /// Returns the status, body and served certificate, or the handshake error.
    /// </summary>
    public async Task<TlsResult> HttpsAsync(string? sni, string path = "/", string? hostHeader = null)
    {
        X509Certificate2? served = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, HttpsPort, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is not null) served = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // .NET sends no SNI when the URL host is an IP address.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{sni ?? "127.0.0.1"}:{HttpsPort}{path}");
        if (hostHeader is not null) req.Headers.Host = hostHeader;
        req.Headers.ConnectionClose = true;
        try
        {
            using var res = await client.SendAsync(req);
            return new TlsResult((int)res.StatusCode, await res.Content.ReadAsStringAsync(), served, null);
        }
        catch (HttpRequestException ex)
        {
            return new TlsResult(0, "", served, (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// HTTPS request to 127.0.0.1:HttpsPort with the given SNI and extra request headers; returns status, all response
    /// headers (content headers included) and the body. Status 0 = the TLS handshake or request failed.
    /// </summary>
    public async Task<RawHttp.Response> HttpsRequestAsync(string sni, string path, params (string Name, string Value)[] headers)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, HttpsPort, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{sni}:{HttpsPort}{path}");
        foreach (var (n, v) in headers) req.Headers.TryAddWithoutValidation(n, v);
        req.Headers.ConnectionClose = true;
        try
        {
            using var res = await client.SendAsync(req);
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in res.Headers.Concat(res.Content.Headers)) dict[h.Key] = h.Value.ToList();
            return new RawHttp.Response((int)res.StatusCode, dict, await res.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            return new RawHttp.Response(0, new(), (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>Retries an HTTPS request until the served certificate satisfies the predicate (certificates are issued in the background).</summary>
    public async Task<TlsResult> HttpsUntilAsync(string? sni, Func<TlsResult, bool> ok, TimeSpan timeout, string path = "/")
    {
        var until = DateTime.UtcNow + timeout;
        TlsResult last;
        do
        {
            last = await HttpsAsync(sni, path);
            if (ok(last)) return last;
            await Task.Delay(300);
        } while (DateTime.UtcNow < until);
        return last;
    }

    public void Dispose()
    {
        Process?.Dispose();
        S.Dispose();
    }
}

/// <summary>
/// Kestrel upstream (HTTP or HTTPS with a self-signed certificate) that records every request it receives: TLS SNI,
/// Host, connection id, path and all headers; the body echoes the headers as "name: value" lines. With
/// <c>strictSni</c> it answers 421 Misdirected Request when a TLS SNI was sent and differs from the Host name, as
/// Apache httpd does for name-based virtual hosts.
/// </summary>
public sealed class RecordingBackend : IAsyncDisposable
{
    public sealed record Seen(string? Sni, string Host, string ConnectionId, string Path, Dictionary<string, string> Headers, int Status);

    private const string SniItem = "cpm-e2e-sni";
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
    private readonly List<Seen> _seen = new();
    public int Port { get; }

    private RecordingBackend(Microsoft.AspNetCore.Builder.WebApplication app, int port) { _app = app; Port = port; }

    public IReadOnlyList<Seen> Requests { get { lock (_seen) return _seen.ToList(); } }
    public void Clear() { lock (_seen) _seen.Clear(); }

    /// <param name="custom">Optional handler that runs first; returns true when it has handled the request itself
    /// (e.g. answered with a special status or aborted the connection).</param>
    public static async Task<RecordingBackend> StartAsync(bool https, bool strictSni = false, int? port = null,
        Func<HttpContext, Task<bool>>? custom = null)
    {
        var listenPort = port ?? Net.FreeTcpPort();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (https)
        {
            using var cert = TestCerts.SelfSigned(["backend.lan"]);
            var pfx = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
            // The certificate selector receives the SNI of each TLS connection (null when none was sent); it is kept
            // in the connection's items for the request handler.
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, listenPort, o => o.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
            {
                ServerCertificateSelector = (conn, name) =>
                {
                    if (conn is not null) conn.Items[SniItem] = name;
                    return pfx;
                },
            })));
        }
        else builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, listenPort));
        var app = builder.Build();
        RecordingBackend? self = null;
        app.Run(async ctx =>
        {
            var items = ctx.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>()?.Items;
            var sni = items is not null && items.TryGetValue(SniItem, out var n) ? n as string : null;
            if (string.IsNullOrEmpty(sni)) sni = null;
            if (custom is not null && await custom(ctx))
            {
                var h = ctx.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                lock (self!._seen) self._seen.Add(new Seen(sni, ctx.Request.Host.Value ?? "", ctx.Connection.Id, ctx.Request.Path + ctx.Request.QueryString, h, ctx.Response.StatusCode));
                return;
            }
            var hostName = ctx.Request.Host.Host;
            var status = strictSni && sni is not null && !string.Equals(sni, hostName, StringComparison.OrdinalIgnoreCase) ? 421 : 200;
            var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            lock (self!._seen) self._seen.Add(new Seen(sni, ctx.Request.Host.Value ?? "", ctx.Connection.Id, ctx.Request.Path + ctx.Request.QueryString, headers, status));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync(string.Join("\n", headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {h.Value}")) + "\n");
        });
        await app.StartAsync();
        self = new RecordingBackend(app, listenPort);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// HTTP/1.1 over a raw socket: the request target is sent byte for byte (HttpClient/Uri would decode "%2e%2e" and
/// remove dot segments before sending, so traversal probes never reach the server as written).
/// </summary>
public static class RawHttp
{
    public sealed record Response(int Status, Dictionary<string, List<string>> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v[0] : null;
    }

    public static async Task<Response> GetAsync(int port, string host, string target, params (string Name, string Value)[] headers)
    {
        // Right after a config reload the old server may still accept a connection and then close it without an
        // answer while it shuts down ("servers shutting down with eternal grace period"); retry those only.
        for (var attempt = 1; ; attempt++)
        {
            var r = await GetOnceAsync(port, host, target, headers);
            if (r is not null) return r;
            if (attempt == 5) throw new IOException($"No HTTP response for {host}{target} (connection closed without data, 5 attempts).");
            await Task.Delay(200);
        }
    }

    private static async Task<Response?> GetOnceAsync(int port, string host, string target, (string Name, string Value)[] headers)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = tcp.GetStream();
        var sb = new System.Text.StringBuilder();
        sb.Append($"GET {target} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: cpm-e2e\r\n");
        foreach (var (n, v) in headers) sb.Append($"{n}: {v}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(sb.ToString()));
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await stream.CopyToAsync(ms, cts.Token);
        var raw = ms.ToArray();
        if (raw.Length == 0) return null;
        var text = System.Text.Encoding.Latin1.GetString(raw);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) throw new IOException("Incomplete HTTP response: " + text);
        var lines = text[..headerEnd].Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i <= 0) continue;
            var name = l[..i].Trim();
            if (!dict.TryGetValue(name, out var list)) dict[name] = list = new();
            list.Add(l[(i + 1)..].Trim());
        }
        var body = raw.AsSpan(headerEnd + 4).ToArray();
        if (dict.TryGetValue("Transfer-Encoding", out var te) && te.Any(t => t.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
            body = Dechunk(body);
        return new Response(status, dict, System.Text.Encoding.UTF8.GetString(body));
    }

    private static byte[] Dechunk(byte[] data)
    {
        using var output = new MemoryStream();
        var pos = 0;
        while (pos < data.Length)
        {
            var lineEnd = Array.IndexOf(data, (byte)'\n', pos);
            if (lineEnd < 0) break;
            var sizeText = System.Text.Encoding.ASCII.GetString(data, pos, lineEnd - pos).Trim().Split(';')[0];
            var size = Convert.ToInt32(sizeText, 16);
            pos = lineEnd + 1;
            if (size == 0) break;
            output.Write(data, pos, Math.Min(size, data.Length - pos));
            pos += size + 2;
        }
        return output.ToArray();
    }
}

public sealed record TlsResult(int Status, string Body, X509Certificate2? Certificate, string? Error)
{
    public bool Handshake => Error is null;
    public IReadOnlyList<string> DnsNames => Certificate is null ? [] : WindowsCertificateStoreSource.DnsNames(Certificate);
    public string Issuer => Certificate?.Issuer ?? "";

    public JsonObject ToJson() => new()
    {
        ["status"] = Status,
        ["handshake"] = Handshake,
        ["error"] = Error,
        ["certificateSubject"] = Certificate?.Subject,
        ["certificateIssuer"] = Certificate?.Issuer,
        ["certificateDnsNames"] = new JsonArray(DnsNames.Select(n => (JsonNode)n).ToArray()),
    };
}

public static class Wait
{
    public static async Task Until(Func<Task<bool>> condition, TimeSpan timeout, Func<string> message)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail(message());
    }

    public static async Task<bool> For(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }
        return false;
    }
}

public static class JsonWalk
{
    public static IEnumerable<JsonNode> Descendants(JsonNode? n)
    {
        if (n is null) yield break;
        yield return n;
        if (n is JsonObject o) foreach (var (_, v) in o) foreach (var d in Descendants(v)) yield return d;
        if (n is JsonArray a) foreach (var v in a) foreach (var d in Descendants(v)) yield return d;
    }

    public static IEnumerable<JsonObject> Handlers(JsonNode? root, string handler) =>
        Descendants(root).OfType<JsonObject>().Where(o => o["handler"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == handler);
}
