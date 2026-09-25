using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace CaddyManager.Config.Certificates;

/// <summary>A certificate in a Windows certificate store (wire shape of GET /api/certificates/windows-store).</summary>
public sealed record StoreCertificateInfo
{
    public string Thumbprint { get; init; } = "";
    public string Subject { get; init; } = "";
    public List<string> DnsNames { get; init; } = new();
    public string Issuer { get; init; } = "";
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public bool HasPrivateKey { get; init; }
    /// <summary>The private key may be exported (Caddy needs it as a PEM file).</summary>
    public bool Exportable { get; init; }
    /// <summary>AD CS certificate template (name or OID), when present.</summary>
    public string? Template { get; init; }
    /// <summary>Usable for TLS servers: no Enhanced Key Usage extension, or one that includes Server Authentication.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ServerAuth { get; init; } = true;
    /// <summary>Common name of the subject (used for subject matching).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CommonName { get; init; }
}

/// <summary>Reads the Windows certificate store. Injectable so the selection/sync logic is testable on every OS.</summary>
public interface IWindowsCertificateSource
{
    /// <summary>False on non-Windows systems (List returns an empty list, Export throws).</summary>
    bool IsSupported { get; }
    IReadOnlyList<StoreCertificateInfo> List(string storeLocation, string storeName);
    /// <summary>Exports the certificate (chain + private key) as PEM. Throws CertificateImportException with a sysadmin-friendly message.</summary>
    ParsedCertificate Export(string storeLocation, string storeName, string thumbprint);
}

public static partial class WindowsStoreNames
{
    public const string LocalMachine = "LocalMachine";
    public const string CurrentUser = "CurrentUser";
    public const string DefaultStore = "My";

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoreNameRegex();

    /// <summary>Canonical store location ("LocalMachine"/"CurrentUser") or null when invalid.</summary>
    public static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return LocalMachine;
        var l = location.Trim();
        if (l.Equals(LocalMachine, StringComparison.OrdinalIgnoreCase)) return LocalMachine;
        if (l.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase)) return CurrentUser;
        return null;
    }

    public static string? NormalizeStoreName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultStore;
        var n = name.Trim();
        return StoreNameRegex().IsMatch(n) ? n : null;
    }

    /// <summary>Thumbprint in canonical form (upper-case hex, separators and invisible characters removed).</summary>
    public static string NormalizeThumbprint(string? thumbprint) =>
        new string((thumbprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}

/// <summary>Which store certificate a WindowsStore certificate uses.</summary>
public static class WindowsStoreSelector
{
    /// <summary>
    /// Thumbprint pin: exactly that certificate (it must have a private key). Subject mode: the newest currently valid
    /// certificate with a private key and Server Authentication usage whose subject CN or a DNS SAN matches the name
    /// (a wildcard SAN covering the name also matches) — this follows AD CS autoenrollment and certreq renewals.
    /// </summary>
    public static StoreCertificateInfo Select(IReadOnlyList<StoreCertificateInfo> certs, string? thumbprint, string? subject, DateTime utcNow, string where)
    {
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            var tp = WindowsStoreNames.NormalizeThumbprint(thumbprint);
            var match = certs.FirstOrDefault(c => WindowsStoreNames.NormalizeThumbprint(c.Thumbprint) == tp)
                ?? throw new CertificateImportException($"No certificate with thumbprint {tp} was found in {where}.");
            if (!match.HasPrivateKey)
                throw new CertificateImportException($"The certificate {tp} ({match.Subject}) in {where} has no private key. Import it together with its key (PFX) or enroll it on this server.");
            return match;
        }
        if (string.IsNullOrWhiteSpace(subject))
            throw new CertificateImportException("Specify either a thumbprint or a subject/DNS name.");

        var name = subject.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.StartsWith("cn=", StringComparison.Ordinal)) name = name[3..].Trim();
        var named = certs.Where(c => Matches(c, name)).ToList();
        if (named.Count == 0)
            throw new CertificateImportException($"No certificate for '{subject.Trim()}' (subject CN or DNS name) was found in {where}.");
        var candidates = named
            .Where(c => c.HasPrivateKey && c.ServerAuth && c.NotBefore <= utcNow && c.NotAfter > utcNow)
            .OrderByDescending(c => c.NotBefore)
            .ThenByDescending(c => c.NotAfter)
            .ThenBy(c => c.Thumbprint, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > 0) return candidates[0];

        var reasons = new List<string>();
        if (named.All(c => !c.HasPrivateKey)) reasons.Add("none has a private key");
        if (named.All(c => c.NotAfter <= utcNow || c.NotBefore > utcNow)) reasons.Add("none is currently valid");
        if (named.All(c => !c.ServerAuth)) reasons.Add("none allows Server Authentication");
        var why = reasons.Count > 0 ? string.Join(", ", reasons) : "none is valid, has a private key and allows Server Authentication";
        throw new CertificateImportException($"{named.Count} certificate(s) for '{subject.Trim()}' exist in {where}, but {why}.");
    }

    internal static bool Matches(StoreCertificateInfo c, string name)
    {
        if (string.Equals(c.CommonName, name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var dns in c.DnsNames)
        {
            if (string.Equals(dns, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!name.StartsWith("*.", StringComparison.Ordinal) && CertificateInventory.Matches(dns, name)) return true;
        }
        return false;
    }
}

/// <summary>X509Store-backed source (Windows only; elsewhere the store is empty).</summary>
public sealed class WindowsCertificateStoreSource : IWindowsCertificateSource
{
    public bool IsSupported => OperatingSystem.IsWindows();

    internal const string NotExportableMessage =
        "The private key of this certificate is not exportable, but Caddy needs the key as a PEM file. Enroll the certificate with an exportable key: in the AD CS certificate template enable \"Allow private key to be exported\" (Request Handling tab) and re-enroll, or re-import the PFX with \"Mark this key as exportable\".";

    public IReadOnlyList<StoreCertificateInfo> List(string storeLocation, string storeName)
    {
        if (!OperatingSystem.IsWindows()) return [];
        return ListWindows(storeLocation, storeName);
    }

    public ParsedCertificate Export(string storeLocation, string storeName, string thumbprint)
    {
        if (!OperatingSystem.IsWindows())
            throw new CertificateImportException("The Windows certificate store is only available when Caddy Proxy Manager runs on Windows.");
        return ExportWindows(storeLocation, storeName, thumbprint);
    }

    [SupportedOSPlatform("windows")]
    private static X509Store Open(string storeLocation, string storeName)
    {
        var location = storeLocation == WindowsStoreNames.CurrentUser ? StoreLocation.CurrentUser : StoreLocation.LocalMachine;
        var store = new X509Store(storeName, location);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException ex)
        {
            store.Dispose();
            throw new CertificateImportException($"The certificate store {storeLocation}\\{storeName} could not be opened ({ex.Message.Trim()}).");
        }
        return store;
    }

    [SupportedOSPlatform("windows")]
    private static List<StoreCertificateInfo> ListWindows(string storeLocation, string storeName)
    {
        using var store = Open(storeLocation, storeName);
        var list = new List<StoreCertificateInfo>();
        foreach (var cert in store.Certificates)
        {
            using (cert)
            {
                var meta = CertificateParser.ReadMetadata(cert);
                list.Add(new StoreCertificateInfo
                {
                    Thumbprint = cert.Thumbprint,
                    Subject = cert.Subject,
                    CommonName = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    DnsNames = DnsNames(cert),
                    Issuer = meta.Issuer,
                    NotBefore = meta.NotBefore,
                    NotAfter = meta.NotAfter,
                    HasPrivateKey = cert.HasPrivateKey,
                    Exportable = cert.HasPrivateKey && IsExportable(cert),
                    Template = TemplateName(cert),
                    ServerAuth = AllowsServerAuth(cert),
                });
            }
        }
        return list.OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase).ThenByDescending(c => c.NotAfter).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static ParsedCertificate ExportWindows(string storeLocation, string storeName, string thumbprint)
    {
        using var store = Open(storeLocation, storeName);
        var tp = WindowsStoreNames.NormalizeThumbprint(thumbprint);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, tp, validOnly: false);
        try
        {
            if (found.Count == 0) throw new CertificateImportException($"No certificate with thumbprint {tp} was found in {storeLocation}\\{storeName}.");
            var leaf = found[0];
            if (!leaf.HasPrivateKey) throw new CertificateImportException($"The certificate {tp} in {storeLocation}\\{storeName} has no private key.");

            // Intermediates from the machine's stores (the root is not sent to clients).
            var chainCerts = new List<X509Certificate2>();
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority | X509VerificationFlags.IgnoreNotTimeValid;
            chain.Build(leaf);
            foreach (var el in chain.ChainElements)
            {
                var c = el.Certificate;
                if (c.Thumbprint == leaf.Thumbprint) continue;
                if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) continue; // self-signed root
                chainCerts.Add(c);
            }
            try
            {
                return CertificateParser.FromCertificate(leaf, chainCerts, NotExportableMessage);
            }
            finally
            {
                foreach (var c in chainCerts) c.Dispose();
            }
        }
        finally
        {
            foreach (var c in found) c.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsExportable(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng rsaCng) return Allows(rsaCng.Key.ExportPolicy);
            if (rsa is RSACryptoServiceProvider csp) return csp.CspKeyContainerInfo.Exportable;
            using var ec = cert.GetECDsaPrivateKey();
            if (ec is ECDsaCng ecCng) return Allows(ecCng.Key.ExportPolicy);
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }

        static bool Allows(CngExportPolicies p) => (p & (CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport)) != 0;
    }

    internal static List<string> DnsNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            var san = ext as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
            foreach (var dns in san.EnumerateDnsNames())
                if (!names.Contains(dns, StringComparer.OrdinalIgnoreCase)) names.Add(dns);
        }
        return names;
    }

    internal static bool AllowsServerAuth(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null) return true;
        foreach (var oid in eku.EnhancedKeyUsages)
            if (oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0") return true;
        return false;
    }

    /// <summary>AD CS template: v2 extension (template OID) or v1 extension (template name).</summary>
    internal static string? TemplateName(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            try
            {
                if (ext.Oid?.Value == "1.3.6.1.4.1.311.21.7")
                {
                    var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
                    var oid = reader.ReadObjectIdentifier();
                    var friendly = Oid.FromOidValue(oid, OidGroup.All).FriendlyName;
                    return string.IsNullOrWhiteSpace(friendly) ? oid : friendly;
                }
                if (ext.Oid?.Value == "1.3.6.1.4.1.311.20.2")
                    return new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.BMPString);
            }
            catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
            {
                // Friendly name lookup failed: fall back to the raw OID when it could be read.
                if (ext.Oid?.Value == "1.3.6.1.4.1.311.21.7")
                {
                    try
                    {
                        return new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence().ReadObjectIdentifier();
                    }
                    catch (AsnContentException)
                    {
                    }
                }
            }
        }
        return null;
    }
}
