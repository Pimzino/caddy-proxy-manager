using CaddyManager.Core;
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

    /// <summary>XDG_DATA_HOME / XDG_CONFIG_HOME used by the Caddy service and child process.</summary>
    public static Dictionary<string, string> CaddyEnvironment(AppPaths paths) => new()
    {
        ["XDG_DATA_HOME"] = paths.CaddyStorageDir,
        ["XDG_CONFIG_HOME"] = Path.Combine(paths.DataDir, "caddy", "config"),
    };

    public void EnsureDirectories()
    {
        foreach (var d in CaddyEnvironment(paths).Values.Append(paths.CaddyLogDir)) Directory.CreateDirectory(d);
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
