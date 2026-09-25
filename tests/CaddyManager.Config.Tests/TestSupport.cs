using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>A throw-away data directory with a real LiteStore.</summary>
public sealed class TempEnv : IDisposable
{
    public string Dir { get; }
    public AppPaths Paths { get; }
    private LiteStore? _store;

    public TempEnv()
    {
        Dir = Path.Combine(Path.GetTempPath(), "cpm-config-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Dir);
        Paths = new AppPaths(Dir);
        Paths.EnsureCreated();
    }

    public LiteStore Store => _store ??= new LiteStore(Paths);

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class TestCerts
{
    /// <summary>Self-signed RSA certificate (with private key) for the given DNS names.</summary>
    public static X509Certificate2 SelfSigned(string[] dnsNames, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, bool ecdsa = false)
    {
        var subject = new X500DistinguishedName($"CN={dnsNames[0]}");
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in dnsNames) san.AddDnsName(d);
        CertificateRequest req;
        if (ecdsa)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        }
        else
        {
            var key = RSA.Create(2048);
            req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return req.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(90));
    }

    /// <summary>A CA + leaf signed by it; returns (leaf with key, ca).</summary>
    public static (X509Certificate2 Leaf, X509Certificate2 Ca) Chain(string dns)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Issuing CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));

        var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest($"CN={dns}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        leafReq.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(12);
        var leafPublic = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60), serial);
        return (leafPublic.CopyWithPrivateKey(leafKey), ca);
    }

    public static string KeyPem(X509Certificate2 cert) =>
        cert.GetRSAPrivateKey() is { } rsa ? rsa.ExportPkcs8PrivateKeyPem() : cert.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem();

    public static string RsaTraditionalKeyPem(X509Certificate2 cert) => cert.GetRSAPrivateKey()!.ExportRSAPrivateKeyPem();
}

public static class CaddyBinary
{
    /// <summary>The development Caddy binary (repo .dev/bin/caddy) or null when missing.</summary>
    public static string? Path
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CPM_TEST_CADDY");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, ".dev", "bin", OperatingSystem.IsWindows() ? "caddy.exe" : "caddy");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }

    /// <summary>Copies the dev binary into AppPaths.CaddyExe (what the service expects).</summary>
    public static bool InstallInto(AppPaths paths)
    {
        var src = Path;
        if (src is null) return false;
        Directory.CreateDirectory(paths.CaddyBinDir);
        if (!File.Exists(paths.CaddyExe))
        {
            File.Copy(src, paths.CaddyExe);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(paths.CaddyExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return true;
    }
}

public static class Net
{
    public static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public static class Build
{
    public static SiteHost Proxy(string domain, int port = 8080, TlsMode tls = TlsMode.None, string host = "127.0.0.1") => new()
    {
        Kind = HostKind.Proxy,
        Domains = [domain],
        Tls = tls,
        Upstreams = [new Upstream { Host = host, Port = port }],
        Compression = false,
    };

    public static ConfigGeneratorInput Input(AppPaths paths, CaddySettings? settings = null, IEnumerable<SiteHost>? hosts = null,
        IEnumerable<AccessList>? lists = null, IEnumerable<Certificate>? certs = null, IEnumerable<StreamHost>? streams = null,
        IEnumerable<string>? modules = null, string? eabMac = null) => new()
    {
        Settings = settings ?? new CaddySettings(),
        Paths = paths,
        Hosts = hosts?.ToList() ?? [],
        AccessLists = lists?.ToList() ?? [],
        Certificates = certs?.ToList() ?? [],
        Streams = streams?.ToList() ?? [],
        InstalledModules = modules?.ToList(),
        EabMacKey = eabMac,
    };
}
