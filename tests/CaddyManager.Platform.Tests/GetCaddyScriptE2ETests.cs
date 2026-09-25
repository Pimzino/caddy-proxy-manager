using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Platform.Infrastructure;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// .github/scripts/get-caddy.ps1 (CI's Caddy download) run for real with pwsh against a local mock of the GitHub releases
/// API and asset downloads, so the checksum handling is exercised without the network.
///
/// Ways the script can fail:
///  1. The release has no caddy_&lt;ver&gt;_checksums.txt asset and the script silently skips the SHA-512 check, installing
///     an unverified caddy.exe (the old `if ($sums) { ... }` without else).
///  2. The checksums file has no line for the zip and the script accepts the zip anyway.
///  3. The entry is not a SHA-512 digest (e.g. a 64-hex SHA-256) and the script accepts it or reports a misleading error.
///  4. The digest does not match the zip and the script accepts it.
///  5. A correct digest is rejected (e.g. lower- vs upper-case hex), so CI can never pass.
///  6. After a failed check a caddy.exe is left in the output directory (a later step would test an unverified binary).
/// Skipped when pwsh is not installed. Artifact: get-caddy-checksums.json.
/// </summary>
[Trait("Category", "Script")]
public class GetCaddyScriptE2ETests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".github", "scripts", "get-caddy.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Skip(".github/scripts/get-caddy.ps1 not found above the test output directory.");
        return "";
    }

    private static async Task<string?> PwshAsync(CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync("pwsh", ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"],
                new ProcessOptions { Timeout = TimeSpan.FromSeconds(60) }, ct);
            return r.ExitCode == 0 ? r.StdOut.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Serves /repos/caddyserver/caddy/releases/latest and the assets from memory.</summary>
    private sealed class MockGitHub : IDisposable
    {
        private readonly MiniHttpServer _server;
        public Dictionary<string, byte[]> Files { get; } = new();
        public string Base => _server.Base;
        public List<string> Requests => _server.Requests;

        public MockGitHub() =>
            _server = new MiniHttpServer((_, path) => Files.TryGetValue(path, out var bytes) ? (200, bytes) : null);

        public void Dispose() => _server.Dispose();
    }

    /// <summary>pwsh's error view: ANSI colours (unless NO_COLOR is honoured) and messages wrapped at "     | ".</summary>
    private static string Plain(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*[A-Za-z]", "");
        return System.Text.RegularExpressions.Regex.Replace(s, "\\s*\\r?\\n\\s*\\|?\\s*", " ").Trim();
    }

    [Fact]
    public async Task ChecksumsAreRequiredAndEnforced()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = Script();
        var pwsh = await PwshAsync(ct);
        Assert.SkipWhen(pwsh is null, "pwsh is not installed.");

        const string ver = "9.9.9";
        var zipName = $"caddy_{ver}_windows_amd64.zip";
        var sumsName = $"caddy_{ver}_checksums.txt";
        var work = Path.Combine(Path.GetTempPath(), "cpm-get-caddy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var report = E2EArtifacts.Report(nameof(ChecksumsAreRequiredAndEnforced));
        report["pwsh"] = pwsh;
        report["caddyVersion"] = $"mock release v{ver} (no Caddy binary is executed; only the script's checksum handling is tested)";
        report["script"] = script;
        var cases = new JsonObject();
        report["cases"] = cases;

        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("caddy.exe");
                await using var w = entry.Open();
                await w.WriteAsync(Encoding.ASCII.GetBytes("not a real caddy.exe"), ct);
            }
            zip = ms.ToArray();
        }
        var sha512 = Convert.ToHexString(SHA512.HashData(zip)).ToLowerInvariant(); // Caddy's file uses lower case (5)

        async Task<(int Exit, string Output, bool Installed)> Run(string label, string? sums)
        {
            using var gh = new MockGitHub();
            var assets = new JsonArray { new JsonObject { ["name"] = zipName, ["browser_download_url"] = $"{gh.Base}/dl/{zipName}" } };
            if (sums is not null)
            {
                assets.Add(new JsonObject { ["name"] = sumsName, ["browser_download_url"] = $"{gh.Base}/dl/{sumsName}" });
                gh.Files[$"/dl/{sumsName}"] = Encoding.ASCII.GetBytes(sums);
            }
            gh.Files["/repos/caddyserver/caddy/releases/latest"] = Encoding.UTF8.GetBytes(new JsonObject { ["tag_name"] = $"v{ver}", ["assets"] = assets }.ToJsonString());
            gh.Files[$"/dl/{zipName}"] = zip;
            var outDir = Path.Combine(work, label, "bin");
            var temp = Path.Combine(work, label, "tmp");
            Directory.CreateDirectory(temp);
            var r = await ProcessRunner.RunAsync("pwsh", ["-NoProfile", "-NonInteractive", "-File", script, "-Which", "latest", "-ApiBase", gh.Base, "-OutDir", outDir],
                new ProcessOptions
                {
                    Timeout = TimeSpan.FromMinutes(2),
                    Environment = new Dictionary<string, string> { ["RUNNER_TEMP"] = temp, ["GITHUB_ENV"] = "", ["GITHUB_ACTIONS"] = "", ["NO_COLOR"] = "1" },
                }, ct);
            var installed = File.Exists(Path.Combine(outDir, "caddy.exe"));
            var output = Plain(r.Combined);
            cases[label] = new JsonObject
            {
                ["exitCode"] = r.ExitCode, ["installed"] = installed, ["output"] = output,
                ["requests"] = string.Join(" ", gh.Requests),
            };
            return (r.ExitCode, output, installed);
        }

        try
        {
            // (1)(6) No checksums asset.
            var missing = await Run("no-checksums-asset", null);
            Assert.NotEqual(0, missing.Exit);
            Assert.Contains("has no " + sumsName, missing.Output);
            Assert.False(missing.Installed);

            // (2) No entry for the zip.
            var noEntry = await Run("no-entry", $"{sha512}  caddy_{ver}_linux_amd64.tar.gz\n");
            Assert.NotEqual(0, noEntry.Exit);
            Assert.Contains("has no entry for " + zipName, noEntry.Output);
            Assert.False(noEntry.Installed);

            // (3) Not SHA-512.
            var sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            var notSha512 = await Run("not-sha512", $"{sha256}  {zipName}\n");
            Assert.NotEqual(0, notSha512.Exit);
            Assert.Contains("is not a SHA-512 digest", notSha512.Output);
            Assert.False(notSha512.Installed);

            // (4) Mismatch.
            var wrong = new string('0', 128);
            var mismatch = await Run("mismatch", $"{wrong}  {zipName}\n");
            Assert.NotEqual(0, mismatch.Exit);
            Assert.Contains("does not match the release checksums", mismatch.Output);
            Assert.False(mismatch.Installed);

            // (5) Correct digest (lower case, as in Caddy's checksums file) passes the check and installs the file. The
            // script then runs `caddy.exe version`, which fails for this fake binary: that later failure is expected.
            var good = await Run("correct", $"{new string('f', 128)}  caddy_{ver}_mac_arm64.tar.gz\n{sha512}  {zipName}\n");
            Assert.Contains($"SHA-512 of {zipName} verified", good.Output);
            Assert.True(good.Installed);
        }
        finally
        {
            E2EArtifacts.Write("get-caddy-checksums.json", report);
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }
}
