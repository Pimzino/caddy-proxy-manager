using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Backup;

/// <summary>
/// Scheduled backups. Owned by the Ops module (IStore.GetSettings). Wire: <c>passwordProtected</c> → output
/// <c>hasPassword</c>, input <c>password</c> (encrypts the archives with WinZip AES-256).
/// </summary>
public sealed class BackupSettings : ISettingsDocument
{
    public bool Enabled { get; set; }
    /// <summary>Local hour (0-23) at which the daily backup runs.</summary>
    public int HourLocal { get; set; } = 2;
    /// <summary>Local folder or UNC path (\\server\share\folder). null = AppPaths.BackupDir (DataDir\backups).</summary>
    public string? Directory { get; set; }
    /// <summary>Number of scheduled backups of this server to keep in the directory (older ones are deleted).</summary>
    public int Keep { get; set; } = 14;
    public string? PasswordProtected { get; set; }
}

/// <summary>Outcome of the last scheduled/manual run (separate document so status writes never race settings edits).</summary>
public sealed class BackupStatus : ISettingsDocument
{
    public DateTime? LastRunAt { get; set; }
    /// <summary>Last run started by the schedule (drives "already ran today").</summary>
    public DateTime? LastScheduledRunAt { get; set; }
    public bool? LastSucceeded { get; set; }
    public string? LastBackupName { get; set; }
    public string? LastError { get; set; }
}
