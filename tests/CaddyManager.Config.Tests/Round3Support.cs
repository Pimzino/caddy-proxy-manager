using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace CaddyManager.Config.Tests;

/// <summary>HTTPS requests to a local Caddy on a given port with a chosen SNI, capturing the served certificate.</summary>
public static class TlsProbe
{
    public static async Task<TlsResult> GetAsync(int port, string? sni, string path = "/")
    {
        X509Certificate2? served = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
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
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{sni ?? "127.0.0.1"}:{port}{path}");
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

    public static async Task<TlsResult> UntilAsync(int port, string? sni, Func<TlsResult, bool> ok, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        TlsResult last;
        do
        {
            last = await GetAsync(port, sni);
            if (ok(last)) return last;
            await Task.Delay(500);
        } while (DateTime.UtcNow < until);
        return last;
    }
}

/// <summary>A throw-away CA and a server certificate for 127.0.0.1 / localhost (Pebble's own HTTPS listeners).</summary>
public static class LocalPki
{
    public static (X509Certificate2 Ca, X509Certificate2 Leaf) Create(string caName)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caReq = new CertificateRequest($"CN={caName}", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(12))
            .CopyWithPrivateKey(leafKey);
        return (ca, leaf);
    }
}

/// <summary>
/// A running Pebble (test ACME CA) with its HTTPS certificate from <see cref="LocalPki"/>, validating challenges through
/// the given DNS server (-dnsserver) without the random validation delay (PEBBLE_VA_NOSLEEP) and without random nonce
/// rejections (PEBBLE_WFE_NONCEREJECT=0). https://github.com/letsencrypt/pebble#readme
/// </summary>
public sealed class PebbleProcess : IDisposable
{
    private readonly Process _p;
    private readonly StringBuilder _output = new();
    public int Port { get; }
    public int ManagementPort { get; }
    public string CaPemPath { get; }
    public string Directory => $"https://127.0.0.1:{Port}/dir";
    public string Output { get { lock (_output) return _output.ToString(); } }

    private PebbleProcess(Process p, int port, int mgmt, string caPem)
    {
        _p = p;
        Port = port;
        ManagementPort = mgmt;
        CaPemPath = caPem;
        _p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    public static async Task<PebbleProcess> StartAsync(string pebble, string dnsServer, string workDir)
    {
        System.IO.Directory.CreateDirectory(workDir);
        var (ca, leaf) = LocalPki.Create("CPM E2E Pebble listener CA");
        var caPem = Path.Combine(workDir, "pebble-listener-ca.pem");
        File.WriteAllText(caPem, ca.ExportCertificatePem());
        var certPem = Path.Combine(workDir, "pebble-cert.pem");
        var keyPem = Path.Combine(workDir, "pebble-key.pem");
        File.WriteAllText(certPem, leaf.ExportCertificatePem());
        File.WriteAllText(keyPem, leaf.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var port = Net.FreeTcpPort();
        var mgmt = Net.FreeTcpPort();
        var config = new JsonObject
        {
            ["pebble"] = new JsonObject
            {
                ["listenAddress"] = $"127.0.0.1:{port}",
                ["managementListenAddress"] = $"127.0.0.1:{mgmt}",
                ["certificate"] = certPem,
                ["privateKey"] = keyPem,
                // HTTP-01 / TLS-ALPN-01 validation ports: unused (DNS-01 only), but must be valid.
                ["httpPort"] = Net.FreeTcpPort(),
                ["tlsPort"] = Net.FreeTcpPort(),
                ["ocspResponderURL"] = "",
                ["externalAccountBindingRequired"] = false,
                ["retryAfter"] = new JsonObject { ["authz"] = 1, ["order"] = 1 },
                ["keyAlgorithm"] = "ecdsa",
                ["profiles"] = new JsonObject { ["default"] = new JsonObject { ["description"] = "default", ["validityPeriod"] = 7776000 } },
            },
        };
        var cfgPath = Path.Combine(workDir, "pebble-config.json");
        File.WriteAllText(cfgPath, config.ToJsonString());

        var psi = new ProcessStartInfo(pebble)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };
        foreach (var a in new[] { "-config", cfgPath, "-dnsserver", dnsServer }) psi.ArgumentList.Add(a);
        psi.Environment["PEBBLE_VA_NOSLEEP"] = "1";
        psi.Environment["PEBBLE_WFE_NONCEREJECT"] = "0";
        var p = Process.Start(psi)!;
        var self = new PebbleProcess(p, port, mgmt, caPem);
        await Wait.Until(async () => (await self.ManagementGetAsync("/roots/0")) is not null, TimeSpan.FromSeconds(20),
            () => "Pebble did not start:\n" + self.Output);
        return self;
    }

    /// <summary>GET on the management API (HTTPS with our listener CA); null on failure.</summary>
    public async Task<string?> ManagementGetAsync(string path)
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var r = await http.GetAsync($"https://127.0.0.1:{ManagementPort}{path}");
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_p.HasExited) { _p.Kill(entireProcessTree: true); _p.WaitForExit(5000); }
        }
        catch (InvalidOperationException) { }
        _p.Dispose();
    }
}
