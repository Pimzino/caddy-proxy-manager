using System.Diagnostics;
using System.Text;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Hosting;

/// <summary>
/// Development / non-Windows host: runs "caddy run --config &lt;caddy.json&gt;" as a child process of the manager.
/// stdout/stderr are appended to AppPaths.CaddyProcessLog. The child is stopped when the manager shuts down.
/// </summary>
public sealed class ProcessCaddyHost : ICaddyHost, IDisposable
{
    public const string Mode = "process";

    private readonly AppPaths _paths;
    private readonly CaddyHostSupport _support;
    private readonly ILogger<ProcessCaddyHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _logLock = new();
    private readonly LinkedList<string> _recentOutput = new();
    private Process? _process;
    private DateTime? _startedAt;
    private string? _lastError;
    private Dictionary<string, string>? _startedEnvironment;
    private bool _stopRequested;
    private bool _disposed;

    public ProcessCaddyHost(AppPaths paths, CaddyHostSupport support, ILogger<ProcessCaddyHost> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _paths = paths;
        _support = support;
        _logger = logger;
        lifetime?.ApplicationStopping.Register(StopOnShutdown);
    }

    public string HostMode => Mode;

    /// <summary>
    /// True when the running child was started with a different environment than the current settings produce
    /// (e.g. the outbound proxy for Caddy changed), so it must be restarted to pick the change up.
    /// </summary>
    public bool EnvironmentOutdated =>
        ChildRunning && _startedEnvironment is { } started &&
        !started.OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(_support.CaddyEnvironment().OrderBy(kv => kv.Key, StringComparer.Ordinal));

    private bool ChildRunning
    {
        get
        {
            var p = _process;
            if (p is null) return false;
            try { return !p.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var binary = File.Exists(_paths.CaddyExe);
        var child = ChildRunning;
        var admin = await _support.IsAdminReachableAsync(ct);
        var state = !binary ? CaddyRunState.NotInstalled
            : child ? (admin || _support.Admin is null ? CaddyRunState.Running : CaddyRunState.Starting)
            : admin ? CaddyRunState.Running // an instance not started by this manager (e.g. left over from a previous run)
            : CaddyRunState.Stopped;
        var lastError = _lastError;
        if (!child && admin)
            lastError = "Caddy is running but was not started by this manager (its admin API answers). Stop it to let the manager take over.";
        return new CaddyStatus
        {
            BinaryInstalled = binary,
            ServiceInstalled = binary,
            State = state,
            ProcessId = child ? SafePid() : null,
            Version = binary ? await _support.GetVersionAsync(ct) : null,
            AdminReachable = admin,
            StartedAt = child ? _startedAt : null,
            BinaryPath = _paths.CaddyExe,
            ConfigPath = _paths.CaddyConfigFile,
            HostMode = Mode,
            ServiceStartType = null,
            LastError = lastError,
        };
    }

    public Task InstallServiceAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Process host mode: no Windows service to install; Caddy runs as a child process.");
        return Task.CompletedTask;
    }

    public Task UninstallServiceAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Process host mode: no Windows service to uninstall.");
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _support.ThrowIfBinarySwapInProgress();
        await _gate.WaitAsync(ct);
        try
        {
            if (ChildRunning) return;
            if (await _support.IsAdminReachableAsync(ct))
            {
                _logger.LogWarning("Caddy admin API is already reachable; assuming an externally started Caddy instance is running.");
                return;
            }
            if (!File.Exists(_paths.CaddyExe))
                throw new InvalidOperationException($"The Caddy binary is not installed ({_paths.CaddyExe}). Install it from the Caddy page first.");
            _support.EnsureDirectories();
            _support.EnsureBootConfig();
            if (!File.Exists(_paths.CaddyConfigFile))
                throw new InvalidOperationException($"The Caddy config file {_paths.CaddyConfigFile} does not exist.");

            var psi = new ProcessStartInfo(_paths.CaddyExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WorkingDirectory = Path.GetDirectoryName(_paths.CaddyExe)!,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(_paths.CaddyConfigFile);
            var environment = _support.CaddyEnvironment();
            foreach (var (k, v) in environment) psi.Environment[k] = v;

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnOutput(e.Data);
            p.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            p.Exited += (_, _) => OnExited(p);
            lock (_recentOutput) _recentOutput.Clear();
            _stopRequested = false;
            _lastError = null;
            AppendLog($"---- {DateTime.UtcNow:O} Caddy Proxy Manager starting: {_paths.CaddyExe} run --config {_paths.CaddyConfigFile}");
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                p.Dispose();
                _lastError = $"Could not start {_paths.CaddyExe}: {ex.Message}";
                throw new InvalidOperationException(_lastError, ex);
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
            _startedAt = DateTime.UtcNow;
            _startedEnvironment = environment;
            _logger.LogInformation("Started Caddy child process (PID {Pid})", p.Id);

            var ok = await _support.WaitForAdminAsync(TimeSpan.FromSeconds(20), () => ChildRunning, ct);
            if (!ok)
            {
                var exited = !ChildRunning;
                if (exited)
                {
                    try { p.WaitForExit(); } catch (InvalidOperationException) { } // drain buffered output
                }
                var message = exited
                    ? $"Caddy exited during startup (exit code {SafeExitCode(p)}). {RecentOutput()}".Trim()
                    : $"Caddy did not open its admin API within 20s and was stopped. {RecentOutput()}".Trim();
                if (exited)
                {
                    _stopRequested = true;
                    _process = null;
                    _startedAt = null;
                    p.Dispose();
                }
                else
                {
                    await StopChildAsync(TimeSpan.FromSeconds(5));
                }
                _lastError = message;
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (ChildRunning)
            {
                await StopChildAsync(TimeSpan.FromSeconds(10));
                return;
            }
            // Not our child: try to stop an externally started instance through its admin API.
            if (_support.Admin is { } admin && await _support.IsAdminReachableAsync(ct))
            {
                _logger.LogInformation("Stopping externally started Caddy via its admin API");
                await admin.StopAsync(ct);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && await _support.IsAdminReachableAsync(ct)) await Task.Delay(250, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    /// <summary>Graceful stop via admin POST /stop, then kill after the grace period.</summary>
    private async Task StopChildAsync(TimeSpan grace)
    {
        var p = _process;
        if (p is null) return;
        _stopRequested = true;
        try
        {
            if (!p.HasExited && _support.Admin is { } admin)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await admin.StopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Admin API /stop failed; will terminate the process");
                }
            }
            using (var wait = new CancellationTokenSource(grace))
            {
                try
                {
                    await p.WaitForExitAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Caddy (PID {Pid}) did not exit within {Grace}s; terminating it", SafePid(), grace.TotalSeconds);
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    p.WaitForExit(5000);
                }
            }
            _logger.LogInformation("Caddy child process stopped");
        }
        catch (InvalidOperationException)
        {
            // process object no longer associated — already gone
        }
        finally
        {
            _process = null;
            _startedAt = null;
            p.Dispose();
        }
    }

    private void StopOnShutdown()
    {
        if (!ChildRunning) return;
        _logger.LogInformation("Manager is shutting down; stopping the Caddy child process");
        try
        {
            StopChildAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop the Caddy child process cleanly");
        }
    }

    private void OnOutput(string? line)
    {
        if (line is null) return;
        lock (_recentOutput)
        {
            _recentOutput.AddLast(line);
            while (_recentOutput.Count > 40) _recentOutput.RemoveFirst();
        }
        AppendLog(line);
    }

    private void OnExited(Process p)
    {
        if (_stopRequested || !ReferenceEquals(p, _process)) return;
        var code = SafeExitCode(p);
        _lastError = $"Caddy exited unexpectedly with code {code}. {RecentOutput()}".Trim();
        _logger.LogError("Caddy child process exited unexpectedly with code {Code}", code);
    }

    /// <summary>
    /// Appends a line to the Caddy process log. The file is opened per write with shared access because the
    /// generated config also lets Caddy write (and roll) the same file.
    /// </summary>
    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(_paths.CaddyLogDir);
                using var fs = new FileStream(_paths.CaddyProcessLog, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                fs.Write(bytes);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not write Caddy output to {File}", _paths.CaddyProcessLog);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Could not write Caddy output to {File}", _paths.CaddyProcessLog);
            }
        }
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            var tail = _recentOutput.TakeLast(8).ToList();
            // Caddy's output can quote credentials; this text becomes the status error every viewer sees.
            return tail.Count == 0 ? "" : _support.Scrub("Last output: " + string.Join(" | ", tail));
        }
    }

    private int? SafePid()
    {
        try { return _process?.Id; }
        catch (InvalidOperationException) { return null; }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch (InvalidOperationException) { return "?"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                _stopRequested = true;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        p.Dispose();
        _process = null;
    }
}
