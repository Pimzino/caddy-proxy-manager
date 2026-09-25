using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Events;
using CaddyManager.Ops.Logs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops.Monitoring;

/// <summary>
/// Periodic health monitoring (SPEC "Monitoring &amp; alerts"): Caddy down + auto restart, upstream health,
/// certificate expiry (every 6h) and daily readiness. Config/Platform services are resolved lazily and
/// every probe is isolated so one failing dependency never stops the others.
/// </summary>
internal sealed class MonitorService(
    IServiceProvider services,
    IStore store,
    IEventSink events,
    IAlertState alerts,
    IAuditLog audit,
    TimeProvider time,
    IOptions<OpsOptions> options,
    ILogger<MonitorService> logger) : BackgroundService
{
    internal const string CaddyDownKey = "caddy-down";
    internal const string AutoRestartLimitKey = "caddy-autorestart-limit";

    private readonly OpsOptions _o = options.Value;
    private int _consecutiveCaddyFailures;
    private readonly List<DateTime> _restartAttempts = new();
    private DateTime _lastCertCheck = DateTime.MinValue;
    private DateTime _lastCertMissingCheck = DateTime.MinValue;
    private DateTime? _lastReadinessSeen;
    private bool _readinessRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_o.MonitorStartDelay, time, stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        logger.LogInformation("Monitoring started (interval {Interval}s)", _o.MonitorInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(_o.MonitorInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring pass failed unexpectedly");
            }
        }
    }

    /// <summary>One monitoring pass. Public for tests.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var settings = SafeSettings();
        CaddyStatus? status = null;
        await Guard("Caddy status", async () => status = await CheckCaddyAsync(settings, ct), ct);
        if (status is { AdminReachable: true })
            await Guard("upstream health", () => CheckUpstreamsAsync(ct), ct);
        await Guard("certificate expiry", () => CheckCertificatesAsync(settings, ct), ct);
        await Guard("missing certificates", () => CheckMissingCertificatesAsync(status, ct), ct);
        await Guard("readiness", () => CheckReadinessAsync(ct), ct);
    }

    private async Task Guard(string what, Func<Task> probe, CancellationToken ct)
    {
        try
        {
            await probe();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Monitor: {What} check failed", what);
        }
    }

    // ------------------------------------------------------------------ Caddy

    private async Task<CaddyStatus?> CheckCaddyAsync(NotificationSettings settings, CancellationToken ct)
    {
        var host = services.GetService<ICaddyHost>();
        if (host is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var status = await host.GetStatusAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(25), ct);

        var expected = status.BinaryInstalled && status.ServiceInstalled;
        var transitional = status.State is CaddyRunState.Starting or CaddyRunState.Stopping;
        var healthy = status.State == CaddyRunState.Running && status.AdminReachable;

        if (!expected || healthy)
        {
            _consecutiveCaddyFailures = 0;
            if (healthy && alerts.IsActive(CaddyDownKey))
            {
                events.Raise(EventSeverity.Recovered, "caddy", "Caddy is running again",
                    $"State: {status.State}, admin API reachable. Version: {status.Version ?? "unknown"}", CaddyDownKey, "caddyDown");
                _restartAttempts.Clear();
            }
            if (healthy && alerts.IsActive(AutoRestartLimitKey))
                events.Raise(EventSeverity.Recovered, "caddy", "Caddy is running again after automatic restarts gave up", null,
                    AutoRestartLimitKey, "caddyDown");
            if (!expected && alerts.IsActive(CaddyDownKey))
                events.Raise(EventSeverity.Recovered, "caddy", "Caddy is no longer expected to run (binary or service removed)",
                    null, CaddyDownKey, "caddyDown");
            return status;
        }

        if (transitional || BusyWithJob() || StoppedIntentionally())
        {
            _consecutiveCaddyFailures = 0;
            return status;
        }

        _consecutiveCaddyFailures++;
        if (_consecutiveCaddyFailures < Math.Max(1, _o.CaddyDownThreshold)) return status;

        var problem = status.State != CaddyRunState.Running
            ? $"Caddy is not running (state: {status.State})"
            : $"Caddy is running but its admin API is not reachable";
        var details = $"Host mode: {status.HostMode}\nBinary: {status.BinaryPath}\nConfig: {status.ConfigPath}" +
                      (string.IsNullOrWhiteSpace(status.LastError) ? "" : $"\nLast error: {status.LastError}");
        events.Raise(EventSeverity.Error, "caddy", problem, details, CaddyDownKey, "caddyDown");

        if (settings.AutoRestartCaddy)
            await TryAutoRestartAsync(host, ct);
        return status;
    }

    private async Task TryAutoRestartAsync(ICaddyHost host, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        _restartAttempts.RemoveAll(t => now - t > _o.AutoRestartWindow);
        if (_restartAttempts.Count >= _o.AutoRestartMaxAttempts)
        {
            events.Raise(EventSeverity.Error, "caddy",
                $"Automatic restart of Caddy gave up after {_o.AutoRestartMaxAttempts} attempts in {_o.AutoRestartWindow.TotalMinutes:0} minutes",
                "Check the Caddy log (Logs → Caddy) and the service state, then start Caddy manually.",
                AutoRestartLimitKey, "caddyDown");
            return;
        }
        _restartAttempts.Add(now);
        var attempt = _restartAttempts.Count;
        logger.LogWarning("Caddy is down; automatic restart attempt {Attempt}/{Max}", attempt, _o.AutoRestartMaxAttempts);
        audit.Record("autoRestart", "caddy", null, "Caddy", $"Attempt {attempt}/{_o.AutoRestartMaxAttempts}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await host.StartAsync(timeout.Token);
            events.Raise(EventSeverity.Info, "caddy", $"Automatic restart of Caddy attempted ({attempt}/{_o.AutoRestartMaxAttempts})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            events.Raise(EventSeverity.Error, "caddy", $"Automatic restart of Caddy failed ({attempt}/{_o.AutoRestartMaxAttempts})", ex.Message);
        }
    }

    /// <summary>A running job (binary install/update) stops and starts Caddy deliberately — do not interfere.</summary>
    private bool BusyWithJob()
    {
        try
        {
            return services.GetService<IJobRunner>()?.Recent(20).Any(j => j.State == JobState.Running) == true;
        }
        catch { return false; }
    }

    /// <summary>True when the most recent Caddy start/stop action in the audit log was a user "stop".</summary>
    private bool StoppedIntentionally()
    {
        try
        {
            var recent = store.Col<AuditEntry>().Query()
                .OrderByDescending(a => a.CreatedAt).Limit(200).ToList()
                .FirstOrDefault(a => a.ObjectType.StartsWith("caddy", StringComparison.OrdinalIgnoreCase) &&
                                     (a.Action.Contains("stop", StringComparison.OrdinalIgnoreCase) ||
                                      a.Action.Contains("start", StringComparison.OrdinalIgnoreCase)));
            return recent is not null &&
                   recent.Action.Contains("stop", StringComparison.OrdinalIgnoreCase) &&
                   !recent.Action.Contains("restart", StringComparison.OrdinalIgnoreCase) &&
                   recent.UserName != "system";
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ upstreams

    private async Task CheckUpstreamsAsync(CancellationToken ct)
    {
        var admin = services.GetService<ICaddyAdminClient>();
        if (admin is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var upstreams = await admin.GetUpstreamsAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(20), ct);

        var unhealthy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in upstreams.Where(u => !u.Healthy))
        {
            var key = "upstream:" + u.Address;
            unhealthy.Add(key);
            events.Raise(EventSeverity.Warning, "upstream", $"Upstream {u.Address} is unhealthy",
                $"Failed requests: {u.Fails}, active requests: {u.NumRequests}", key, "upstreamUnhealthy");
        }
        foreach (var key in alerts.ActiveKeys("upstream:").Where(k => !unhealthy.Contains(k)))
            events.Raise(EventSeverity.Recovered, "upstream", $"Upstream {key["upstream:".Length..]} is healthy again", null, key, "upstreamUnhealthy");
    }

    // ------------------------------------------------------------------ certificates

    private async Task CheckCertificatesAsync(NotificationSettings settings, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastCertCheck < _o.CertificateCheckInterval) return;
        var inventory = services.GetService<ICertificateInventory>();
        if (inventory is null) return;
        _lastCertCheck = now;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var certs = await inventory.ListAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(70), ct);
        var threshold = Math.Max(1, settings.CertificateExpiryDays);
        var problems = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in certs.Where(c => c.Kind is CertificateKind.Custom or CertificateKind.Acme))
        {
            var key = "cert-expiry:" + c.Id;
            var label = string.IsNullOrWhiteSpace(c.Name) ? string.Join(", ", c.Subjects) : c.Name;
            if (!string.IsNullOrEmpty(c.Error))
            {
                problems.Add(key);
                events.Raise(EventSeverity.Warning, "certificate", $"Certificate '{label}' cannot be read",
                    $"{c.Error}\nCertificate: {c.CertPath}\nKey: {c.KeyPath}", key, "certificateExpiry");
                continue;
            }
            if (c.DaysRemaining > threshold) continue;
            problems.Add(key);
            var expired = c.DaysRemaining < 0 || c.NotAfter <= now;
            var msg = expired
                ? $"Certificate '{label}' expired on {c.NotAfter:yyyy-MM-dd}"
                : $"Certificate '{label}' expires in {c.DaysRemaining} day(s) ({c.NotAfter:yyyy-MM-dd})";
            var details = $"Subjects: {string.Join(", ", c.Subjects)}\nIssuer: {c.Issuer}\nKind: {c.Kind}" +
                          (c.Kind == CertificateKind.Acme ? "\nCaddy normally renews ACME certificates ~30 days before expiry; check the Caddy log for ACME errors." : "\nReplace the certificate (Certificates → Replace).");
            events.Raise(expired ? EventSeverity.Error : EventSeverity.Warning, "certificate", msg, details, key, "certificateExpiry");
        }
        foreach (var key in alerts.ActiveKeys("cert-expiry:").Where(k => !problems.Contains(k)))
            events.Raise(EventSeverity.Recovered, "certificate", $"Certificate {key["cert-expiry:".Length..]} is no longer expiring", null, key, "certificateExpiry");
    }

    // ------------------------------------------------------------------ missing certificates

    internal const string CertMissingPrefix = "cert-missing:";

    /// <summary>
    /// Every enabled ACME/internal host (non-wildcard domains) should have a certificate from Caddy within
    /// OpsOptions.CertificateMissingGrace of its last change. Skipped while Caddy is not running (nothing can be issued).
    /// </summary>
    private async Task CheckMissingCertificatesAsync(CaddyStatus? status, CancellationToken ct)
    {
        if (status is not { State: CaddyRunState.Running, AdminReachable: true }) return;
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastCertMissingCheck < _o.CertificateMissingInterval) return;
        var inventory = services.GetService<ICertificateInventory>();
        if (inventory is null) return;
        _lastCertMissingCheck = now;

        var expected = new Dictionary<string, SiteHost>(StringComparer.OrdinalIgnoreCase);
        var managed = true;
        try { managed = store.GetSettings<CaddySettings>().Mode == ConfigMode.Managed; }
        catch { /* assume managed */ }
        if (managed)
            foreach (var h in store.Col<SiteHost>().Find(h => h.Enabled))
            {
                if (h.Tls is not (TlsMode.Acme or TlsMode.Internal)) continue;
                foreach (var d in h.Domains.Select(d => d.Trim().TrimEnd('.').ToLowerInvariant()).Where(d => d.Length > 0 && !d.StartsWith('*')))
                    expected.TryAdd(d, h);
            }

        List<CertificateInfo> certs = [];
        if (expected.Count > 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            certs = await inventory.ListAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(70), ct);
        }

        var stillOpen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (domain, host) in expected)
        {
            var key = CertMissingPrefix + domain;
            if (IsCovered(domain, certs, now)) continue;
            if (now - host.UpdatedAt < _o.CertificateMissingGrace)
            {
                // Recently changed: give Caddy time to obtain it (and do not declare an open alert recovered).
                if (alerts.IsActive(key)) stillOpen.Add(key);
                continue;
            }
            stillOpen.Add(key);
            var minutes = (int)(now - host.UpdatedAt).TotalMinutes;
            var hint = host.Tls == TlsMode.Acme
                ? "Caddy has not obtained an ACME certificate yet. Check that the domain's public DNS points at this server and that TCP 80 and 443 " +
                  "reach it from the internet (HTTP-01 / TLS-ALPN-01 challenges), or configure a DNS challenge; see Logs → Caddy."
                : "Caddy's internal CA has not issued a certificate for this name; see Logs → Caddy.";
            var errors = LastIssuanceErrors(domain);
            events.Raise(EventSeverity.Warning, "certificate", $"No certificate has been issued for {domain}",
                $"Host: {string.Join(", ", host.Domains)} ({host.Kind}, TLS {host.Tls}), last changed {minutes} minute(s) ago.\n{hint}" +
                (errors.Count > 0 ? "\n\nLast certificate errors from caddy.log:\n" + string.Join("\n", errors) : ""),
                key, "certificateExpiry");
        }

        foreach (var key in alerts.ActiveKeys(CertMissingPrefix).Where(k => !stillOpen.Contains(k)))
        {
            var domain = key[CertMissingPrefix.Length..];
            var message = expected.ContainsKey(domain)
                ? $"The certificate for {domain} has been issued"
                : $"{domain} no longer needs a certificate from Caddy (host removed, disabled or changed)";
            events.Raise(EventSeverity.Recovered, "certificate", message, null, key, "certificateExpiry");
        }
    }

    /// <summary>A current (not expired, readable) Caddy-managed certificate whose subjects include the domain or a matching wildcard.</summary>
    internal static bool IsCovered(string domain, IEnumerable<CertificateInfo> certs, DateTime nowUtc) =>
        certs.Any(c => c.Kind is CertificateKind.Acme or CertificateKind.Internal && string.IsNullOrEmpty(c.Error) && c.NotAfter > nowUtc &&
                       c.Subjects.Any(s => Matches(s.Trim().TrimEnd('.'), domain)));

    private static bool Matches(string subject, string domain)
    {
        if (string.Equals(subject, domain, StringComparison.OrdinalIgnoreCase)) return true;
        if (!subject.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = subject[1..]; // ".example.com"
        return domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && domain.Length > suffix.Length &&
               !domain[..^suffix.Length].Contains('.');
    }

    /// <summary>Last tls.obtain / tls.issuance warnings and errors in caddy.log that mention the domain.</summary>
    private List<string> LastIssuanceErrors(string domain)
    {
        try
        {
            var paths = services.GetService<AppPaths>();
            if (paths is null) return [];
            return LogTail.Read(paths.CaddyProcessLog, 400, domain, maxScanBytes: 16L * 1024 * 1024)
                .Where(l => (l.Contains("\"tls.obtain\"", StringComparison.Ordinal) || l.Contains("\"tls.issuance", StringComparison.Ordinal)) &&
                            (l.Contains("\"level\":\"error\"", StringComparison.Ordinal) || l.Contains("\"level\":\"warn\"", StringComparison.Ordinal)))
                .TakeLast(3)
                .Select(l => l.Length > 800 ? l[..800] + "…" : l)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ readiness

    private async Task CheckReadinessAsync(CancellationToken ct)
    {
        var readiness = services.GetService<IReadinessService>();
        if (readiness is null || _readinessRunning) return;
        var now = time.GetUtcNow().UtcDateTime;
        var report = readiness.LastReport;
        if (report is null || now - report.RanAt >= _o.ReadinessInterval)
        {
            _readinessRunning = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                report = await readiness.RunAsync(timeout.Token).WaitAsync(TimeSpan.FromMinutes(6), ct);
            }
            finally
            {
                _readinessRunning = false;
            }
        }
        if (report is null || report.RanAt == _lastReadinessSeen) return;
        _lastReadinessSeen = report.RanAt;
        ProcessReadiness(report);
    }

    private void ProcessReadiness(ReadinessReport report)
    {
        var failing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in report.Checks.Where(c => c.Status == CheckStatus.Fail))
        {
            var key = "readiness:" + c.Id;
            failing.Add(key);
            if (alerts.IsActive(key)) continue; // only new failures
            events.Raise(EventSeverity.Warning, "readiness", $"Readiness check failed: {c.Title}",
                $"{c.Summary}{(string.IsNullOrWhiteSpace(c.Details) ? "" : "\n" + c.Details)}{(string.IsNullOrWhiteSpace(c.Remediation) ? "" : "\nRemediation: " + c.Remediation)}",
                key, "readinessFailure");
        }
        foreach (var key in alerts.ActiveKeys("readiness:").Where(k => !failing.Contains(k)))
        {
            var id = key["readiness:".Length..];
            var title = report.Checks.FirstOrDefault(c => c.Id == id)?.Title ?? id;
            events.Raise(EventSeverity.Recovered, "readiness", $"Readiness check passes again: {title}", null, key, "readinessFailure");
        }
    }

    private NotificationSettings SafeSettings()
    {
        try { return store.GetSettings<NotificationSettings>(); }
        catch { return new NotificationSettings(); }
    }
}

/// <summary>Daily clean-up: events older than 90 days, audit entries older than 365 days.</summary>
internal sealed class RetentionService(IStore store, TimeProvider time, IOptions<OpsOptions> options, ILogger<RetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        try { await Task.Delay(o.MonitorStartDelay, time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            RunOnce();
            try { await Task.Delay(o.RetentionInterval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal (int Events, int Audit) RunOnce()
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            var eventCutoff = now.AddDays(-Math.Max(1, o.EventRetentionDays));
            var auditCutoff = now.AddDays(-Math.Max(1, o.AuditRetentionDays));
            var ev = store.Col<EventEntry>().DeleteMany(e => e.CreatedAt < eventCutoff);
            var au = store.Col<AuditEntry>().DeleteMany(a => a.CreatedAt < auditCutoff);
            if (ev + au > 0)
                logger.LogInformation("Retention: removed {Events} event(s) older than {EventDays} days and {Audit} audit entr(ies) older than {AuditDays} days",
                    ev, o.EventRetentionDays, au, o.AuditRetentionDays);
            return (ev, au);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Retention clean-up failed");
            return (0, 0);
        }
    }
}
