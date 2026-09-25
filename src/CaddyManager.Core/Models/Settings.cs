namespace CaddyManager.Core.Models;

/// <summary>Marker for singleton settings documents stored via IStore.GetSettings/SaveSettings.</summary>
public interface ISettingsDocument { }

public enum AcmeCa { LetsEncrypt, LetsEncryptStaging, ZeroSsl, Custom }
public enum DefaultSiteBehavior { NotFound, CloseConnection, Redirect, CaddyWelcome }
public enum ConfigMode
{
    /// <summary>Config generated from hosts/certs/access lists in the UI.</summary>
    Managed,
    /// <summary>User-authored Caddyfile is loaded verbatim (escape hatch).</summary>
    Caddyfile,
}

/// <summary>Global Caddy behaviour. Owned by the Config module.</summary>
public sealed class CaddySettings : ISettingsDocument
{
    public ConfigMode Mode { get; set; } = ConfigMode.Managed;
    public string RawCaddyfile { get; set; } = "";

    // ACME
    public string AcmeEmail { get; set; } = "";
    public AcmeCa AcmeCa { get; set; } = AcmeCa.LetsEncrypt;
    public string? CustomAcmeDirectory { get; set; }
    /// <summary>External account binding (ZeroSSL / some private CAs).</summary>
    public string? EabKeyId { get; set; }
    /// <summary>Protected with ISecretProtector.</summary>
    public string? EabMacKeyProtected { get; set; }
    /// <summary>Trust this PEM root when talking to a custom ACME CA (path).</summary>
    public string? CustomAcmeRootPath { get; set; }
    public bool DisableHttpChallenge { get; set; }
    public bool DisableTlsAlpnChallenge { get; set; }

    // Listeners
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool EnableHttp3 { get; set; } = true;
    /// <summary>Bind to specific addresses (empty = all interfaces).</summary>
    public List<string> BindAddresses { get; set; } = new();

    public DefaultSiteBehavior DefaultSite { get; set; } = DefaultSiteBehavior.NotFound;
    public string? DefaultRedirectUrl { get; set; }

    /// <summary>CIDRs of trusted upstream proxies/load balancers (for real client IP).</summary>
    public List<string> TrustedProxies { get; set; } = new();
    /// <summary>"debug" | "info" | "warn" | "error"</summary>
    public string LogLevel { get; set; } = "info";

    /// <summary>Caddy admin API listen address. Keep on loopback.</summary>
    public string AdminListen { get; set; } = "127.0.0.1:2019";

    /// <summary>Shared folder where uploaded certificates are written. Empty = AppPaths.DefaultCertificateStore.</summary>
    public string? CertificateStorePath { get; set; }

    /// <summary>Global raw JSON merged into apps.http.servers.* (advanced) — optional.</summary>
    public string? ServerOptionsJson { get; set; }
}

/// <summary>Caddy binary / plugin management. Owned by the Platform module.</summary>
public sealed class BinarySettings : ISettingsDocument
{
    /// <summary>Go package paths of desired non-standard plugins, e.g. github.com/mholt/caddy-l4.</summary>
    public List<string> Plugins { get; set; } = new();
    public bool AutoCheckUpdates { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 12;
    /// <summary>Install updates automatically (off by default — sysadmins decide).</summary>
    public bool AutoInstallUpdates { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public string? LatestKnownVersion { get; set; }
    /// <summary>Optional HTTP proxy for outbound downloads (e.g. http://proxy:8080).</summary>
    public string? OutboundProxy { get; set; }
}

public enum SmtpSecurity { None, StartTls, SslOnConnect, Auto }

/// <summary>Owned by the Ops module.</summary>
public sealed class NotificationSettings : ISettingsDocument
{
    public bool SmtpEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordProtected { get; set; }
    public string SmtpFrom { get; set; } = "";
    public List<string> Recipients { get; set; } = new();
    public bool AllowInvalidCertificate { get; set; }

    public bool WebhookEnabled { get; set; }
    /// <summary>Generic JSON webhook (Teams/Slack-compatible "text" payload).</summary>
    public string? WebhookUrl { get; set; }

    public bool WriteWindowsEventLog { get; set; } = true;

    // Alert rules
    public bool AlertCaddyDown { get; set; } = true;
    public bool AlertConfigFailure { get; set; } = true;
    public bool AlertUpstreamUnhealthy { get; set; } = true;
    public bool AlertCertificateExpiry { get; set; } = true;
    public int CertificateExpiryDays { get; set; } = 14;
    public bool AlertUpdateAvailable { get; set; } = true;
    public bool AlertReadinessFailure { get; set; } = true;
    public bool AutoRestartCaddy { get; set; } = true;
    public int CooldownMinutes { get; set; } = 30;
    public bool SendRecoveryNotices { get; set; } = true;
}

/// <summary>Management UI listener. Owned by the Ops module; applied by the host at startup.</summary>
public sealed class UiSettings : ISettingsDocument
{
    public int Port { get; set; } = 81;
    /// <summary>"0.0.0.0" = all interfaces, "127.0.0.1" = local only.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    public bool HttpsEnabled { get; set; }
    public int HttpsPort { get; set; } = 8443;
    /// <summary>Optional PFX for the UI; if empty and HTTPS enabled, a self-signed cert is generated.</summary>
    public string? HttpsPfxPath { get; set; }
    public string? HttpsPfxPasswordProtected { get; set; }
    public int SessionHours { get; set; } = 12;
    public string? DisplayName { get; set; }
}
