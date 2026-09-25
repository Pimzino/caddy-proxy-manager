using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Hosting;

/// <summary>
/// Shared helpers for the ICaddyHost implementations. Cross-module services (admin client, config service,
/// binary manager) are resolved lazily from DI to avoid construction cycles and to tolerate their absence in tests.
/// </summary>
public sealed class CaddyHostSupport(AppPaths paths, IServiceProvider services, ILogger<CaddyHostSupport> logger)
{
    public AppPaths Paths => paths;

    // ---- binary swap guard: while an install/update job replaces caddy(.exe), only that job may start Caddy
    // (e.g. the monitor's auto-restart must not start the old binary half-way through the swap).
    private int _swapInProgress;
    private static readonly AsyncLocal<bool> IsSwapOwner = new();

    /// <summary>Marks the start of a binary swap for the current async flow. Dispose to end it.</summary>
    public IDisposable BeginBinarySwap()
    {
        Interlocked.Exchange(ref _swapInProgress, 1);
        IsSwapOwner.Value = true;
        return new SwapScope(this);
    }

    /// <summary>True while an install/update job replaces the Caddy binary.</summary>
    public bool IsBinarySwapInProgress => Volatile.Read(ref _swapInProgress) == 1;

    /// <summary>Throws when another flow is replacing the Caddy binary right now.</summary>
    public void ThrowIfBinarySwapInProgress()
    {
        if (Volatile.Read(ref _swapInProgress) == 1 && !IsSwapOwner.Value)
            throw new InvalidOperationException("The Caddy binary is being updated right now; Caddy will be started by the update job. Try again in a minute.");
    }

    private sealed class SwapScope(CaddyHostSupport owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            IsSwapOwner.Value = false;
            Interlocked.Exchange(ref owner._swapInProgress, 0);
        }
    }

    public ICaddyAdminClient? Admin => services.GetService<ICaddyAdminClient>();
    public ICaddyConfigService? ConfigService => services.GetService<ICaddyConfigService>();
    public ICaddyBinaryManager? Binary => services.GetService<ICaddyBinaryManager>();
    /// <summary>Current binary settings (null when no store is registered, e.g. in some tests).</summary>
    public BinarySettings? BinarySettings => services.GetService<IStore>()?.GetSettings<BinarySettings>();

    /// <summary>
    /// Environment of the Caddy service / child process: XDG_DATA_HOME and XDG_CONFIG_HOME, plus HTTPS_PROXY, HTTP_PROXY and
    /// NO_PROXY when BinarySettings.ProxyCaddyTraffic is on and an outbound proxy is configured (ACME behind a corporate
    /// proxy). Only upper-case names: Windows environment names are case-insensitive, and Go reads both spellings.
    /// </summary>
    public static Dictionary<string, string> CaddyEnvironment(AppPaths paths, BinarySettings? binary = null)
    {
        var env = new Dictionary<string, string>
        {
            ["XDG_DATA_HOME"] = paths.CaddyStorageDir,
            ["XDG_CONFIG_HOME"] = CaddyConfigHome(paths),
        };
        if (CaddyProxy(binary) is { } proxy)
        {
            env["HTTPS_PROXY"] = proxy;
            env["HTTP_PROXY"] = proxy;
            env["NO_PROXY"] = NormalizeNoProxy(binary!.NoProxy);
        }
        return env;
    }

    /// <summary>Names of the proxy variables the manager controls in Caddy's environment.</summary>
    public static readonly string[] ProxyVariables = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"];

    /// <summary>The proxy URL Caddy should use, or null when Caddy connects directly.</summary>
    public static string? CaddyProxy(BinarySettings? binary) =>
        binary is { ProxyCaddyTraffic: true } && !string.IsNullOrWhiteSpace(binary.OutboundProxy) ? binary.OutboundProxy.Trim() : null;

    /// <summary>Normalises a NO_PROXY list: entries separated by commas, semicolons or whitespace → "a,b,c" (duplicates removed).</summary>
    public static string NormalizeNoProxy(string? noProxy) =>
        string.Join(",", SplitNoProxy(noProxy).Distinct(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitNoProxy(string? noProxy) =>
        (noProxy ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Validates NO_PROXY entries as Go's net/http understands them: host names (optionally with a leading "." or "*."),
    /// IP addresses, CIDR ranges, an optional ":port", or "*". Returns one message per invalid entry.
    /// </summary>
    public static List<string> ValidateNoProxy(string? noProxy)
    {
        var errors = new List<string>();
        foreach (var entry in SplitNoProxy(noProxy))
        {
            if (entry == "*") continue;
            var e = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[1..] : entry;
            if (System.Net.IPNetwork.TryParse(e, out _) || System.Net.IPAddress.TryParse(e.Trim('[', ']'), out _)) continue;
            var host = e;
            var colon = e.LastIndexOf(':');
            if (colon > 0 && e.IndexOf(':') == colon)
            {
                if (!int.TryParse(e[(colon + 1)..], out var port) || port is < 1 or > 65535)
                {
                    errors.Add($"'{entry}' has an invalid port.");
                    continue;
                }
                host = e[..colon];
                if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) continue;
            }
            var name = host.TrimStart('.');
            if (name.Length == 0 || name.Length > 253 || !name.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                errors.Add($"'{entry}' is not a host name, domain suffix (.corp.local), IP address or CIDR range (10.0.0.0/8).");
        }
        return errors;
    }

    /// <summary>The environment for Caddy with the current settings.</summary>
    public Dictionary<string, string> CaddyEnvironment() => CaddyEnvironment(paths, BinarySettings);

    private static string CaddyConfigHome(AppPaths paths) => Path.Combine(paths.DataDir, "caddy", "config");

    public void EnsureDirectories()
    {
        foreach (var d in new[] { paths.CaddyStorageDir, CaddyConfigHome(paths), paths.CaddyLogDir }) Directory.CreateDirectory(d);
    }

    /// <summary>Makes sure the boot config exists (the Config module writes a minimal config when missing).</summary>
    public void EnsureBootConfig()
    {
        if (File.Exists(paths.CaddyConfigFile)) return;
        var cfg = ConfigService;
        if (cfg is null) return;
        try
        {
            cfg.EnsureBootConfig();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the Caddy boot config {File}", paths.CaddyConfigFile);
        }
    }

    public async Task<bool> IsAdminReachableAsync(CancellationToken ct)
    {
        var admin = Admin;
        if (admin is null) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await admin.IsReachableAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Caddy admin API reachability check failed");
            return false;
        }
    }

    /// <summary>Waits until the admin API answers. Returns false on timeout or when <paramref name="stillAlive"/> reports the process died.</summary>
    public async Task<bool> WaitForAdminAsync(TimeSpan timeout, Func<bool> stillAlive, CancellationToken ct)
    {
        if (Admin is null) return stillAlive();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!stillAlive()) return false;
            if (await IsAdminReachableAsync(ct)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        var bin = Binary;
        if (bin is null || !File.Exists(paths.CaddyExe)) return null;
        try
        {
            return (await bin.GetInstalledAsync(ct))?.Version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not determine the Caddy version");
            return null;
        }
    }

    /// <summary>Last lines of the Caddy process log, for error messages.</summary>
    public string TailLog(int lines = 15)
    {
        try
        {
            if (!File.Exists(paths.CaddyProcessLog)) return "";
            using var fs = new FileStream(paths.CaddyProcessLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            fs.Seek(Math.Max(0, len - 16 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('\n', all.TakeLast(lines)).Trim();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
