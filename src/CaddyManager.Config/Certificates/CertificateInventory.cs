using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Certificates;

/// <summary>Custom certificates from the database merged with certificates Caddy keeps in its storage.</summary>
public sealed class CertificateInventory(IStore store, AppPaths paths, ILogger<CertificateInventory> logger) : ICertificateInventory
{
    public const string InternalIssuerDir = "local";
    public const string InternalRootId = "pki/authorities/local/root";

    public string InternalRootPath => Path.Combine(paths.CaddyStorageDir, "pki", "authorities", "local", "root.crt");

    public Task<List<CertificateInfo>> ListAsync(CancellationToken ct = default) => Task.Run(() => List(ct), ct);

    private List<CertificateInfo> List(CancellationToken ct)
    {
        var hosts = store.Col<SiteHost>().FindAll().ToList();
        var now = DateTime.UtcNow;
        var result = new List<CertificateInfo>();

        foreach (var c in store.Col<Certificate>().FindAll().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new CertificateInfo
            {
                Id = c.Id,
                Kind = CertificateKind.Custom,
                Name = c.Name,
                Subjects = c.Subjects.ToList(),
                Issuer = c.Issuer,
                NotBefore = c.NotBefore,
                NotAfter = c.NotAfter,
                DaysRemaining = DaysRemaining(c.NotAfter, now),
                CertPath = c.CertPath,
                KeyPath = c.KeyPath,
                Source = c.Source == CertificateSource.Uploaded ? "uploaded" : "filePath",
                UsedByHostIds = hosts.Where(h => h.Tls == TlsMode.Custom && h.CertificateId == c.Id).Select(h => h.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Error = FileError(c),
                Notes = c.Notes,
            });
        }

        result.AddRange(ScanStorage(hosts, now, ct));
        return result;
    }

    private static int DaysRemaining(DateTime notAfter, DateTime now) =>
        notAfter == default ? 0 : (int)Math.Floor((notAfter - now).TotalDays);

    private static string? FileError(Certificate c)
    {
        var errors = new List<string>();
        Check(c.CertPath, "Certificate file");
        Check(c.KeyPath, "Key file");
        return errors.Count == 0 ? null : string.Join(" ", errors);

        void Check(string path, string what)
        {
            if (string.IsNullOrWhiteSpace(path)) { errors.Add($"{what} path is not set."); return; }
            try
            {
                if (!File.Exists(path)) { errors.Add($"{what} not found: {path}."); return; }
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{what} cannot be read ({ex.Message}): {path}.");
            }
        }
    }

    private IEnumerable<CertificateInfo> ScanStorage(List<SiteHost> hosts, DateTime now, CancellationToken ct)
    {
        var list = new List<CertificateInfo>();
        var certRoot = Path.Combine(paths.CaddyStorageDir, "certificates");
        if (Directory.Exists(certRoot))
        {
            try
            {
                foreach (var issuerDir in Directory.EnumerateDirectories(certRoot).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var issuerName = Path.GetFileName(issuerDir);
                    var kind = issuerName == InternalIssuerDir ? CertificateKind.Internal : CertificateKind.Acme;
                    foreach (var nameDir in Directory.EnumerateDirectories(issuerDir).OrderBy(d => d, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(nameDir);
                        var crt = Path.Combine(nameDir, name + ".crt");
                        if (!File.Exists(crt)) continue;
                        var key = Path.Combine(nameDir, name + ".key");
                        var id = $"certificates/{issuerName}/{name}";
                        list.Add(Describe(id, kind, name, crt, File.Exists(key) ? key : null, issuerName, hosts, now,
                            tls => kind == CertificateKind.Internal ? tls == TlsMode.Internal : tls == TlsMode.Acme));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not scan Caddy certificate storage {Dir}", certRoot);
            }
        }

        if (File.Exists(InternalRootPath))
        {
            var info = Describe(InternalRootId, CertificateKind.InternalRoot, "Caddy Local Authority (root CA)", InternalRootPath, null,
                InternalIssuerDir, hosts, now, _ => false);
            list.Add(info with
            {
                UsedByHostIds = hosts.Where(h => h.Tls == TlsMode.Internal).Select(h => h.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            });
        }
        return list;
    }

    private CertificateInfo Describe(string id, CertificateKind kind, string name, string crt, string? key, string source,
        List<SiteHost> hosts, DateTime now, Func<TlsMode, bool> tlsFilter)
    {
        try
        {
            var meta = CertificateParser.ReadMetadataFromPem(File.ReadAllText(crt));
            return new CertificateInfo
            {
                Id = id,
                Kind = kind,
                Name = name.Replace("wildcard_", "*", StringComparison.Ordinal),
                Subjects = meta.Subjects,
                Issuer = meta.Issuer,
                NotBefore = meta.NotBefore,
                NotAfter = meta.NotAfter,
                DaysRemaining = DaysRemaining(meta.NotAfter, now),
                CertPath = crt,
                KeyPath = key,
                Source = source,
                UsedByHostIds = hosts
                    .Where(h => tlsFilter(h.Tls) && h.Domains.Any(d => meta.Subjects.Any(s => Matches(s, d))))
                    .Select(h => h.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CertificateImportException)
        {
            logger.LogDebug(ex, "Could not read {File}", crt);
            return new CertificateInfo
            {
                Id = id,
                Kind = kind,
                Name = name,
                CertPath = crt,
                KeyPath = key,
                Source = source,
                Error = "Certificate could not be read: " + ex.Message,
            };
        }
    }

    /// <summary>Certificate subject (possibly wildcard) covers the host name.</summary>
    internal static bool Matches(string subject, string domain)
    {
        var s = subject.Trim().ToLowerInvariant();
        var d = domain.Trim().ToLowerInvariant();
        if (s == d) return true;
        if (s.StartsWith("*.", StringComparison.Ordinal) && !d.StartsWith("*.", StringComparison.Ordinal))
        {
            var dot = d.IndexOf('.');
            return dot > 0 && d[(dot + 1)..] == s[2..];
        }
        return false;
    }
}
