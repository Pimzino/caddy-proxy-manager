using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops.Backup;

public sealed record BackupFileInfo(string Name, long Size, DateTime CreatedAt);

/// <summary>A scheduled/manual backup run failed; the message is administrator-readable.</summary>
internal sealed class BackupRunException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Writes backup archives (the same content as GET /api/backup, optionally AES-256 encrypted) to the configured
/// directory — local or UNC — as caddy-proxy-manager-&lt;machine&gt;-&lt;yyyyMMdd-HHmmss&gt;.zip, then keeps only the newest
/// <see cref="BackupSettings.Keep"/> archives of this server. Failures of scheduled runs raise an Error event
/// (category "backup", key "backup-failed", alertRule "backupFailure"); the next successful run recovers it.
/// </summary>
internal sealed partial class ScheduledBackups(
    BackupService backups,
    IStore store,
    AppPaths paths,
    ISecretProtector secrets,
    IEventSink events,
    IAuditLog audit,
    TimeProvider time,
    ILogger<ScheduledBackups> logger)
{
    public const string FailureKey = "backup-failed";
    public const string AlertRule = "backupFailure";
    private const string Partial = ".partial";

    private readonly SemaphoreSlim _run = new(1, 1);

    /// <summary>File name prefix of this server's archives (other servers may share a UNC directory).</summary>
    public static string MachinePrefix => $"caddy-proxy-manager-{BackupService.SafeName(Environment.MachineName)}-";

    [GeneratedRegex(@"^caddy-proxy-manager-[A-Za-z0-9_-]+-\d{8}-\d{6}(-\d{1,3})?\.zip$")]
    internal static partial Regex ArchiveName();

    public string EffectiveDirectory(BackupSettings s) => string.IsNullOrWhiteSpace(s.Directory) ? paths.BackupDir : s.Directory.Trim();

    /// <summary>Runs one backup now. Returns the archive name; throws <see cref="BackupRunException"/> on failure.</summary>
    public async Task<string> RunAsync(bool scheduled, CancellationToken ct)
    {
        await _run.WaitAsync(ct);
        try
        {
            var s = store.GetSettings<BackupSettings>();
            var dir = EffectiveDirectory(s);
            var now = time.GetUtcNow().UtcDateTime;
            if (scheduled) UpdateStatus(st => st.LastScheduledRunAt = now);
            try
            {
                var (name, pruned) = await WriteAsync(s, dir, ct);
                UpdateStatus(st =>
                {
                    st.LastRunAt = now;
                    st.LastSucceeded = true;
                    st.LastBackupName = name;
                    st.LastError = null;
                });
                events.Raise(EventSeverity.Recovered, "backup", "Backups are written successfully again", $"Latest: {Path.Combine(dir, name)}",
                    FailureKey, AlertRule);
                audit.Record(scheduled ? "scheduledBackup" : "backup", "system", null, name,
                    $"Written to {dir}{(string.IsNullOrEmpty(s.PasswordProtected) ? "" : " (AES-256 encrypted)")}" +
                    (pruned > 0 ? $"; {pruned} old backup(s) removed (keep {s.Keep})" : ""));
                logger.LogInformation("Backup {Name} written to {Dir}; {Pruned} old backup(s) removed", name, dir, pruned);
                return name;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = Describe(ex, dir);
                UpdateStatus(st =>
                {
                    st.LastRunAt = now;
                    st.LastSucceeded = false;
                    st.LastError = message;
                });
                logger.LogError(ex, "{Kind} backup to {Dir} failed", scheduled ? "Scheduled" : "Manual", dir);
                audit.Record("backupFailed", "system", null, dir, message);
                if (scheduled)
                    events.Raise(EventSeverity.Error, "backup", "Scheduled backup failed", message, FailureKey, AlertRule);
                throw new BackupRunException(message, ex);
            }
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task<(string Name, int Pruned)> WriteAsync(BackupSettings s, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var password = string.IsNullOrEmpty(s.PasswordProtected) ? null : secrets.Unprotect(s.PasswordProtected);
        var (stream, name) = await backups.CreateAsync(ct);
        string final;
        await using (stream)
        {
            name = UniqueName(dir, name);
            final = Path.Combine(dir, name);
            var partial = final + Partial;
            try
            {
                await using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    if (password is null) await stream.CopyToAsync(fs, ct);
                    else BackupEncryption.Encrypt(stream, fs, password, ct);
                    await fs.FlushAsync(ct);
                }
                File.Move(partial, final);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
        RestrictIfLocal(final, dir);
        return (name, Prune(dir, s.Keep));
    }

    private static string UniqueName(string dir, string name)
    {
        if (!File.Exists(Path.Combine(dir, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{stem}-{i}.zip";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
        throw new IOException($"Too many backups named {stem}*.zip in {dir}.");
    }

    /// <summary>Deletes this server's archives beyond the newest <paramref name="keep"/> (and stale partial files). Never touches other files.</summary>
    internal int Prune(string dir, int keep)
    {
        var prefix = MachinePrefix;
        var removed = 0;
        var mine = new DirectoryInfo(dir).EnumerateFiles("caddy-proxy-manager-*")
            .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var f in mine.Where(f => f.Name.EndsWith(".zip" + Partial, StringComparison.OrdinalIgnoreCase) &&
                                          f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
            if (TryDelete(f.FullName)) removed++;
        foreach (var f in mine.Where(f => ArchiveName().IsMatch(f.Name))
                     .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                     .Skip(Math.Max(1, keep)))
        {
            if (TryDelete(f.FullName)) removed++;
            else logger.LogWarning("Could not delete old backup {File}", f.FullName);
        }
        return removed;
    }

    /// <summary>Archives in the backup directory (all servers' archives when the directory is shared), newest first.</summary>
    public List<BackupFileInfo> List(BackupSettings s)
    {
        var dir = EffectiveDirectory(s);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles("caddy-proxy-manager-*.zip")
            .Where(f => ArchiveName().IsMatch(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFileInfo(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    /// <summary>Full path of an archive by name, or null when the name is not a backup archive name or does not exist.</summary>
    public string? Resolve(BackupSettings s, string name)
    {
        if (string.IsNullOrEmpty(name) || !ArchiveName().IsMatch(name)) return null;
        var dir = Path.GetFullPath(EffectiveDirectory(s));
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!string.Equals(Path.GetDirectoryName(full), dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return File.Exists(full) ? full : null;
    }

    // ------------------------------------------------------------------ schedule

    /// <summary>
    /// The scheduled instant (UTC) of today's run when it is due now: enabled, today's HourLocal has passed and no scheduled
    /// run started since then. A run missed while the service was stopped is made up later the same day.
    /// </summary>
    internal static DateTime? Due(BackupSettings s, BackupStatus st, DateTimeOffset utcNow, TimeZoneInfo tz)
    {
        if (!s.Enabled) return null;
        var scheduled = ScheduledUtc(utcNow.UtcDateTime, s.HourLocal, tz, dayOffset: 0);
        if (utcNow.UtcDateTime < scheduled) return null;
        return st.LastScheduledRunAt is { } last && last >= scheduled ? null : scheduled;
    }

    /// <summary>Next scheduled run (UTC) for display; null when disabled.</summary>
    internal static DateTime? Next(BackupSettings s, BackupStatus st, DateTimeOffset utcNow, TimeZoneInfo tz)
    {
        if (!s.Enabled) return null;
        if (Due(s, st, utcNow, tz) is { }) return utcNow.UtcDateTime;
        var today = ScheduledUtc(utcNow.UtcDateTime, s.HourLocal, tz, 0);
        return utcNow.UtcDateTime < today ? today : ScheduledUtc(utcNow.UtcDateTime, s.HourLocal, tz, 1);
    }

    private static DateTime ScheduledUtc(DateTime utcNow, int hour, TimeZoneInfo tz, int dayOffset)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var at = DateTime.SpecifyKind(local.Date.AddDays(dayOffset).AddHours(Math.Clamp(hour, 0, 23)), DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(at)) at = at.AddHours(1); // skipped by a daylight-saving jump
        return TimeZoneInfo.ConvertTimeToUtc(at, tz);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly object StatusLock = new();

    private void UpdateStatus(Action<BackupStatus> change)
    {
        try
        {
            lock (StatusLock)
            {
                var st = store.GetSettings<BackupStatus>();
                change(st);
                store.SaveSettings(st);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the backup status");
        }
    }

    /// <summary>Checks that the directory can be created and written by the service. Returns an error message or null.</summary>
    public string? ProbeWritable(string dir)
    {
        var probe = Path.Combine(dir, $".cpm-write-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "test");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(probe);
            return Describe(ex, dir);
        }
    }

    internal static string Describe(Exception ex, string dir)
    {
        var msg = ex is BackupRunException ? ex.Message : $"Could not write the backup to {dir}: {ex.Message}";
        if (ex is UnauthorizedAccessException or IOException)
        {
            if (dir.StartsWith(@"\\", StringComparison.Ordinal))
                msg += $" The service runs as LocalSystem and reaches network shares as the computer account " +
                       $"({Environment.UserDomainName}\\{Environment.MachineName}$): grant that account Modify on both the share and the folder.";
            else if (ex is UnauthorizedAccessException)
                msg += " Grant SYSTEM Modify permission on the folder.";
        }
        return msg;
    }

    private void RestrictIfLocal(string file, string dir)
    {
        if (!OperatingSystem.IsWindows() || dir.StartsWith(@"\\", StringComparison.Ordinal)) return;
        var full = Path.GetFullPath(dir);
        if (full.StartsWith(Path.GetFullPath(paths.DataDir), StringComparison.OrdinalIgnoreCase)) return; // DataDir ACL already applies
        try
        {
            // Backups contain the database and private keys: SYSTEM and Administrators only.
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restrict the permissions of backup {File}; make sure only administrators can read {Dir}", file, dir);
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Runs the daily scheduled backup (checks every OpsOptions.BackupCheckInterval).</summary>
internal sealed class ScheduledBackupService(ScheduledBackups runner, IStore store, TimeProvider time, IOptions<OpsOptions> options,
    ILogger<ScheduledBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        try { await Task.Delay(o.MonitorStartDelay, time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunIfDueAsync(stoppingToken);
            try { await Task.Delay(o.BackupCheckInterval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One schedule check. Returns the archive name when a backup ran successfully.</summary>
    internal async Task<string?> RunIfDueAsync(CancellationToken ct)
    {
        try
        {
            var s = store.GetSettings<BackupSettings>();
            var st = store.GetSettings<BackupStatus>();
            if (ScheduledBackups.Due(s, st, time.GetUtcNow(), time.LocalTimeZone) is null) return null;
            return await runner.RunAsync(scheduled: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (BackupRunException) { return null; } // already logged, audited and raised as an event
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled backup check failed");
            return null;
        }
    }
}
