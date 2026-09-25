using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaddyManager.Config.Certificates;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

public sealed class CertificateTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void Pem_import_normalises_key_and_reads_metadata()
    {
        using var cert = TestCerts.SelfSigned(["shop.example.com", "www.shop.example.com"]);
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), TestCerts.RsaTraditionalKeyPem(cert));

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", parsed.PrivateKeyPem); // PKCS#8, unencrypted
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", parsed.FullChainPem);
        Assert.Equal(["shop.example.com", "www.shop.example.com"], parsed.Metadata.Subjects);
        Assert.Equal(cert.Thumbprint, parsed.Metadata.Thumbprint);
        Assert.Equal("shop.example.com", parsed.Metadata.Issuer);
        Assert.Equal(cert.NotAfter.ToUniversalTime(), parsed.Metadata.NotAfter);
        Assert.Equal(DateTimeKind.Utc, parsed.Metadata.NotAfter.Kind);
        Assert.False(parsed.Metadata.IsExpired);

        // The normalised output round-trips.
        using var again = X509Certificate2.CreateFromPem(parsed.FullChainPem, parsed.PrivateKeyPem);
        Assert.True(again.HasPrivateKey);
    }

    [Fact]
    public void Ecdsa_pem_import_works()
    {
        using var cert = TestCerts.SelfSigned(["ec.example.com"], ecdsa: true);
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), cert.GetECDsaPrivateKey()!.ExportECPrivateKeyPem());
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", parsed.PrivateKeyPem);
        Assert.Equal(["ec.example.com"], parsed.Metadata.Subjects);
    }

    [Fact]
    public void Chain_is_ordered_leaf_first_even_when_uploaded_reversed()
    {
        var (leaf, ca) = TestCerts.Chain("chain.example.com");
        using (leaf) using (ca)
        {
            var reversed = ca.ExportCertificatePem() + "\n" + leaf.ExportCertificatePem();
            var parsed = CertificateParser.FromPem(reversed, TestCerts.KeyPem(leaf));
            var certs = new X509Certificate2Collection();
            certs.ImportFromPem(parsed.FullChainPem);
            Assert.Equal(2, certs.Count);
            Assert.Equal(leaf.Thumbprint, certs[0].Thumbprint);
            Assert.Equal(ca.Thumbprint, certs[1].Thumbprint);
            Assert.Equal("Test Issuing CA", parsed.Metadata.Issuer);
        }
    }

    [Fact]
    public void Combined_pem_without_separate_key_is_accepted()
    {
        using var cert = TestCerts.SelfSigned(["combo.example.com"]);
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem() + "\n" + TestCerts.KeyPem(cert), null);
        Assert.Equal(["combo.example.com"], parsed.Metadata.Subjects);
    }

    [Fact]
    public void Mismatched_key_is_rejected()
    {
        using var a = TestCerts.SelfSigned(["a.example.com"]);
        using var b = TestCerts.SelfSigned(["b.example.com"]);
        var ex = Assert.Throws<CertificateImportException>(() => CertificateParser.FromPem(a.ExportCertificatePem(), TestCerts.KeyPem(b)));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public void Garbage_and_encrypted_keys_give_clear_errors()
    {
        using var cert = TestCerts.SelfSigned(["x.example.com"]);
        Assert.Contains("No certificate", Assert.Throws<CertificateImportException>(() => CertificateParser.FromPem("hello", "world")).Message);
        Assert.Contains("No private key", Assert.Throws<CertificateImportException>(() => CertificateParser.FromPem(cert.ExportCertificatePem(), "nope")).Message);
        var encrypted = cert.GetRSAPrivateKey()!.ExportEncryptedPkcs8PrivateKeyPem("pw", new PbeParameters(PbeEncryptionAlgorithm.Aes128Cbc, HashAlgorithmName.SHA256, 1000));
        Assert.Contains("password protected", Assert.Throws<CertificateImportException>(() => CertificateParser.FromPem(cert.ExportCertificatePem(), encrypted)).Message);
    }

    [Fact]
    public void Pfx_import_with_password()
    {
        var (leaf, ca) = TestCerts.Chain("pfx.example.com");
        using (leaf) using (ca)
        {
            var coll = new X509Certificate2Collection { leaf, ca };
            var pfx = coll.Export(X509ContentType.Pfx, "s3cret!")!;
            var parsed = CertificateParser.FromPfx(pfx, "s3cret!");
            Assert.Equal(["pfx.example.com"], parsed.Metadata.Subjects);
            Assert.Equal(leaf.Thumbprint, parsed.Metadata.Thumbprint);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", parsed.PrivateKeyPem);
            using var again = X509Certificate2.CreateFromPem(parsed.FullChainPem, parsed.PrivateKeyPem);
            Assert.True(again.HasPrivateKey);
            Assert.Equal(leaf.Thumbprint, again.Thumbprint);
        }
    }

    [Fact]
    public void Pfx_wrong_password_is_rejected()
    {
        using var cert = TestCerts.SelfSigned(["pfx.example.com"]);
        var pfx = cert.Export(X509ContentType.Pfx, "right");
        var ex = Assert.Throws<CertificateImportException>(() => CertificateParser.FromPfx(pfx, "wrong"));
        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public void Pfx_without_key_is_rejected()
    {
        using var cert = TestCerts.SelfSigned(["nokey.example.com"]);
        using var pub = X509CertificateLoader.LoadCertificate(cert.RawData);
        var pfx = pub.Export(X509ContentType.Pfx, "pw")!;
        Assert.Throws<CertificateImportException>(() => CertificateParser.FromPfx(pfx, "pw"));
    }

    [Fact]
    public void Expired_certificate_is_parsed_and_flagged()
    {
        using var cert = TestCerts.SelfSigned(["old.example.com"], DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-10));
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), TestCerts.KeyPem(cert));
        Assert.True(parsed.Metadata.IsExpired);
    }

    [Fact]
    public void File_paths_are_validated()
    {
        using var cert = TestCerts.SelfSigned(["file.example.com"]);
        var certPath = Path.Combine(_env.Dir, "c.pem");
        var keyPath = Path.Combine(_env.Dir, "k.pem");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(cert));
        Assert.Equal(["file.example.com"], CertificateParser.FromFiles(certPath, keyPath).Metadata.Subjects);
        Assert.Contains("not found", Assert.Throws<CertificateImportException>(() => CertificateParser.FromFiles(certPath + ".missing", keyPath)).Message);
        Assert.Contains("absolute", Assert.Throws<CertificateImportException>(() => CertificateParser.FromFiles("relative.pem", keyPath)).Message);
    }

    [Fact]
    public void File_store_writes_pem_pair_under_cert_id()
    {
        using var cert = TestCerts.SelfSigned(["store.example.com"]);
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), TestCerts.KeyPem(cert));
        var files = new CertificateFileStore(_env.Store, _env.Paths, NullLogger<CertificateFileStore>.Instance);
        var (c, k) = files.Write("abc123", parsed);
        Assert.Equal(Path.Combine(_env.Paths.DefaultCertificateStore, "abc123", "fullchain.pem"), c);
        Assert.Equal(Path.Combine(_env.Paths.DefaultCertificateStore, "abc123", "privkey.pem"), k);
        Assert.Equal(parsed.FullChainPem, File.ReadAllText(c));

        files.DeleteFiles(new Certificate { Id = "abc123", Source = CertificateSource.Uploaded, CertPath = c, KeyPath = k });
        Assert.False(Directory.Exists(Path.GetDirectoryName(c)));
    }

    [Fact]
    public async Task Inventory_merges_custom_and_caddy_storage()
    {
        using var custom = TestCerts.SelfSigned(["custom.example.com"]);
        var certPath = Path.Combine(_env.Dir, "custom.pem");
        File.WriteAllText(certPath, custom.ExportCertificatePem());
        var store = _env.Store;
        store.Col<Certificate>().Insert(new Certificate { Id = "c1", Name = "Custom", Source = CertificateSource.FilePath, CertPath = certPath, KeyPath = "/missing/key.pem", NotAfter = DateTime.UtcNow.AddDays(10.5) });
        store.Col<SiteHost>().Insert(new SiteHost { Id = "h1", Domains = ["custom.example.com"], Tls = TlsMode.Custom, CertificateId = "c1" });
        store.Col<SiteHost>().Insert(new SiteHost { Id = "h2", Domains = ["a.example.com"], Tls = TlsMode.Acme });
        store.Col<SiteHost>().Insert(new SiteHost { Id = "h3", Domains = ["x.corp.local"], Tls = TlsMode.Internal });

        // Caddy storage layout
        using var acme = TestCerts.SelfSigned(["a.example.com"]);
        var acmeDir = Path.Combine(_env.Paths.CaddyStorageDir, "certificates", "acme-v02.api.letsencrypt.org-directory", "a.example.com");
        Directory.CreateDirectory(acmeDir);
        File.WriteAllText(Path.Combine(acmeDir, "a.example.com.crt"), acme.ExportCertificatePem());
        using var local = TestCerts.SelfSigned(["x.corp.local"]);
        var localDir = Path.Combine(_env.Paths.CaddyStorageDir, "certificates", "local", "x.corp.local");
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "x.corp.local.crt"), local.ExportCertificatePem());
        using var root = TestCerts.SelfSigned(["Caddy Local Authority"]);
        var rootDir = Path.Combine(_env.Paths.CaddyStorageDir, "pki", "authorities", "local");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "root.crt"), root.ExportCertificatePem());

        var inv = new CertificateInventory(store, _env.Paths, NullLogger<CertificateInventory>.Instance);
        var list = await inv.ListAsync();

        var c1 = list.Single(x => x.Id == "c1");
        Assert.Equal(CertificateKind.Custom, c1.Kind);
        Assert.Equal(["h1"], c1.UsedByHostIds);
        Assert.Equal(10, c1.DaysRemaining);
        Assert.Contains("Key file not found", c1.Error);
        Assert.Equal("filePath", c1.Source);

        var a = list.Single(x => x.Kind == CertificateKind.Acme);
        Assert.Equal("certificates/acme-v02.api.letsencrypt.org-directory/a.example.com", a.Id);
        Assert.Equal(["h2"], a.UsedByHostIds);
        Assert.Equal("acme-v02.api.letsencrypt.org-directory", a.Source);

        var i = list.Single(x => x.Kind == CertificateKind.Internal);
        Assert.Equal(["h3"], i.UsedByHostIds);

        var r = list.Single(x => x.Kind == CertificateKind.InternalRoot);
        Assert.Equal(["h3"], r.UsedByHostIds);
        Assert.True(r.DaysRemaining > 80);
    }

    [Theory]
    [InlineData("*.example.com", "a.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("A.example.com", "a.example.com", true)]
    [InlineData("*.example.com", "*.example.com", true)]
    public void Subject_matching(string subject, string domain, bool expected) =>
        Assert.Equal(expected, CertificateInventory.Matches(subject, domain));
}
