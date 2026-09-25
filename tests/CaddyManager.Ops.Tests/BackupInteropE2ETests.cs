using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// End to end: an encrypted backup written by the real backup run (API → ScheduledBackups → SharpZipLib AES-256) is
/// opened by independent tools administrators actually use: 7-Zip (preinstalled on the Windows CI runner) and libarchive's
/// bsdtar (the engine behind Windows 11 Explorer's archive support; /usr/bin/bsdtar on macOS). The documentation promises
/// "7-Zip / WinZip / WinRAR" can open them (docs/backup-restore.md).
///
/// Ways this can fail:
///  1. The archive is not WinZip AE-2 / AES-256 that other tools understand (SharpZipLib option or version change), so an
///     administrator cannot open a backup outside the product (e.g. to recover caddy.json).
///  2. A wrong password "extracts" garbage instead of failing.
///  3. Entry names or contents differ from the plain backup (manifest.json, manager.db).
///  4. The tool is missing on the machine (then this test is skipped for that tool and the artifact says so).
/// </summary>
public class BackupInteropE2ETests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cpm-backup-interop", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static string? Tool(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(true); return (-1, "timeout"); }
        return (p.ExitCode, output.Result + error.Result);
    }

    [Fact]
    public async Task Encrypted_backups_open_in_7zip_and_libarchive()
    {
        const string password = "correct-backup-pass";
        var target = Path.Combine(_dir, "target");
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        await admin.PutJsonAsync("api/settings/backup", new { directory = target, password });
        var name = (await (await admin.PostAsJsonAsync("api/backups/run", new { })).JsonAsync()).GetProperty("name").GetString()!;
        var file = Path.Combine(target, name);
        using var names = new ZipArchive(File.OpenRead(file));
        var entries = names.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).ToList();

        var report = E2EArtifacts.Report(nameof(Encrypted_backups_open_in_7zip_and_libarchive));
        report["archive"] = name;
        report["entries"] = string.Join(",", entries);
        var tools = 0;

        var sevenZip = Tool(@"C:\Program Files\7-Zip\7z.exe", "/usr/local/bin/7z", "/opt/homebrew/bin/7z", "/usr/bin/7z");
        if (sevenZip is not null)
        {
            tools++;
            var ok = Run(sevenZip, "t", "-p" + password, file);
            var bad = Run(sevenZip, "t", "-pnot-the-password", file);
            report["7zip"] = new JsonObject { ["okExit"] = ok.Exit, ["badExit"] = bad.Exit, ["ok"] = ok.Output.Trim() };
            Assert.True(ok.Exit == 0, ok.Output);
            Assert.Contains("Everything is Ok", ok.Output);
            Assert.NotEqual(0, bad.Exit);
        }
        else report["7zip"] = "not installed";

        var bsdtar = Tool("/usr/bin/bsdtar", "/usr/local/bin/bsdtar", "/opt/homebrew/bin/bsdtar");
        if (bsdtar is not null)
        {
            tools++;
            var outDir = Path.Combine(_dir, "bsdtar");
            Directory.CreateDirectory(outDir);
            var ok = Run(bsdtar, "-x", "-f", file, "--passphrase", password, "-C", outDir);
            var badDir = Path.Combine(_dir, "bsdtar-bad");
            Directory.CreateDirectory(badDir);
            var bad = Run(bsdtar, "-x", "-f", file, "--passphrase", "not-the-password", "-C", badDir);
            report["bsdtar"] = new JsonObject { ["okExit"] = ok.Exit, ["badExit"] = bad.Exit, ["badOutput"] = bad.Output.Trim() };
            Assert.True(ok.Exit == 0, ok.Output);
            foreach (var entry in entries) Assert.True(File.Exists(Path.Combine(outDir, entry)), $"{entry} not extracted");
            Assert.Contains("\"product\"", await File.ReadAllTextAsync(Path.Combine(outDir, "manifest.json")), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(0, bad.Exit);
        }
        else report["bsdtar"] = "not installed";

        E2EArtifacts.Write("backup-aes-interop.json", report);
        Assert.True(tools > 0, "Neither 7-Zip nor bsdtar is installed; nothing verified.");
    }
}
