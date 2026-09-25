using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CaddyManager.Config.Certificates;

/// <summary>A user error while importing a certificate (shown to the sysadmin as-is).</summary>
public sealed class CertificateImportException(string message) : Exception(message);

/// <summary>Metadata read from a certificate.</summary>
public sealed record CertificateMetadata(
    List<string> Subjects,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Thumbprint)
{
    public bool IsExpired => NotAfter < DateTime.UtcNow;
}

/// <summary>A parsed certificate + key, normalised to PEM (leaf-first chain, unencrypted PKCS#8 key).</summary>
public sealed record ParsedCertificate(string FullChainPem, string PrivateKeyPem, CertificateMetadata Metadata);

public static class CertificateParser
{
    private const int MaxPemBytes = 1024 * 1024;

    // ------------------------------------------------------------------ PEM

    /// <summary>
    /// Parses a PEM certificate (chain) and PEM private key. The key may also be embedded in the certificate text
    /// (combined PEM) when <paramref name="keyPem"/> is empty.
    /// </summary>
    public static ParsedCertificate FromPem(string certPem, string? keyPem)
    {
        if (string.IsNullOrWhiteSpace(certPem)) throw new CertificateImportException("The certificate is empty.");
        if (certPem.Length > MaxPemBytes) throw new CertificateImportException("The certificate file is too large.");
        var certs = LoadPemCertificates(certPem);
        try
        {
            var keySource = string.IsNullOrWhiteSpace(keyPem) ? certPem : keyPem;
            using var key = LoadPrivateKey(keySource);
            var leaf = FindLeafForKey(certs, key)
                ?? throw new CertificateImportException("The private key does not match the certificate (the public keys differ). Check that you selected the key that belongs to this certificate.");
            return Build(leaf, certs, key);
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
        }
    }

    private static List<X509Certificate2> LoadPemCertificates(string pem)
    {
        var list = new List<X509Certificate2>();
        var span = pem.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            var label = span[fields.Label].ToString();
            if (label == "CERTIFICATE")
            {
                try
                {
                    var der = Convert.FromBase64String(span[fields.Base64Data].ToString());
                    list.Add(X509CertificateLoader.LoadCertificate(der));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    foreach (var c in list) c.Dispose();
                    throw new CertificateImportException("A certificate in the PEM file is corrupt: " + ex.Message);
                }
            }
            span = span[fields.Location.End..];
        }
        if (list.Count == 0)
            throw new CertificateImportException("No certificate found. Expected PEM text starting with -----BEGIN CERTIFICATE-----. (For .pfx/.p12 files use the PFX upload.)");
        return list;
    }

    private static AsymmetricAlgorithm LoadPrivateKey(string pem)
    {
        if (pem.Length > MaxPemBytes) throw new CertificateImportException("The private key file is too large.");
        string? label = null;
        var span = pem.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            var l = span[fields.Label].ToString();
            if (l.EndsWith("PRIVATE KEY", StringComparison.Ordinal)) { label = l; break; }
            span = span[fields.Location.End..];
        }
        if (label is null)
            throw new CertificateImportException("No private key found. Expected PEM text starting with -----BEGIN PRIVATE KEY----- (or RSA/EC PRIVATE KEY).");
        if (label == "ENCRYPTED PRIVATE KEY")
            throw new CertificateImportException("The private key is password protected. Provide an unencrypted key (e.g. openssl pkey -in key.pem -out key-plain.pem) or upload a PFX with its password.");

        if (label != "EC PRIVATE KEY")
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
                return rsa;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                rsa.Dispose();
                if (label == "RSA PRIVATE KEY") throw new CertificateImportException("The RSA private key could not be read: " + ex.Message);
            }
        }
        var ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return ec;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            ec.Dispose();
            throw new CertificateImportException("The private key could not be read. Supported: RSA and ECDSA keys in PKCS#1, SEC1 or PKCS#8 PEM format. (" + ex.Message + ")");
        }
    }

    private static X509Certificate2? FindLeafForKey(IEnumerable<X509Certificate2> certs, AsymmetricAlgorithm key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        return certs.FirstOrDefault(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki));
    }

    // ------------------------------------------------------------------ PFX

    public static ParsedCertificate FromPfx(byte[] pfx, string? password)
    {
        if (pfx is null || pfx.Length == 0) throw new CertificateImportException("The PFX file is empty.");
        X509Certificate2Collection coll;
        var flags = X509KeyStorageFlags.Exportable;
        if (!OperatingSystem.IsMacOS()) flags |= X509KeyStorageFlags.EphemeralKeySet;
        try
        {
            coll = X509CertificateLoader.LoadPkcs12Collection(pfx, password, flags);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateImportException("The PFX could not be opened: the password is wrong or the file is not a valid PFX/PKCS#12 file. (" + ex.Message + ")");
        }

        try
        {
            var withKey = coll.Where(c => c.HasPrivateKey).ToList();
            if (withKey.Count == 0) throw new CertificateImportException("The PFX file does not contain a private key.");
            var leaf = withKey.FirstOrDefault(c => !IsCa(c)) ?? withKey[0];
            using var key = ExportableCopyOfKey(leaf, PfxNotExportable);
            return Build(leaf, coll.ToList(), key);
        }
        finally
        {
            foreach (var c in coll) c.Dispose();
        }
    }

    private const string PfxNotExportable = "The private key in the PFX could not be exported. Re-export the PFX with \"Mark this key as exportable\".";

    /// <summary>
    /// A certificate that already carries its private key (e.g. from the Windows certificate store) plus optional
    /// chain certificates, normalised to PEM. <paramref name="notExportableMessage"/> is the error shown when the key
    /// cannot be exported.
    /// </summary>
    public static ParsedCertificate FromCertificate(X509Certificate2 leafWithKey, IEnumerable<X509Certificate2> chain, string notExportableMessage)
    {
        if (!leafWithKey.HasPrivateKey) throw new CertificateImportException("The certificate has no private key.");
        using var key = ExportableCopyOfKey(leafWithKey, notExportableMessage);
        return Build(leafWithKey, [leafWithKey, .. chain], key);
    }

    /// <summary>Reads and converts a .pfx/.p12 file referenced by path.</summary>
    public static ParsedCertificate FromPfxFile(string path, string? password)
    {
        var bytes = ReadBytes(path, "PFX", MaxPfxBytes);
        return FromPfx(bytes, password ?? "");
    }

    private const int MaxPfxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Copies the private key into a software key via an encrypted PKCS#8 round trip: Windows CNG keys imported
    /// as "Exportable" (and store keys that allow export but not plaintext export) still refuse plaintext export.
    /// </summary>
    private static AsymmetricAlgorithm ExportableCopyOfKey(X509Certificate2 cert, string notExportableMessage)
    {
        var pwd = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000);
        try
        {
            if (cert.GetRSAPrivateKey() is { } rsa)
            {
                using (rsa)
                {
                    var enc = rsa.ExportEncryptedPkcs8PrivateKey(pwd, pbe);
                    var copy = RSA.Create();
                    copy.ImportEncryptedPkcs8PrivateKey(pwd, enc, out _);
                    return copy;
                }
            }
            if (cert.GetECDsaPrivateKey() is { } ec)
            {
                using (ec)
                {
                    var enc = ec.ExportEncryptedPkcs8PrivateKey(pwd, pbe);
                    var copy = ECDsa.Create();
                    copy.ImportEncryptedPkcs8PrivateKey(pwd, enc, out _);
                    return copy;
                }
            }
        }
        catch (CryptographicException ex)
        {
            throw new CertificateImportException(notExportableMessage + " (" + ex.Message.Trim() + ")");
        }
        throw new CertificateImportException("Unsupported key type in the PFX. Supported: RSA and ECDSA.");
    }

    // ------------------------------------------------------------------ shared

    private static ParsedCertificate Build(X509Certificate2 leaf, List<X509Certificate2> all, AsymmetricAlgorithm key)
    {
        var chain = OrderChain(leaf, all);
        var sb = new StringBuilder();
        foreach (var c in chain) sb.Append(c.ExportCertificatePem()).Append('\n');
        string keyPem = key switch
        {
            RSA r => r.ExportPkcs8PrivateKeyPem(),
            ECDsa e => e.ExportPkcs8PrivateKeyPem(),
            _ => throw new CertificateImportException("Unsupported key type. Supported: RSA and ECDSA."),
        };
        return new ParsedCertificate(sb.ToString(), keyPem + "\n", ReadMetadata(leaf));
    }

    /// <summary>Leaf first, then each issuer in turn; any remaining certificates keep their original order.</summary>
    private static List<X509Certificate2> OrderChain(X509Certificate2 leaf, List<X509Certificate2> all)
    {
        var rest = all.Where(c => c.Thumbprint != leaf.Thumbprint).DistinctBy(c => c.Thumbprint).ToList();
        var ordered = new List<X509Certificate2> { leaf };
        var current = leaf;
        while (true)
        {
            if (current.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData)) break; // self-signed
            var issuer = rest.FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData));
            if (issuer is null) break;
            ordered.Add(issuer);
            rest.Remove(issuer);
            current = issuer;
        }
        ordered.AddRange(rest);
        return ordered;
    }

    private static bool IsCa(X509Certificate2 c) =>
        c.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    public static CertificateMetadata ReadMetadata(X509Certificate2 cert)
    {
        var subjects = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            var san = ext as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
            foreach (var dns in san.EnumerateDnsNames())
                if (!subjects.Contains(dns, StringComparer.OrdinalIgnoreCase)) subjects.Add(dns);
            foreach (var ip in san.EnumerateIPAddresses())
            {
                var s = ip.ToString();
                if (!subjects.Contains(s, StringComparer.OrdinalIgnoreCase)) subjects.Add(s);
            }
        }
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.IsNullOrWhiteSpace(cn) && !subjects.Contains(cn, StringComparer.OrdinalIgnoreCase)) subjects.Add(cn);

        var issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        if (string.IsNullOrWhiteSpace(issuer)) issuer = cert.Issuer;
        return new CertificateMetadata(subjects, issuer, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), cert.Thumbprint);
    }

    /// <summary>Reads metadata of the first certificate in a PEM file (no key needed).</summary>
    public static CertificateMetadata ReadMetadataFromPem(string pem)
    {
        var certs = LoadPemCertificates(pem);
        try
        {
            return ReadMetadata(certs[0]);
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
        }
    }

    /// <summary>Reads PEM cert + key files from disk (existence, readability, key match).</summary>
    public static ParsedCertificate FromFiles(string certPath, string keyPath)
    {
        return FromPem(ReadText(certPath, "certificate"), ReadText(keyPath, "private key"));
    }

    internal static string ReadText(string path, string what) =>
        System.Text.Encoding.UTF8.GetString(ReadBytes(path, what, MaxPemBytes));

    /// <summary>
    /// Reads a referenced file. Every failure (missing, no access, a folder, too large) gives the same message so the
    /// endpoint cannot be used to probe which files exist on the server.
    /// </summary>
    internal static byte[] ReadBytes(string path, string what, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CertificateImportException($"The {what} path is required.");
        if (!Path.IsPathFullyQualified(path)) throw new CertificateImportException($"The {what} path must be absolute (e.g. C:\\certs\\site.pem or \\\\server\\share\\site.pem).");
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= maxBytes) return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            // fall through to the generic message
        }
        throw new CertificateImportException(CannotRead(what, path));
    }

    /// <summary>The generic "cannot read" message for referenced files.</summary>
    public static string CannotRead(string what, string path) =>
        $"The {what} file cannot be read: {path}. Check that the path is correct, that it is a file of at most a few MB, and that the service account can read it (LocalSystem on this server; the computer account DOMAIN\\SERVER$ on network shares).";
}
