namespace CaddyManager.Core.Models;

public abstract class Entity
{
    public string Id { get; set; } = NewId();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>What a site does.</summary>
public enum HostKind
{
    /// <summary>Reverse proxy to one or more upstreams ("Proxy Host").</summary>
    Proxy,
    /// <summary>HTTP redirect to another URL ("Redirection Host").</summary>
    Redirect,
    /// <summary>Serve files from a directory (Caddy file_server).</summary>
    Static,
    /// <summary>Fixed status/body response ("404 Host" / maintenance page).</summary>
    Response,
}

public enum TlsMode
{
    /// <summary>Plain HTTP only; no certificate.</summary>
    None,
    /// <summary>Automatic public certificate via ACME (Let's Encrypt / ZeroSSL / custom CA from settings).</summary>
    Acme,
    /// <summary>Certificate from Caddy's internal CA (for internal names; distribute root via GPO).</summary>
    Internal,
    /// <summary>User supplied certificate (Certificate entity referenced by CertificateId).</summary>
    Custom,
}

/// <summary>Per-host ACME challenge choice.</summary>
public enum HostAcmeChallenge
{
    /// <summary>CaddySettings.DefaultAcmeChallenge (wildcard domains always use DNS when a provider is configured).</summary>
    Default,
    Http,
    Dns,
}

/// <summary>Per-host DNS challenge delegation (CNAME _acme-challenge.&lt;domain&gt; → a name in a separate validation zone).</summary>
public enum HostDnsDelegation
{
    /// <summary>CaddySettings.DnsOverrideDomain (no delegation when that is empty).</summary>
    Default,
    /// <summary>No delegation: the TXT record is written in the domain's own zone.</summary>
    Off,
    /// <summary>SiteHost.DnsOverrideDomain.</summary>
    Custom,
}

public enum LoadBalancingPolicy { RoundRobin, Random, LeastConn, IpHash, First, Cookie, UriHash }
public enum UpstreamScheme { Http, Https }
public enum HeaderAction { Set, Add, Delete }

public sealed class Upstream
{
    public UpstreamScheme Scheme { get; set; } = UpstreamScheme.Http;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;
}

public sealed class HeaderOp
{
    public HeaderAction Action { get; set; } = HeaderAction.Set;
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class HealthCheck
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "/";
    public int IntervalSeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 5;
    /// <summary>Expected status code, 0 = any 2xx.</summary>
    public int ExpectStatus { get; set; }
}

/// <summary>NPM "custom location": a path prefix proxied to a different upstream set.</summary>
public sealed class ProxyLocation
{
    public string Path { get; set; } = "/";
    public List<Upstream> Upstreams { get; set; } = new();
    public bool StripPrefix { get; set; }
    public bool UpstreamTlsInsecure { get; set; }
}

public sealed class SiteHost : Entity
{
    public HostKind Kind { get; set; } = HostKind.Proxy;
    public bool Enabled { get; set; } = true;
    /// <summary>Hostnames this site answers to. Wildcards like *.example.com allowed.</summary>
    public List<string> Domains { get; set; } = new();
    public string? Notes { get; set; }

    // ---- TLS / HTTP behaviour (all kinds)
    public TlsMode Tls { get; set; } = TlsMode.Acme;
    /// <summary>Certificate.Id when Tls == Custom.</summary>
    public string? CertificateId { get; set; }
    /// <summary>ACME challenge for this host (Tls == Acme only).</summary>
    public HostAcmeChallenge AcmeChallenge { get; set; } = HostAcmeChallenge.Default;
    /// <summary>DNS challenge delegation for this host (effective challenge Dns only).</summary>
    public HostDnsDelegation DnsDelegation { get; set; } = HostDnsDelegation.Default;
    /// <summary>Delegated challenge record name when DnsDelegation == Custom, e.g. "_acme-challenge.shop.validation.example.net".</summary>
    public string? DnsOverrideDomain { get; set; }
    /// <summary>Redirect HTTP to HTTPS (only meaningful when Tls != None).</summary>
    public bool ForceHttps { get; set; } = true;
    public bool Hsts { get; set; }
    public bool HstsSubdomains { get; set; }
    public int HstsMaxAgeSeconds { get; set; } = 31536000;
    /// <summary>Enable gzip + zstd response compression.</summary>
    public bool Compression { get; set; } = true;
    public string? AccessListId { get; set; }
    /// <summary>Block common exploit patterns (SQLi/path traversal probes) with 403, like NPM.</summary>
    public bool BlockExploits { get; set; }
    /// <summary>Write an access log for this host to logs/access/{firstDomain}.log.</summary>
    public bool AccessLog { get; set; }
    public List<HeaderOp> ResponseHeaders { get; set; } = new();

    // ---- Proxy
    public List<Upstream> Upstreams { get; set; } = new();
    public LoadBalancingPolicy LoadBalancing { get; set; } = LoadBalancingPolicy.RoundRobin;
    public HealthCheck HealthCheck { get; set; } = new();
    /// <summary>Skip TLS verification to HTTPS upstreams (self-signed backends).</summary>
    public bool UpstreamTlsInsecure { get; set; }
    /// <summary>
    /// Upstream uses Windows Integrated Authentication (NTLM/Negotiate — IIS, SharePoint, SSRS, Exchange).
    /// Uses Caddy's http_ntlm transport (plugin github.com/caddyserver/ntlm-transport) for connection affinity.
    /// </summary>
    public bool UpstreamNtlm { get; set; }
    /// <summary>null/"" = keep the client's Host header (Caddy default); "{upstream}" = upstream host:port; otherwise literal value.</summary>
    public string? UpstreamHostHeader { get; set; }
    public List<HeaderOp> RequestHeaders { get; set; } = new();
    public List<ProxyLocation> Locations { get; set; } = new();

    // ---- Redirect
    public string? RedirectTarget { get; set; }
    public int RedirectCode { get; set; } = 301;
    /// <summary>Append the original request URI (path + query) to the target.</summary>
    public bool PreservePath { get; set; } = true;

    // ---- Static
    public string? RootPath { get; set; }
    public bool Browse { get; set; }
    /// <summary>Rewrite unknown paths to /index.html (single-page apps).</summary>
    public bool SpaFallback { get; set; }

    // ---- Response
    public int ResponseStatus { get; set; } = 404;
    public string? ResponseBody { get; set; }
    public string ResponseContentType { get; set; } = "text/plain; charset=utf-8";

    // ---- Advanced
    /// <summary>Raw Caddy JSON: an array of route objects inserted before this host's generated handlers.</summary>
    public string? AdvancedRoutesJson { get; set; }
}

public enum StreamProtocol { Tcp, Udp }

/// <summary>Layer-4 TCP/UDP forwarder. Requires the github.com/mholt/caddy-l4 plugin.</summary>
public sealed class StreamHost : Entity
{
    public bool Enabled { get; set; } = true;
    public StreamProtocol Protocol { get; set; } = StreamProtocol.Tcp;
    public int ListenPort { get; set; }
    public string UpstreamHost { get; set; } = "";
    public int UpstreamPort { get; set; }
    public string? Notes { get; set; }
}

public enum IpRuleAction { Allow, Deny }

public sealed class IpRule
{
    public IpRuleAction Action { get; set; } = IpRuleAction.Allow;
    /// <summary>IP or CIDR, e.g. 10.0.0.0/8, 192.168.1.10, or "all".</summary>
    public string Cidr { get; set; } = "";
}

public sealed class AccessUser
{
    public string Username { get; set; } = "";
    /// <summary>bcrypt hash (Caddy http_basic compatible). Never returned by the API.</summary>
    public string PasswordHash { get; set; } = "";
}

public sealed class AccessList : Entity
{
    public string Name { get; set; } = "";
    /// <summary>true: client passes if EITHER the IP rules allow OR basic auth succeeds. false: must satisfy both.</summary>
    public bool SatisfyAny { get; set; }
    public List<AccessUser> Users { get; set; } = new();
    /// <summary>Evaluated in order; first match wins. Implicit final rule: deny if any Allow rule exists, else allow.</summary>
    public List<IpRule> Rules { get; set; } = new();
    /// <summary>Forward the Authorization header to the upstream.</summary>
    public bool PassAuthToUpstream { get; set; }
}

public enum CertificateSource
{
    /// <summary>Uploaded through the UI; PEM files written to the certificate store.</summary>
    Uploaded,
    /// <summary>References cert/key files that already exist on disk or a share (renewed externally; auto reloaded on change).</summary>
    FilePath,
    /// <summary>References a .pfx/.p12 on disk or a share (e.g. win-acme output); converted to PEM in the store and re-converted on change.</summary>
    PfxFile,
    /// <summary>
    /// Exported from the Windows certificate store (LocalMachine\My by default) by thumbprint or by subject/SAN match
    /// (newest valid cert wins — follows AD CS autoenrollment / certreq renewals). Re-synced periodically.
    /// </summary>
    WindowsStore,
}

public sealed class Certificate : Entity
{
    public string Name { get; set; } = "";
    public CertificateSource Source { get; set; }
    /// <summary>Absolute path to PEM certificate chain.</summary>
    public string CertPath { get; set; } = "";
    /// <summary>Absolute path to PEM private key.</summary>
    public string KeyPath { get; set; } = "";
    // Parsed metadata (refreshed on import and periodically)
    public List<string> Subjects { get; set; } = new();
    public string Issuer { get; set; } = "";
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string Thumbprint { get; set; } = "";
    public string? Notes { get; set; }

    // ---- PfxFile source
    /// <summary>Path of the referenced .pfx/.p12 (PfxFile source). CertPath/KeyPath point at the converted PEMs in the store.</summary>
    public string? SourcePath { get; set; }
    /// <summary>PFX password, protected with ISecretProtector.</summary>
    public string? PfxPasswordProtected { get; set; }

    // ---- WindowsStore source
    /// <summary>"LocalMachine" (default) or "CurrentUser".</summary>
    public string StoreLocation { get; set; } = "LocalMachine";
    /// <summary>Store name, default "My" (Personal).</summary>
    public string StoreName { get; set; } = "My";
    /// <summary>Pin to one certificate by thumbprint (no automatic renewal following).</summary>
    public string? StoreThumbprint { get; set; }
    /// <summary>Follow renewals: newest currently-valid cert with a private key whose subject CN or SAN matches this name.</summary>
    public string? StoreSubject { get; set; }

    // ---- Sync status (PfxFile / WindowsStore / FilePath)
    public DateTime? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
}
