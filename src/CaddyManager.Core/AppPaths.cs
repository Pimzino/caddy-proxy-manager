namespace CaddyManager.Core;

/// <summary>
/// Well-known filesystem locations. On Windows everything mutable lives under
/// %ProgramData%\CaddyProxyManager so the service can run as LocalSystem with no
/// user profile. On other platforms (development) it lives under CM_DATA_DIR or ./.devdata.
/// </summary>
public sealed class AppPaths
{
    public const string ProductName = "Caddy Proxy Manager";
    public const string ManagerServiceName = "CaddyProxyManager";
    public const string CaddyServiceName = "Caddy";

    /// <summary>HKLM key shared with the MSI (which stores UiPort/UiBind there).</summary>
    public const string RegistryKey = @"SOFTWARE\Caddy Proxy Manager";
    /// <summary>
    /// DWORD 1 once first-run setup is complete. Written by the manager (not MSI-owned, so it survives an uninstall that
    /// keeps the data folder); the MSI reads it to choose its finish-page text. Removed with the data by uninstall --purge.
    /// </summary>
    public const string SetupCompletedValue = "SetupCompleted";
    /// <summary>
    /// REG_SZ with the URL of the management UI as reachable on this server (written by the manager at every start),
    /// for the tray companion, which runs as the signed-in user and cannot read the settings database.
    /// </summary>
    public const string UiUrlValue = "UiUrl";
    /// <summary>The notification-area companion (per user, started at sign-in), installed next to CaddyManager.exe.</summary>
    public const string TrayExeName = "CaddyManagerTray.exe";
    /// <summary>HKLM ...\CurrentVersion\Run value that starts the tray companion at sign-in.</summary>
    public const string TrayRunValue = "Caddy Proxy Manager";
    /// <summary>
    /// Local named pipe on which the manager accepts Caddy start/stop/restart from elevated administrators
    /// (CaddyManager.exe caddy ..., used by the tray), so those go through the manager: audited, and respected by the
    /// Caddy watchdog. Its DACL admits only SYSTEM and Administrators, and never network clients.
    /// </summary>
    public const string LocalControlPipe = "CaddyProxyManager.Control";

    /// <summary>Directory containing the manager executable (e.g. C:\Program Files\Caddy Proxy Manager).</summary>
    public string InstallDir { get; }
    /// <summary>Root for all mutable data (e.g. C:\ProgramData\CaddyProxyManager).</summary>
    public string DataDir { get; }

    public string DbFile => Path.Combine(DataDir, "db", "manager.db");
    public string SecretsKeyFile => Path.Combine(DataDir, "db", "secret.key");
    public string SetupTokenFile => Path.Combine(DataDir, "setup-token.txt");

    /// <summary>Caddy binary directory. caddy.exe (Windows) / caddy (dev).</summary>
    public string CaddyBinDir => Path.Combine(DataDir, "caddy", "bin");
    public string CaddyExe => Path.Combine(CaddyBinDir, OperatingSystem.IsWindows() ? "caddy.exe" : "caddy");
    /// <summary>Previous binary kept for rollback after an update.</summary>
    public string CaddyExeBackup => CaddyExe + ".previous";
    public string CaddyStagingDir => Path.Combine(DataDir, "caddy", "staging");
    /// <summary>The JSON config file Caddy boots from (last successfully applied config).</summary>
    public string CaddyConfigFile => Path.Combine(DataDir, "caddy", "caddy.json");
    /// <summary>Caddy storage root (ACME certs, internal CA, locks). Set via config "storage".</summary>
    public string CaddyStorageDir => Path.Combine(DataDir, "caddy", "data");
    public string CaddyLogDir => Path.Combine(DataDir, "logs", "caddy");
    public string CaddyProcessLog => Path.Combine(CaddyLogDir, "caddy.log");
    public string AccessLogDir => Path.Combine(DataDir, "logs", "access");
    public string ManagerLogDir => Path.Combine(DataDir, "logs", "manager");
    /// <summary>Compact access log of every HTTP request, tailed by the Telemetry module for traffic statistics.</summary>
    public string StatsLogDir => Path.Combine(DataDir, "logs", "stats");
    public string StatsLogFile => Path.Combine(StatsLogDir, "requests.log");
    public string BackupDir => Path.Combine(DataDir, "backups");

    /// <summary>Default shared certificate store. Can be overridden in CaddySettings.CertificateStorePath.</summary>
    public string DefaultCertificateStore => Path.Combine(DataDir, "certificates");

    public AppPaths(string? dataDirOverride = null)
    {
        InstallDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var env = Environment.GetEnvironmentVariable("CM_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDirOverride)) DataDir = Path.GetFullPath(dataDirOverride);
        else if (!string.IsNullOrWhiteSpace(env)) DataDir = Path.GetFullPath(env);
        else if (OperatingSystem.IsWindows())
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CaddyProxyManager");
        else
            DataDir = Path.Combine(Directory.GetCurrentDirectory(), ".devdata");
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { DataDir, Path.GetDirectoryName(DbFile)!, CaddyBinDir, CaddyStagingDir, CaddyStorageDir,
                     CaddyLogDir, AccessLogDir, ManagerLogDir, StatsLogDir, BackupDir, DefaultCertificateStore })
            Directory.CreateDirectory(d);
    }
}
