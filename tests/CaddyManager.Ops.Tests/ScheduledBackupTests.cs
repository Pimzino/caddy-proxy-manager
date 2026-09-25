using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Backup;
using CaddyManager.Ops.Events;

namespace CaddyManager.Ops.Tests;

public class ScheduledBackupTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "cpm-backup-target", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_target, true); } catch { /* temp */ }
    }

    private static string Prefix => ScheduledBackups.MachinePrefix;

    [Fact]
    public async Task Settings_have_defaults_and_are_validated()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        var get = await (await admin.GetAsync("api/settings/backup")).JsonAsync();
        Assert.False(get.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, get.GetProperty("hourLocal").GetInt32());
        Assert.Equal(14, get.GetProperty("keep").GetInt32());
        Assert.False(get.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(app.Paths.BackupDir, get.GetProperty("directory").GetString());
        Assert.Contains("Windows Explorer", get.GetProperty("encryptionNote").GetString());
        Assert.False(get.TryGetProperty("nextRunAt", out _));

        var bad = await admin.PutJsonAsync("api/settings/backup", new
        {
            hourLocal = 24, keep = 0, directory = "relative/dir", password = "short",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.JsonAsync()).GetProperty("errors");
        foreach (var f in new[] { "hourLocal", "keep", "directory", "password" })
            Assert.True(errors.TryGetProperty(f, out _), $"missing {f}: {errors}");

        var inside = await admin.PutJsonAsync("api/settings/backup", new { directory = app.Paths.CaddyStorageDir });
        Assert.Equal(HttpStatusCode.BadRequest, inside.StatusCode);
        Assert.Contains("data directory", (await inside.JsonAsync()).GetProperty("errors").GetProperty("directory")[0].GetString());

        var ok = await (await admin.PutJsonAsync("api/settings/backup", new
        {
            enabled = true, hourLocal = 3, keep = 5, directory = _target, password = "backup-passphrase-1",
        })).JsonAsync();
        Assert.True(ok.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(_target, ok.GetProperty("directory").GetString());
        Assert.True(ok.TryGetProperty("nextRunAt", out _));
        Assert.False(ok.TryGetProperty("passwordProtected", out _));
        Assert.DoesNotContain("backup-passphrase-1", ok.ToString());

        // Sending the default directory back (as the UI does) stores "default".
        await admin.PutJsonAsync("api/settings/backup", new { directory = app.Paths.BackupDir });
        Assert.Null(app.Store.GetSettings<BackupSettings>().Directory);
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.ObjectId == "backup" && (a.Details ?? "").Contains("encryption password set"));
    }

    [Fact]
    public async Task Run_now_writes_lists_and_downloads_a_plain_backup()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/backup", new { directory = _target })).StatusCode);

        var run = await admin.PostAsJsonAsync("api/backups/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var name = (await run.JsonAsync()).GetProperty("name").GetString()!;
        Assert.StartsWith(Prefix, name);
        Assert.Matches(ScheduledBackups.ArchiveName(), name);
        Assert.True(File.Exists(Path.Combine(_target, name)));
        Assert.Empty(Directory.GetFiles(_target, "*.partial"));

        var list = await (await admin.GetAsync("api/backups")).JsonAsync();
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(name, item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("size").GetInt64() > 0);
        Assert.True(item.TryGetProperty("createdAt", out _));

        var dl = await admin.GetAsync($"api/backups/{name}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/zip", dl.Content.Headers.ContentType?.MediaType);
        using (var zip = new ZipArchive(new MemoryStream(await dl.Content.ReadAsByteArrayAsync())))
        {
            Assert.NotNull(zip.GetEntry("manager.db"));
            Assert.NotNull(zip.GetEntry("manifest.json"));
        }

        foreach (var bad in new[] { "..%2F..%2Fdb%2Fmanager.db", "manager.db", "caddy-proxy-manager-x-20260101-000000.zip", "caddy-proxy-manager-%2E%2E-20260101-000000.zip" })
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"api/backups/{bad}")).StatusCode);

        var status = await (await admin.GetAsync("api/settings/backup")).JsonAsync();
        Assert.True(status.GetProperty("lastSucceeded").GetBoolean());
        Assert.Equal(name, status.GetProperty("lastBackupName").GetString());
        var audit = app.Store.Col<AuditEntry>().FindAll().ToList();
        Assert.Contains(audit, a => a.Action == "backup" && a.ObjectName == name);
        Assert.Contains(audit, a => a.Action == "downloaded" && a.ObjectName == name);

        var viewer = app.CreateUser("viewer@example.com", UserRole.Viewer);
        var vc = await app.LoginAsync(viewer.Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await vc.GetAsync("api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vc.PostAsJsonAsync("api/backups/run", new { })).StatusCode);
    }

    [Fact]
    public async Task Encrypted_backup_needs_the_password_to_restore()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        await admin.PutJsonAsync("api/settings/backup", new { directory = _target, password = "correct-backup-pass" });
        var name = (await (await admin.PostAsJsonAsync("api/backups/run", new { })).JsonAsync()).GetProperty("name").GetString()!;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_target, name));

        Assert.True(BackupEncryption.IsEncrypted(new MemoryStream(bytes)));
        // Standard tools see a zip whose entries are AES-encrypted (method 99): not readable without the password.
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
            Assert.ThrowsAny<Exception>(() => { using var s = zip.GetEntry("manager.db")!.Open(); s.ReadByte(); });
        }

        var noPassword = await Upload(admin, bytes, null);
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);
        Assert.Contains("encrypted", (await noPassword.JsonAsync()).GetProperty("errors").GetProperty("password")[0].GetString());
        var wrong = await Upload(admin, bytes, "not-the-password");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("incorrect", (await wrong.JsonAsync()).GetProperty("detail").GetString());
        Assert.False(RestoreStager.HasPendingRestore(app.Paths));

        var ok = await Upload(admin, bytes, "correct-backup-pass");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(RestoreStager.HasPendingRestore(app.Paths));
        Assert.True(File.Exists(Path.Combine(RestoreStager.PendingDir(app.Paths), "manager.db")));
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "restoreStaged" && (a.Details ?? "").Contains("encrypted"));

        // Plain backups still restore without a password (existing behaviour).
        var plain = await (await admin.GetAsync("api/backup")).Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, plain, "ignored")).StatusCode);
    }

    [Fact]
    public void Encryption_roundtrip_preserves_every_entry()
    {
        var plain = new MemoryStream();
        using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, true))
        {
            foreach (var (n, content) in new[] { ("manifest.json", "{}"), ("certificates/a/fullchain.pem", new string('x', 100_000)), ("empty.txt", "") })
            {
                using var w = new StreamWriter(zip.CreateEntry(n).Open());
                w.Write(content);
            }
        }
        plain.Position = 0;
        var encrypted = new MemoryStream();
        BackupEncryption.Encrypt(plain, encrypted, "pass-phrase-123", default);
        encrypted.Position = 0;
        Assert.True(BackupEncryption.IsEncrypted(encrypted));
        Assert.Equal(0, encrypted.Position);
        plain.Position = 0;
        Assert.False(BackupEncryption.IsEncrypted(plain));

        var decrypted = new MemoryStream();
        BackupEncryption.Decrypt(encrypted, decrypted, "pass-phrase-123", long.MaxValue, 100, default);
        decrypted.Position = 0;
        using var result = new ZipArchive(decrypted);
        Assert.Equal(3, result.Entries.Count);
        using var r = new StreamReader(result.GetEntry("certificates/a/fullchain.pem")!.Open());
        Assert.Equal(100_000, r.ReadToEnd().Length);

        encrypted.Position = 0;
        Assert.Throws<InvalidDataException>(() => BackupEncryption.Decrypt(encrypted, new MemoryStream(), "pass-phrase-123", 1000, 100, default));
        encrypted.Position = 0;
        Assert.Throws<BackupPasswordException>(() => BackupEncryption.Decrypt(encrypted, new MemoryStream(), "", long.MaxValue, 100, default));
    }

    [Fact]
    public async Task Retention_keeps_the_newest_archives_of_this_server_only()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        Directory.CreateDirectory(_target);
        var old = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var f = Path.Combine(_target, $"{Prefix}2026010{i + 1}-020000.zip");
            File.WriteAllText(f, "old");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-10 + i));
            old.Add(f);
        }
        var foreign = Path.Combine(_target, "caddy-proxy-manager-OTHERSRV-20260101-020000.zip");
        File.WriteAllText(foreign, "other server");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-30));
        var unrelated = Path.Combine(_target, "notes.txt");
        File.WriteAllText(unrelated, "keep me");

        await admin.PutJsonAsync("api/settings/backup", new { directory = _target, keep = 2 });
        var name = (await (await admin.PostAsJsonAsync("api/backups/run", new { })).JsonAsync()).GetProperty("name").GetString()!;

        Assert.True(File.Exists(Path.Combine(_target, name)));
        Assert.True(File.Exists(old[2]));   // newest old one
        Assert.False(File.Exists(old[0]));
        Assert.False(File.Exists(old[1]));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(unrelated));
        // The listing shows every server's archives (shared UNC folders help disaster recovery).
        var list = await (await admin.GetAsync("api/backups")).JsonAsync();
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal(name, list[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Due_follows_the_local_hour_and_runs_once_per_day()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var s = new BackupSettings { Enabled = true, HourLocal = 2 };
        var st = new BackupStatus();
        // 23:30 UTC = 01:30 local next day → not yet due.
        Assert.Null(ScheduledBackups.Due(s, st, DateTimeOffset.Parse("2026-09-24T23:30:00Z"), tz));
        var due = ScheduledBackups.Due(s, st, DateTimeOffset.Parse("2026-09-25T00:10:00Z"), tz);
        Assert.Equal(DateTime.Parse("2026-09-25T00:00:00Z").ToUniversalTime(), due);
        st.LastScheduledRunAt = DateTime.Parse("2026-09-25T00:10:00Z").ToUniversalTime();
        Assert.Null(ScheduledBackups.Due(s, st, DateTimeOffset.Parse("2026-09-25T12:00:00Z"), tz));
        Assert.Equal(DateTime.Parse("2026-09-26T00:00:00Z").ToUniversalTime(),
            ScheduledBackups.Next(s, st, DateTimeOffset.Parse("2026-09-25T12:00:00Z"), tz));
        // Missed while stopped: made up later the same day.
        Assert.NotNull(ScheduledBackups.Due(s, st, DateTimeOffset.Parse("2026-09-26T20:00:00Z"), tz));
        Assert.Null(ScheduledBackups.Due(new BackupSettings { Enabled = false }, new BackupStatus(), DateTimeOffset.UtcNow, tz));
        Assert.Null(ScheduledBackups.Next(new BackupSettings { Enabled = false }, new BackupStatus(), DateTimeOffset.UtcNow, tz));
    }

    [Fact]
    public async Task Scheduled_run_failure_raises_a_backup_event_and_recovers()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T01:00:00Z"), TimeZoneInfo.Utc);
        await using var app = await TestApp.StartAsync(time: clock);
        await app.SetupAdminAsync();
        // A directory that cannot be created (a file is in the way), as when a share disappears.
        Directory.CreateDirectory(_target);
        var blocker = Path.Combine(_target, "blocked");
        File.WriteAllText(blocker, "file");
        app.Store.SaveSettings(new BackupSettings { Enabled = true, HourLocal = 2, Directory = Path.Combine(blocker, "sub") });

        var service = app.Services.GetRequiredService<ScheduledBackupService>();
        Assert.Null(await service.RunIfDueAsync(default)); // 01:00, not due
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Null(await service.RunIfDueAsync(default)); // due, fails
        var failure = app.Store.Col<EventEntry>().FindOne(e => e.Key == ScheduledBackups.FailureKey);
        Assert.NotNull(failure);
        Assert.Equal(EventSeverity.Error, failure.Severity);
        Assert.Equal("backup", failure.Category);
        Assert.Contains("Could not write the backup", failure.Details);
        await HttpExtensions.WaitUntilAsync(() => !app.Notifier!.Sent.IsEmpty);
        Assert.Contains(app.Notifier!.Sent, n => n.Subject.Contains("Scheduled backup failed"));
        var status = app.Store.GetSettings<BackupStatus>();
        Assert.False(status.LastSucceeded);
        Assert.NotNull(status.LastError);
        // Not retried every minute the same day.
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await service.RunIfDueAsync(default));
        Assert.Single(app.Store.Col<EventEntry>().Find(e => e.Key == ScheduledBackups.FailureKey));

        // Next day with a working directory: backup written and the alert recovers.
        app.Store.SaveSettings(new BackupSettings { Enabled = true, HourLocal = 2, Directory = _target });
        clock.Advance(TimeSpan.FromDays(1));
        var name = await service.RunIfDueAsync(default);
        Assert.NotNull(name);
        Assert.True(File.Exists(Path.Combine(_target, name!)));
        Assert.Contains(app.Store.Col<EventEntry>().FindAll(), e => e.Key == ScheduledBackups.FailureKey && e.Severity == EventSeverity.Recovered);
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "scheduledBackup" && a.ObjectName == name);
    }

    [Fact]
    public void Backup_failure_rule_follows_the_configuration_failure_toggle()
    {
        var sink = typeof(EventSink);
        Assert.NotNull(sink);
        var paths = new AppPaths(_target);
        using var store = new Core.Infrastructure.LiteStore(paths);
        var es = new EventSink(store, new FakeNotifier(), new NullEventLog(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventSink>.Instance);
        Assert.True(es.IsRuleEnabled(new NotificationSettings { AlertConfigFailure = true }, "backupFailure"));
        Assert.False(es.IsRuleEnabled(new NotificationSettings { AlertConfigFailure = false }, "backupFailure"));
        Assert.Equal(1702, WindowsEventLogWriter.EventId(new EventEntry { Category = "backup", Severity = EventSeverity.Error }));
    }

    private sealed class NullEventLog : IEventLogWriter
    {
        public void Write(EventEntry entry) { }
    }

    private static Task<HttpResponseMessage> Upload(HttpClient c, byte[] data, string? password)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        if (password is not null) form.Add(new StringContent(password), "password");
        return c.PostAsync("api/backup/restore", form);
    }
}
