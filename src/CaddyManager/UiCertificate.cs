using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager;

/// <summary>Certificate for the management UI's HTTPS listener: user PFX or a generated self-signed cert.</summary>
public static class UiCertificate
{
    public static X509Certificate2 Load(AppPaths paths, UiSettings ui, ISecretProtector secrets)
    {
        if (!string.IsNullOrWhiteSpace(ui.HttpsPfxPath) && File.Exists(ui.HttpsPfxPath))
        {
            var pwd = string.IsNullOrEmpty(ui.HttpsPfxPasswordProtected) ? null : secrets.Unprotect(ui.HttpsPfxPasswordProtected);
            return X509CertificateLoader.LoadPkcs12FromFile(ui.HttpsPfxPath, pwd, X509KeyStorageFlags.MachineKeySet);
        }

        var file = Path.Combine(paths.DataDir, "ui-selfsigned.pfx");
        if (File.Exists(file))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.MachineKeySet);
            if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
        }

        using var key = RSA.Create(2048);
        var host = Environment.MachineName;
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        try
        {
            var fqdn = System.Net.Dns.GetHostEntry(host).HostName;
            if (!string.Equals(fqdn, host, StringComparison.OrdinalIgnoreCase)) san.AddDnsName(fqdn);
        }
        catch { }
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        File.WriteAllBytes(file, cert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.MachineKeySet);
    }
}
