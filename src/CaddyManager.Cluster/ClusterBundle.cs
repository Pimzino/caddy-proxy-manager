using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Cluster;

/// <summary>A configuration bundle built by the primary: canonical JSON plus its SHA-256 revision.</summary>
public sealed record BundleSnapshot(JsonObject Bundle, string Revision, List<string> Warnings);

/// <summary>
/// The replicated configuration (SPEC "Cluster module / Replicated"): SiteHost, StreamHost, AccessList, Certificate (with
/// the PEM chain and key text), CaddySettings except NodeLocalProperties (secrets un-protected — they only ever travel
/// inside the encrypted RPC envelope and are re-protected by the node) and BinarySettings.Plugins.
/// </summary>
public static class ClusterBundle
{
    public const int Version = 1;
    private const string ProtectedSuffix = "Protected";

    /// <summary>CaddySettings secrets (string properties named *Protected) — re-protected on the node with its own ISecretProtector.</summary>
    internal static readonly PropertyInfo[] SecretProperties = typeof(CaddySettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith(ProtectedSuffix, StringComparison.Ordinal) && p.CanWrite)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    internal static readonly PropertyInfo[] NodeLocalProperties = CaddySettings.NodeLocalProperties
        .Select(n => typeof(CaddySettings).GetProperty(n) ?? throw new InvalidOperationException($"CaddySettings.{n} does not exist"))
        .ToArray();

    /// <summary>Builds the bundle from the primary's store.</summary>
    public static BundleSnapshot Build(IStore store, ISecretProtector secrets)
    {
        var warnings = new List<string>();
        var o = JsonDefaults.Storage;
        var bundle = new JsonObject
        {
            ["v"] = Version,
            ["hosts"] = Array(store.Col<SiteHost>().FindAll().OrderBy(h => h.Id, StringComparer.Ordinal), o),
            ["streams"] = Array(store.Col<StreamHost>().FindAll().OrderBy(h => h.Id, StringComparer.Ordinal), o),
            ["accessLists"] = Array(store.Col<AccessList>().FindAll().OrderBy(h => h.Id, StringComparer.Ordinal), o),
        };

        var certs = new JsonArray();
        foreach (var c in store.Col<Certificate>().FindAll().OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            string certPem, keyPem;
            try
            {
                certPem = ReadCertificatePem(c.CertPath);
                keyPem = File.ReadAllText(c.KeyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                // Still listed (without material): the certificate exists on the primary, only its files could not be read
                // just now (a renewal in progress, a share briefly unreachable). Nodes keep the copy they have instead of
                // deleting it and dropping every site that uses it.
                warnings.Add($"Certificate '{c.Name}' could not be read on the primary ({ex.Message}); nodes keep the copy they already have.");
                certs.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["materialUnavailable"] = true,
                });
                continue;
            }
            // Only what a node needs: it stores the certificate as Uploaded in its own certificate store. Source-specific
            // fields (paths, PFX password, Windows store selectors, sync timestamps) belong to the primary.
            certs.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["notes"] = c.Notes,
                ["subjects"] = JsonSerializer.SerializeToNode(c.Subjects, o),
                ["issuer"] = c.Issuer,
                ["notBefore"] = JsonSerializer.SerializeToNode(c.NotBefore, o),
                ["notAfter"] = JsonSerializer.SerializeToNode(c.NotAfter, o),
                ["thumbprint"] = c.Thumbprint,
                ["createdAt"] = JsonSerializer.SerializeToNode(c.CreatedAt, o),
                ["certPem"] = certPem,
                ["keyPem"] = keyPem,
            });
        }
        bundle["certificates"] = certs;

        var settings = store.GetSettings<CaddySettings>();
        var settingsNode = (JsonObject)JsonSerializer.SerializeToNode(settings, o)!;
        var secretNode = new JsonObject();
        foreach (var p in SecretProperties)
        {
            settingsNode.Remove(JsonName(p));
            var value = (string?)p.GetValue(settings);
            if (string.IsNullOrEmpty(value)) continue;
            try
            {
                secretNode[JsonName(p)] = secrets.Unprotect(value);
            }
            catch (CryptographicException)
            {
                warnings.Add($"Caddy setting '{JsonName(p)[..^ProtectedSuffix.Length]}' could not be decrypted on the primary and was not replicated; enter it again.");
            }
        }
        foreach (var p in NodeLocalProperties) settingsNode.Remove(JsonName(p));
        bundle["caddySettings"] = settingsNode;
        bundle["caddySecrets"] = secretNode;
        bundle["plugins"] = JsonSerializer.SerializeToNode(store.GetSettings<BinarySettings>().Plugins, o);

        // CustomAcmeRootPath is node-local (a path on the primary means nothing on a node): the root certificate itself is
        // replicated and each node writes it to its own data folder.
        if (settings.AcmeCa == AcmeCa.Custom && !string.IsNullOrWhiteSpace(settings.CustomAcmeRootPath))
        {
            try
            {
                bundle["customAcmeRootPem"] = ReadCertificatePem(settings.CustomAcmeRootPath.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"The custom ACME root certificate '{settings.CustomAcmeRootPath}' could not be read on the primary ({ex.Message}); nodes keep the copy they already have.");
                bundle["customAcmeRootUnavailable"] = true;
            }
        }

        var canonical = Canonicalize(bundle);
        return new BundleSnapshot(canonical, Revision(canonical), warnings);
    }

    /// <summary>SHA-256 (lower-case hex) of the canonical JSON text.</summary>
    public static string Revision(JsonNode canonicalBundle) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalBundle.ToJsonString())));

    /// <summary>Deep copy with object properties ordered by ordinal name (arrays keep their order), so equal content has equal text.</summary>
    public static JsonObject Canonicalize(JsonObject node) => (JsonObject)CanonicalizeNode(node)!;

    private static JsonNode? CanonicalizeNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, CanonicalizeNode(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(CanonicalizeNode).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    internal static string JsonName(PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    private static JsonArray Array<T>(IEnumerable<T> items, JsonSerializerOptions o) =>
        new(items.Select(i => JsonSerializer.SerializeToNode(i, o)).ToArray());

    /// <summary>PEM text of a certificate file (a DER file referenced by path is converted).</summary>
    private static string ReadCertificatePem(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal)) return text;
        using var cert = X509CertificateLoader.LoadCertificate(bytes);
        return cert.ExportCertificatePem() + "\n";
    }
}

/// <summary>Replicated data decoded from a bundle on the node.</summary>
internal sealed class BundleContent
{
    public List<SiteHost> Hosts { get; init; } = new();
    public List<StreamHost> Streams { get; init; } = new();
    public List<AccessList> AccessLists { get; init; } = new();
    public List<BundleCertificate> Certificates { get; init; } = new();
    public JsonObject CaddySettings { get; init; } = new();
    public Dictionary<string, string> CaddySecrets { get; init; } = new();
    public List<string> Plugins { get; init; } = new();
    /// <summary>PEM of the primary's custom ACME CA root (AcmeCa Custom with a root file), written to the node's data folder.</summary>
    public string? CustomAcmeRootPem { get; init; }
    /// <summary>The primary uses a custom ACME root but could not read it: the node keeps what it has.</summary>
    public bool CustomAcmeRootUnavailable { get; init; }

    public static BundleContent Parse(JsonObject bundle)
    {
        var o = JsonDefaults.Storage;
        if (bundle["v"]?.GetValue<int>() != ClusterBundle.Version)
            throw new InvalidDataException("The configuration bundle has an unsupported version; update Caddy Proxy Manager on this server.");
        return new BundleContent
        {
            Hosts = bundle["hosts"]?.Deserialize<List<SiteHost>>(o) ?? new(),
            Streams = bundle["streams"]?.Deserialize<List<StreamHost>>(o) ?? new(),
            AccessLists = bundle["accessLists"]?.Deserialize<List<AccessList>>(o) ?? new(),
            Certificates = bundle["certificates"]?.Deserialize<List<BundleCertificate>>(o) ?? new(),
            CaddySettings = bundle["caddySettings"] as JsonObject ?? new JsonObject(),
            CaddySecrets = bundle["caddySecrets"]?.Deserialize<Dictionary<string, string>>(o) ?? new(),
            Plugins = bundle["plugins"]?.Deserialize<List<string>>(o) ?? new(),
            CustomAcmeRootPem = bundle["customAcmeRootPem"]?.GetValue<string>(),
            CustomAcmeRootUnavailable = bundle["customAcmeRootUnavailable"]?.GetValue<bool>() ?? false,
        };
    }
}

internal sealed class BundleCertificate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public List<string> Subjects { get; set; } = new();
    public string Issuer { get; set; } = "";
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string Thumbprint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CertPem { get; set; } = "";
    public string KeyPem { get; set; } = "";
    /// <summary>The primary could not read the files just now: keep this node's copy (see ClusterBundle.Build).</summary>
    public bool MaterialUnavailable { get; set; }
}
