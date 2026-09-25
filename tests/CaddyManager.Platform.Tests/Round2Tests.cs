using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Background;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Readiness;
using CaddyManager.Platform.Windows;
using Microsoft.AspNetCore.Http;

namespace CaddyManager.Platform.Tests;

internal static class Jobs
{
    public static async Task<JobInfo> WaitAsync(IJobRunner jobs, string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var j = jobs.Get(id)!;
            if (j.State != JobState.Running) return j;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {id} did not finish within {timeout}.");
    }

    public static string Sha512(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA512.HashData(fs));
    }

    /// <summary>Copies a file into a fresh upload directory under the staging directory (like the upload endpoint does).</summary>
    public static string Stage(AppPaths paths, string source, string name)
    {
        var dir = Path.Combine(paths.CaddyStagingDir, "upload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.Copy(source, file);
        return file;
    }

    public static string StageBytes(AppPaths paths, byte[] content, string name)
    {
        var dir = Path.Combine(paths.CaddyStagingDir, "upload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.WriteAllBytes(file, content);
        return file;
    }
}

// ============================================================================ executable / archive detection

public class ExecutableFormatTests
{
    private static byte[] Pe(ushort machine)
    {
        var b = new byte[512];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x84), machine);
        return b;
    }

    internal static byte[] Elf(ushort machine, byte osAbi = 0)
    {
        var b = new byte[128];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F';
        b[4] = 2; b[5] = 1; b[6] = 1; b[7] = osAbi;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), machine);
        return b;
    }

    [Fact]
    public void DetectsPlatformsFromHeaders()
    {
        Assert.Equal("windows/amd64", ExecutableFormat.Detect(Pe(0x8664))!.ToString());
        Assert.Equal("windows/arm64", ExecutableFormat.Detect(Pe(0xAA64))!.ToString());
        Assert.Equal("linux/amd64", ExecutableFormat.Detect(Elf(62))!.ToString());
        Assert.Equal("linux/arm64", ExecutableFormat.Detect(Elf(183))!.ToString());
        Assert.Equal("freebsd/amd64", ExecutableFormat.Detect(Elf(62, osAbi: 9))!.ToString());

        var macho = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(macho, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(macho.AsSpan(4), 0x0100000C);
        Assert.Equal("darwin/arm64", ExecutableFormat.Detect(macho)!.ToString());

        var fat = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(fat.AsSpan(8), 0x01000007);
        BinaryPrimitives.WriteInt32BigEndian(fat.AsSpan(28), 0x0100000C);
        var universal = ExecutableFormat.Detect(fat)!;
        Assert.True(universal.Matches(new CaddyPlatform("darwin", "amd64")));
        Assert.True(universal.Matches(new CaddyPlatform("darwin", "arm64")));
        Assert.False(universal.Matches(new CaddyPlatform("linux", "arm64")));

        Assert.Null(ExecutableFormat.Detect(Encoding.ASCII.GetBytes(new string('x', 200))));
        Assert.Null(ExecutableFormat.Detect(new byte[10]));
        Assert.True(ExecutableFormat.Detect(Pe(0x8664))!.Matches(new CaddyPlatform("windows", "amd64")));
        Assert.False(ExecutableFormat.Detect(Pe(0x8664))!.Matches(new CaddyPlatform("windows", "arm64")));
    }

    [Fact]
    public void ClassifiesUploads()
    {
        Assert.Equal(UploadKind.Zip, ExecutableFormat.DetectKind("PK\u0003\u0004rest"u8.ToArray()));
        Assert.Equal(UploadKind.TarGz, ExecutableFormat.DetectKind(new byte[] { 0x1F, 0x8B, 8, 0 }));
        Assert.Equal(UploadKind.Executable, ExecutableFormat.DetectKind(Pe(0x8664)));
        Assert.Equal(UploadKind.Unknown, ExecutableFormat.DetectKind("<html>not a binary</html>"u8.ToArray()));
    }

    [Fact]
    public void DetectsTheDevelopmentBinary()
    {
        var dev = DevCaddy.Find();
        Assert.SkipWhen(dev is null, "Development Caddy binary not found.");
        var target = ExecutableFormat.Detect(dev!);
        Assert.NotNull(target);
        Assert.True(target.Matches(CaddyPlatform.Current), $"{target} vs {CaddyPlatform.Current}");
        Assert.Equal(UploadKind.Executable, ExecutableFormat.DetectKind(dev!));
    }

    [Fact]
    public void NormalizesExpectedSha512()
    {
        var hex = new string('a', 64) + new string('B', 64);
        Assert.Equal(hex.ToLowerInvariant(), ExecutableFormat.NormalizeSha512("  sha512:" + hex[..60] + " " + hex[60..] + "\n"));
        Assert.Null(ExecutableFormat.NormalizeSha512(null));
        Assert.Null(ExecutableFormat.NormalizeSha512("   "));
        Assert.Throws<ArgumentException>(() => ExecutableFormat.NormalizeSha512("abc"));
        Assert.Throws<ArgumentException>(() => ExecutableFormat.NormalizeSha512(new string('g', 128)));
    }

    [Fact]
    public void ExtractsByContentWhateverTheName()
    {
        using var env = new TempEnvironment();
        var payload = "fake-binary"u8.ToArray();
        var platform = new CaddyPlatform("windows", "amd64");
        var zip = Path.Combine(env.Root, "upload.bin");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var s = z.CreateEntry("caddy_2.11.4_windows_amd64/caddy.exe").Open();
            s.Write(payload);
        }
        var outExe = Path.Combine(env.Root, "out.exe");
        CaddyBinaryManager.ExtractBinary(zip, platform, outExe, byContent: true);
        Assert.Equal(payload, File.ReadAllBytes(outExe));

        var tgz = Path.Combine(env.Root, "upload.zip"); // misleading name: content decides
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "caddy") { DataStream = new MemoryStream(payload) });
        var outBin = Path.Combine(env.Root, "caddy-out");
        CaddyBinaryManager.ExtractBinary(tgz, new CaddyPlatform("linux", "amd64"), outBin, byContent: true);
        Assert.Equal(payload, File.ReadAllBytes(outBin));

        var ex = Assert.Throws<InvalidOperationException>(() => CaddyBinaryManager.ExtractBinary(zip, new CaddyPlatform("linux", "amd64"), outBin, byContent: true));
        Assert.Contains("caddy_<version>_linux_amd64.tar.gz", ex.Message);
    }
}

// ============================================================================ upload receiver

public class BinaryUploadReceiverTests
{
    private static async Task<HttpContext> RequestAsync(byte[]? file, string? fileName = "caddy.exe", string? sha = null, string? contentType = null)
    {
        var form = new MultipartFormDataContent("----cpm-test-boundary");
        if (file is not null) form.Add(new ByteArrayContent(file), "file", fileName!);
        if (sha is not null) form.Add(new StringContent(sha), "sha512");
        var body = new MemoryStream();
        await form.CopyToAsync(body);
        body.Position = 0;
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = contentType ?? form.Headers.ContentType!.ToString();
        ctx.Request.Body = body;
        return ctx;
    }

    [Fact]
    public async Task StreamsTheFileHashesItAndChecksTheExpectedSha()
    {
        using var env = new TempEnvironment();
        var ct = TestContext.Current.CancellationToken;
        var content = RandomNumberGenerator.GetBytes(300_000);
        var sha = Convert.ToHexStringLower(SHA512.HashData(content));

        var ok = await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content, "..\\evil/../caddy_2.11.4_windows_amd64.zip", sha.ToUpperInvariant())).Request,
            env.Paths.CaddyStagingDir, 1_000_000, ct);
        Assert.Equal(content, await File.ReadAllBytesAsync(ok.FilePath, ct));
        Assert.Equal(sha, ok.Sha512);
        Assert.Equal(sha, ok.ExpectedSha512);
        Assert.Equal(content.Length, ok.Length);
        Assert.Equal("caddy_2.11.4_windows_amd64.zip", ok.FileName);
        Assert.StartsWith(Path.Combine(env.Paths.CaddyStagingDir, "upload-"), ok.FilePath);

        var noSha = await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content)).Request, env.Paths.CaddyStagingDir, 1_000_000, ct);
        Assert.Null(noSha.ExpectedSha512);
    }

    [Fact]
    public async Task RejectsBadUploadsAndLeavesNothingBehind()
    {
        using var env = new TempEnvironment();
        var ct = TestContext.Current.CancellationToken;
        var content = RandomNumberGenerator.GetBytes(50_000);
        int Leftovers() => Directory.GetDirectories(env.Paths.CaddyStagingDir, "upload-*").Length;

        var mismatch = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content, sha: new string('0', 128))).Request, env.Paths.CaddyStagingDir, 1_000_000, ct));
        Assert.Equal("sha512", mismatch.Field);
        Assert.Contains("SHA-512 mismatch", mismatch.Message);

        var malformed = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content, sha: "not-a-hash")).Request, env.Paths.CaddyStagingDir, 1_000_000, ct));
        Assert.Equal("sha512", malformed.Field);

        var tooLarge = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content)).Request, env.Paths.CaddyStagingDir, 10_000, ct));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooLarge.StatusCode);

        var none = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(null, sha: new string('0', 128))).Request, env.Paths.CaddyStagingDir, 1_000_000, ct));
        Assert.Contains("No file was uploaded", none.Message);

        var empty = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync([])).Request, env.Paths.CaddyStagingDir, 1_000_000, ct));
        Assert.Contains("empty", empty.Message);

        var notMultipart = await Assert.ThrowsAsync<BinaryUploadException>(async () =>
            await BinaryUploadReceiver.ReceiveAsync((await RequestAsync(content, contentType: "application/octet-stream")).Request, env.Paths.CaddyStagingDir, 1_000_000, ct));
        Assert.Contains("multipart/form-data", notMultipart.Message);

        Assert.Equal(0, Leftovers());
    }

    [Theory]
    [InlineData("caddy.exe", "caddy.exe")]
    [InlineData("C:\\Users\\me\\Downloads\\caddy_2.11.4_windows_amd64.zip", "caddy_2.11.4_windows_amd64.zip")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("ca ddy$(x).exe", "ca_ddy__x_.exe")]
    [InlineData("..", "upload.bin")]
    [InlineData(null, "upload.bin")]
    public void SanitizesFileNames(string? input, string expected) => Assert.Equal(expected, BinaryUploadReceiver.SafeFileName(input));

    [Fact]
    public void CleansStaleUploadDirectories()
    {
        using var env = new TempEnvironment();
        var stale = Directory.CreateDirectory(Path.Combine(env.Paths.CaddyStagingDir, "upload-old"));
        Directory.SetCreationTimeUtc(stale.FullName, DateTime.UtcNow.AddDays(-2));
        var fresh = Directory.CreateDirectory(Path.Combine(env.Paths.CaddyStagingDir, "upload-new"));
        var other = Directory.CreateDirectory(Path.Combine(env.Paths.CaddyStagingDir, "job-dir"));
        Directory.SetCreationTimeUtc(other.FullName, DateTime.UtcNow.AddDays(-2));
        BinaryUploadReceiver.CleanStale(env.Paths.CaddyStagingDir, TimeSpan.FromHours(24));
        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(fresh.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }
}

// ============================================================================ offline install / rollback end to end

/// <summary>Offline installs with the real development Caddy binary as the "uploaded" file, then rollback.</summary>
[Trait("Category", "Caddy")]
public class OfflineInstallTests
{
    [Fact]
    public async Task InstallsFromUploadedBinaryUpdatesFromArchiveAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var dev = DevCaddy.Find();
        Assert.SkipWhen(dev is null, "Development Caddy binary (.dev/bin/caddy or CM_TEST_CADDY) not found.");
        const int adminPort = 12249;
        using var env = new TempEnvironment();
        var httpPort = DevCaddy.FreeTcpPort();
        using var svc = new PlatformServices(env, adminPort, DevCaddy.MinimalConfig(adminPort, httpPort, Path.Combine(env.Root, "caddy-runtime.log")));
        ICaddyBinaryManager bin = svc.Get<ICaddyBinaryManager>();
        var manager = svc.Get<CaddyBinaryManager>();
        var host = svc.Get<ICaddyHost>();
        var jobs = svc.Get<IJobRunner>();
        try
        {
            Assert.False(bin.CanRollback);
            Assert.Throws<InvalidOperationException>(() => bin.StartRollback());

            // 1. Fresh offline install of the plain binary with its SHA-512 → installed, Caddy started, service/apply bootstrap ran.
            var upload = Jobs.Stage(env.Paths, dev!, CaddyPlatform.Current.BinaryName);
            var job = bin.StartInstallFromFile(upload, Jobs.Sha512(upload).ToUpperInvariant());
            var done = await Jobs.WaitAsync(jobs, job.Id, TimeSpan.FromMinutes(3));
            Assert.True(done.State == JobState.Succeeded, string.Join("\n", done.Log) + done.Error);
            Assert.Contains(done.Log, l => l.Contains("SHA-512 checksum verified"));
            Assert.Contains(done.Log, l => l.Contains("matches this server"));
            Assert.False(File.Exists(upload));
            Assert.False(Directory.Exists(Path.GetDirectoryName(upload)));
            Assert.Empty(Directory.GetFileSystemEntries(env.Paths.CaddyStagingDir));
            var meta = manager.ReadMetadata()!;
            Assert.Equal("upload", meta.Source);
            Assert.Equal("v2.11.4", meta.Version);
            Assert.Empty(meta.Plugins);
            Assert.Equal(128, meta.Sha512?.Length);
            Assert.Equal("v2.11.4", (await bin.GetInstalledAsync(ct))?.Version);
            Assert.Equal(CaddyRunState.Running, (await host.GetStatusAsync(ct)).State);
            Assert.Contains("caddy installed", svc.Config.Applied);
            Assert.Contains(svc.Events.Events, e => e.Message == "Caddy v2.11.4 installed (uploaded file caddy" + (OperatingSystem.IsWindows() ? ".exe)" : ")"));
            Assert.False(bin.CanRollback);

            // 2. Update from an official-style release archive (content-detected): current binary kept as .previous.
            var archiveName = $"caddy_2.11.4_{CaddyPlatform.Current.ReleaseOs}_{CaddyPlatform.Current.ReleaseArch}.{CaddyPlatform.Current.ArchiveExtension}";
            var archive = Path.Combine(env.Root, archiveName);
            if (CaddyPlatform.Current.IsWindows)
            {
                using var z = ZipFile.Open(archive, ZipArchiveMode.Create);
                z.CreateEntryFromFile(dev!, "caddy.exe", CompressionLevel.Fastest);
            }
            else
            {
                await using var fs = File.Create(archive);
                await using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                await using var tar = new TarWriter(gz);
                await tar.WriteEntryAsync(dev!, "caddy", ct);
            }
            var pidBefore = (await host.GetStatusAsync(ct)).ProcessId;
            var update = await Jobs.WaitAsync(jobs, bin.StartInstallFromFile(Jobs.Stage(env.Paths, archive, archiveName)).Id, TimeSpan.FromMinutes(3));
            Assert.True(update.State == JobState.Succeeded, string.Join("\n", update.Log) + update.Error);
            Assert.Contains(update.Log, l => l.Contains("Extracting"));
            Assert.Contains(update.Log, l => l.Contains("No expected SHA-512 was given"));
            Assert.Contains(update.Log, l => l.Contains("Stopping Caddy"));
            Assert.True(File.Exists(env.Paths.CaddyExeBackup));
            Assert.True(bin.CanRollback);
            Assert.Equal("v2.11.4", await manager.GetPreviousVersionAsync(ct));
            Assert.Equal(archiveName, manager.ReadMetadata()?.Url);
            Assert.Equal(CaddyPlatform.Current.BinaryName, manager.ReadPreviousMetadata()?.Url);
            var afterUpdate = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, afterUpdate.State);
            Assert.NotEqual(pidBefore, afterUpdate.ProcessId);
            Assert.Contains(svc.Events.Events, e => e.Message.StartsWith("Caddy updated from v2.11.4 to v2.11.4 (uploaded file " + archiveName));

            // 3. Rollback swaps .previous back in through the same pipeline (and keeps the replaced binary as .previous).
            var rollback = await Jobs.WaitAsync(jobs, bin.StartRollback().Id, TimeSpan.FromMinutes(3));
            Assert.True(rollback.State == JobState.Succeeded, string.Join("\n", rollback.Log) + rollback.Error);
            Assert.Contains(rollback.Log, l => l.Contains("Staging the previous binary"));
            Assert.Contains(rollback.Log, l => l.Contains("Validating the current configuration"));
            Assert.Equal(CaddyPlatform.Current.BinaryName, manager.ReadMetadata()?.Url);  // the first upload is active again
            Assert.Equal(archiveName, manager.ReadPreviousMetadata()?.Url);                // and the archive build is the new .previous
            Assert.True(bin.CanRollback);
            Assert.Equal(CaddyRunState.Running, (await host.GetStatusAsync(ct)).State);
            Assert.Contains(svc.Events.Events, e => e.Message == "Caddy rolled back from v2.11.4 to v2.11.4");
            Assert.Empty(Directory.GetFileSystemEntries(env.Paths.CaddyStagingDir));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RejectsBadUploadsWithoutTouchingTheInstallation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dev = DevCaddy.Find();
        Assert.SkipWhen(dev is null, "Development Caddy binary (.dev/bin/caddy or CM_TEST_CADDY) not found.");
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12259, "{}");
        var bin = svc.Get<CaddyBinaryManager>();
        var jobs = svc.Get<IJobRunner>();

        async Task<JobInfo> Run(string file, string? sha = null)
        {
            var j = await Jobs.WaitAsync(jobs, bin.StartInstallFromFile(file, sha).Id, TimeSpan.FromMinutes(2));
            Assert.False(File.Exists(file), "the uploaded file must be deleted");
            return j;
        }

        var wrongSha = await Run(Jobs.Stage(env.Paths, dev!, "caddy"), new string('a', 128));
        Assert.Equal(JobState.Failed, wrongSha.State);
        Assert.Contains("SHA-512 checksum mismatch", wrongSha.Error);

        var text = await Run(Jobs.StageBytes(env.Paths, Encoding.UTF8.GetBytes(new string('x', 4096)), "caddy.exe"));
        Assert.Equal(JobState.Failed, text.State);
        Assert.Contains("neither a Caddy executable nor a release archive", text.Error);

        var foreign = await Run(Jobs.StageBytes(env.Paths, ExecutableFormatTests.Elf(243), "caddy"));
        Assert.Equal(JobState.Failed, foreign.State);
        Assert.Contains("linux/riscv64", foreign.Error);
        Assert.Contains($"this server needs {CaddyPlatform.Current}", foreign.Error);

        var zipPath = Path.Combine(env.Root, "readme-only.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create)) z.CreateEntry("README.md").Open().Dispose();
        var noBinary = await Run(Jobs.Stage(env.Paths, zipPath, "caddy.zip"));
        Assert.Equal(JobState.Failed, noBinary.State);
        Assert.Contains($"does not contain {CaddyPlatform.Current.BinaryName}", noBinary.Error);

        Assert.False(File.Exists(env.Paths.CaddyExe));
        Assert.Equal(4, svc.Events.Events.Count(e => e.Key == "caddy-update-failed" && e.Message.Contains("uploaded Caddy binary failed")));
        Assert.Empty(Directory.GetFileSystemEntries(env.Paths.CaddyStagingDir));

        Assert.Throws<FileNotFoundException>(() => bin.StartInstallFromFile(Path.Combine(env.Root, "missing")));
        var staged = Jobs.Stage(env.Paths, dev!, "caddy");
        Assert.Throws<ArgumentException>(() => bin.StartInstallFromFile(staged, "xyz"));
        Assert.False(File.Exists(staged)); // rejected uploads are removed too
    }
}

// ============================================================================ outbound proxy for Caddy

public class CaddyProxyEnvironmentTests
{
    [Fact]
    public void AddsProxyVariablesOnlyWhenEnabled()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cpm-env"));
        var plain = CaddyHostSupport.CaddyEnvironment(paths, new BinarySettings { OutboundProxy = "http://proxy:3128" });
        Assert.Equal(["XDG_DATA_HOME", "XDG_CONFIG_HOME"], plain.Keys.ToArray());
        Assert.Equal(plain, CaddyHostSupport.CaddyEnvironment(paths));
        Assert.Null(CaddyHostSupport.CaddyProxy(new BinarySettings { ProxyCaddyTraffic = true }));

        var env = CaddyHostSupport.CaddyEnvironment(paths, new BinarySettings
        {
            OutboundProxy = " http://user:p%40ss@proxy.corp:3128 ", ProxyCaddyTraffic = true, NoProxy = "localhost; 10.0.0.0/8\n.corp.local , localhost",
        });
        Assert.Equal("http://user:p%40ss@proxy.corp:3128", env["HTTPS_PROXY"]);
        Assert.Equal("http://user:p%40ss@proxy.corp:3128", env["HTTP_PROXY"]);
        Assert.Equal("localhost,10.0.0.0/8,.corp.local", env["NO_PROXY"]);
        Assert.Equal(paths.CaddyStorageDir, env["XDG_DATA_HOME"]);
        Assert.DoesNotContain(env.Keys, k => k != k.ToUpperInvariant());

#pragma warning disable CA1416
        var def = WindowsServiceCaddyHost.Definition(paths, new BinarySettings { OutboundProxy = "http://proxy:3128", ProxyCaddyTraffic = true });
#pragma warning restore CA1416
        Assert.Contains("HTTPS_PROXY=http://proxy:3128", def.Environment!);
        Assert.Contains($"NO_PROXY={CaddyHostSupport.NormalizeNoProxy(new BinarySettings().NoProxy)}", def.Environment!);
        Assert.StartsWith("Caddy uses the outbound proxy http://proxy:3128", CaddyEnvironmentSync.Describe(new BinarySettings { OutboundProxy = "http://proxy:3128", ProxyCaddyTraffic = true }));
        Assert.Equal("Caddy connects directly (no proxy)", CaddyEnvironmentSync.Describe(new BinarySettings { OutboundProxy = "http://proxy:3128" }));
        Assert.Contains("********", CaddyEnvironmentSync.Describe(new BinarySettings { OutboundProxy = "http://u:secret@proxy:3128", ProxyCaddyTraffic = true }));
    }

    [Fact]
    public void ValidatesNoProxyEntries()
    {
        Assert.Empty(CaddyHostSupport.ValidateNoProxy(new BinarySettings().NoProxy));
        Assert.Empty(CaddyHostSupport.ValidateNoProxy("*, *.corp.local, intranet, 192.168.1.10, [::1], fd00::/8, sap.corp.local:8443, 10.1.2.3:80"));
        var errors = CaddyHostSupport.ValidateNoProxy("good.local, bad_host!, host:99999, http://x");
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("'bad_host!'"));
        Assert.Contains(errors, e => e.Contains("'host:99999' has an invalid port"));
    }

    [Fact]
    public async Task ProcessHostDetectsAnOutdatedEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        const int adminPort = 12269;
        using var env = new TempEnvironment();
        DevCaddy.InstallInto(env.Paths);
        using var svc = new PlatformServices(env, adminPort, DevCaddy.MinimalConfig(adminPort, DevCaddy.FreeTcpPort(), Path.Combine(env.Root, "r.log")));
        var host = (ProcessCaddyHost)svc.Get<ICaddyHost>();
        var sync = svc.Get<CaddyEnvironmentSync>();
        try
        {
            Assert.Contains("Caddy is stopped", await sync.ApplyAsync(ct));
            await host.StartAsync(ct);
            Assert.False(host.EnvironmentOutdated);
            Assert.Contains("already runs with this environment", await sync.ApplyAsync(ct));
            var pid = (await host.GetStatusAsync(ct)).ProcessId;

            env.Store.SaveSettings(new BinarySettings { OutboundProxy = "http://127.0.0.1:3128", ProxyCaddyTraffic = true });
            Assert.True(host.EnvironmentOutdated);
            var message = await sync.ApplyAsync(ct);
            Assert.Contains("Caddy was restarted", message);
            Assert.False(host.EnvironmentOutdated);
            var status = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, status.State);
            Assert.NotEqual(pid, status.ProcessId);
            Assert.Contains(svc.Events.Events, e => e.Message == "Caddy was restarted to apply its outbound proxy settings");
            Assert.Contains(svc.Audit.Entries, e => e.StartsWith("restarted caddy"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}

// ============================================================================ binary settings + manager release repo

public class BinarySettingsTests
{
    [Theory]
    [InlineData("my-org/caddy-proxy-manager", "my-org/caddy-proxy-manager")]
    [InlineData(" https://github.com/my-org/caddy-proxy-manager ", "my-org/caddy-proxy-manager")]
    [InlineData("https://github.com/my-org/cpm.git", "my-org/cpm")]
    [InlineData("https://github.com/my-org/cpm/releases", "my-org/cpm")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesReleaseRepo(string? input, string? expected) => Assert.Equal(expected, CaddyBinaryManager.NormalizeReleaseRepo(input));

    [Theory]
    [InlineData("just-a-name")]
    [InlineData("https://gitlab.com/a/b")]
    [InlineData("a/b/c d")]
    [InlineData("-bad/repo")]
    public void RejectsInvalidReleaseRepo(string input) => Assert.Throws<ArgumentException>(() => CaddyBinaryManager.NormalizeReleaseRepo(input));

    [Fact]
    public void ManagerVersionIsSemVer() =>
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$", CaddyBinaryManager.ManagerVersion);

    [Fact]
    public async Task ValidatesAndNormalisesTheSettingsBody()
    {
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12279, "{}");
        var bin = svc.Get<CaddyBinaryManager>();
        var ct = TestContext.Current.CancellationToken;
        var current = new BinarySettings { OutboundProxy = "http://svc:S3cret@proxy:8080", LastCheckedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), LatestKnownVersion = "v2.11.4" };

        var (_, noProxy) = await PlatformEndpoints.ValidateBinarySettingsAsync(new BinarySettings { ProxyCaddyTraffic = true }, current, bin, ct);
        Assert.NotNull(noProxy);
        var (_, badNoProxy) = await PlatformEndpoints.ValidateBinarySettingsAsync(new BinarySettings { NoProxy = "a b!c" }, current, bin, ct);
        Assert.NotNull(badNoProxy);
        var (_, badRepo) = await PlatformEndpoints.ValidateBinarySettingsAsync(new BinarySettings { ManagerReleaseRepo = "nope" }, current, bin, ct);
        Assert.NotNull(badRepo);

        var (ok, error) = await PlatformEndpoints.ValidateBinarySettingsAsync(new BinarySettings
        {
            OutboundProxy = $"http://svc:{Infrastructure.OutboundHttp.RedactedPassword}@proxy:8080", ProxyCaddyTraffic = true,
            NoProxy = "localhost 10.0.0.0/8", ManagerReleaseRepo = "https://github.com/my-org/cpm", CheckIntervalHours = 6,
        }, current, bin, ct);
        Assert.Null(error);
        Assert.Equal("http://svc:S3cret@proxy:8080", ok!.OutboundProxy);
        Assert.True(ok.ProxyCaddyTraffic);
        Assert.Equal("localhost,10.0.0.0/8", ok.NoProxy);
        Assert.Equal("my-org/cpm", ok.ManagerReleaseRepo);
        Assert.Equal(current.LastCheckedAt, ok.LastCheckedAt);
        Assert.Equal("v2.11.4", ok.LatestKnownVersion);
    }
}

// ============================================================================ readiness: streams, ACL, admin API, proxy

public class ReadinessRound2Tests
{
    private static readonly AppPaths Paths = new(Path.Combine(Path.GetTempPath(), "cpm-rules"));

    [Fact]
    public void AddsOneFirewallRulePerEnabledStream()
    {
        var streams = new List<StreamHost>
        {
            new() { Protocol = StreamProtocol.Tcp, ListenPort = 3389, UpstreamHost = "10.0.0.5", UpstreamPort = 3389 },
            new() { Protocol = StreamProtocol.Udp, ListenPort = 1194, UpstreamHost = "10.0.0.6", UpstreamPort = 1194 },
            new() { Protocol = StreamProtocol.Tcp, ListenPort = 2222, Enabled = false },
            new() { Protocol = StreamProtocol.Tcp, ListenPort = 3389, UpstreamHost = "dup", UpstreamPort = 1 },
        };
        var rules = ReadinessService.RequiredRules(Paths, new CaddySettings(), new UiSettings(), 81, streams);
        var tcp = rules.Single(r => r.CheckId == "firewall.stream.tcp3389");
        Assert.Equal("Caddy Proxy Manager - Stream TCP 3389 (TCP-In)", tcp.DisplayName);
        Assert.Equal("TCP", tcp.Protocol);
        Assert.Equal(Paths.CaddyExe, tcp.Program);
        Assert.Equal(AppPaths.CaddyServiceName, tcp.Service);
        Assert.Contains("10.0.0.5:3389", tcp.Purpose);
        var udp = rules.Single(r => r.CheckId == "firewall.stream.udp1194");
        Assert.Equal("Caddy Proxy Manager - Stream UDP 1194 (UDP-In)", udp.DisplayName);
        Assert.DoesNotContain(rules, r => r.Port == 2222);
        Assert.Equal(rules.Count, rules.Select(r => r.CheckId).Distinct().Count());
        Assert.Equal("Caddy Proxy Manager - Stream UDP 53 (UDP-In)", ReadinessService.StreamRuleName(StreamProtocol.Udp, 53));

        // Fix command and GPO script carry the stream rules too.
        Assert.Contains("-DisplayName 'Caddy Proxy Manager - Stream TCP 3389 (TCP-In)'", ReadinessScripts.CreateFirewallRuleCommand(tcp));
        Assert.Contains("-Protocol UDP -LocalPort 1194", ReadinessScripts.CreateFirewallRule(udp));
        var gpo = GpoScriptBuilder.Build(new GpoScriptInput { Domain = "corp.local", ComputerName = "WEB01", Rules = rules, InternalCaUsed = true });
        Assert.Contains("DisplayName = 'Caddy Proxy Manager - Stream TCP 3389 (TCP-In)'; Protocol = 'TCP'; LocalPort = '3389'", gpo);
        Assert.Contains("DisplayName = 'Caddy Proxy Manager - Stream UDP 1194 (UDP-In)'; Protocol = 'UDP'; LocalPort = '1194'", gpo);
        // Safe for Windows PowerShell 5.1 reading a UTF-8 file without BOM.
        Assert.True(gpo.All(c => c < 128), "GPO script contains non-ASCII characters");
    }

    [Fact]
    public void QuotesTypographicApostrophes()
    {
        Assert.Equal("'O''Brien''s'", PowerShellRunner.Quote("O\u2019Brien\u2018s"));
        Assert.Equal("'a -> b'", GpoScriptBuilder.AsciiPunctuation("'a → b'"));
    }

    [Fact]
    public void EvaluatesDataDirectoryAcl()
    {
        var good = DataDirAcl.Evaluate(@"C:\ProgramData\CaddyProxyManager", isProtected: true,
        [
            new AclEntry("S-1-5-18", @"NT AUTHORITY\SYSTEM", true, "FullControl", false),
            new AclEntry("S-1-5-32-544", @"BUILTIN\Administrators", true, "FullControl", false),
        ]);
        Assert.Equal(CheckStatus.Pass, good.Status);
        Assert.False(good.Fixable);
        Assert.Equal("system.datadir", good.Id);

        var inherited = DataDirAcl.Evaluate(@"C:\ProgramData\CaddyProxyManager", isProtected: false,
        [
            new AclEntry("S-1-5-18", @"NT AUTHORITY\SYSTEM", true, "FullControl", true),
            new AclEntry("S-1-5-32-545", @"BUILTIN\Users", true, "ReadAndExecute, Synchronize", true),
            new AclEntry("S-1-5-32-545", @"BUILTIN\Users", true, "CreateDirectories", true),
            new AclEntry("S-1-1-0", "Everyone", false, "FullControl", false),
        ]);
        Assert.Equal(CheckStatus.Warn, inherited.Status);
        Assert.True(inherited.Fixable);
        Assert.Contains("inherits permissions", inherited.Summary);
        Assert.Contains(@"BUILTIN\Users is granted ReadAndExecute, Synchronize (inherited)", inherited.Summary);
        Assert.DoesNotContain("Everyone is granted", inherited.Summary); // deny entries are not a problem
        Assert.Contains("/inheritance:r", inherited.Script);
        Assert.Contains("*S-1-5-32-545", inherited.Script);

        var explicitUsers = DataDirAcl.Evaluate("D:\\data", true, [new AclEntry("S-1-5-11", @"NT AUTHORITY\Authenticated Users", true, "Modify", false)]);
        Assert.Equal(CheckStatus.Warn, explicitUsers.Status);
        Assert.Contains("Authenticated Users is granted Modify", explicitUsers.Summary);
    }

    [Fact]
    public void DescribesTheAdminApiRisk()
    {
        var c = ReadinessService.AdminApiRiskCheck("127.0.0.1:2019", true, "127.0.0.1:2019");
        Assert.Equal("caddy.admin.access", c.Id);
        Assert.Equal(CheckStatus.Info, c.Status);
        Assert.Contains("no authentication", c.Summary);
        Assert.Contains("Remote Desktop", c.Summary);
        Assert.Contains("socket file", ReadinessService.AdminApiRiskCheck("unix//run/caddy.sock", true, "unix//run/caddy.sock").Summary);
    }

    [Fact]
    public async Task ReportsStreamsProxyAndNewChecks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Non-Windows behaviour.");
        using var env = new TempEnvironment();
        var streamPort = DevCaddy.FreeTcpPort();
        env.Store.Col<StreamHost>().Insert(new StreamHost { Protocol = StreamProtocol.Tcp, ListenPort = streamPort, UpstreamHost = "10.0.0.5", UpstreamPort = 3389 });
        env.Store.Col<StreamHost>().Insert(new StreamHost { Protocol = StreamProtocol.Udp, ListenPort = 5353, Enabled = false });
        // A proxy nobody listens on: every proxied probe fails fast and deterministically.
        env.Store.SaveSettings(new BinarySettings { OutboundProxy = "http://127.0.0.1:9", ProxyCaddyTraffic = true });
        using var svc = new PlatformServices(env, DevCaddy.FreeTcpPort(), "{}");
        var report = await svc.Get<IReadinessService>().RunAsync(TestContext.Current.CancellationToken);
        string Dump() => string.Join("\n", report.Checks.Select(c => $"{c.Id} [{c.Status}] {c.Summary}"));

        var port = report.Checks.Single(c => c.Id == $"ports.stream.tcp{streamPort}");
        Assert.Equal(CheckStatus.Pass, port.Status);
        Assert.Contains("stream", port.Title);
        Assert.DoesNotContain(report.Checks, c => c.Id.Contains("5353"));
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "system.datadir").Status);
        Assert.Equal(CheckStatus.Info, report.Checks.Single(c => c.Id == "caddy.admin.access").Status);
        var caddyProxy = report.Checks.Single(c => c.Id == "connectivity.caddyproxy");
        Assert.StartsWith("Caddy uses the outbound proxy http://127.0.0.1:9", caddyProxy.Summary);
        var le = report.Checks.Single(c => c.Id == "connectivity.letsencrypt");
        Assert.Contains("through the outbound proxy http://127.0.0.1:9", le.Summary);
        Assert.Equal(CheckStatus.Warn, le.Status); // no ACME hosts → not required
        Assert.Equal(report.Checks.Count, report.Checks.Select(c => c.Id).Distinct().Count());
        Assert.True(report.Checks.Count > 15, Dump());
        Assert.Contains("Caddy Proxy Manager - Stream TCP", svc.Get<IReadinessService>().BuildGpoScript());
    }
}

// ============================================================================ CLI: configure

public class ConfigureCliTests
{
    private static readonly IPAddress[] Local = [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fe80::1%3"), IPAddress.Loopback];

    [Fact]
    public void ParsesOptions()
    {
        var (o, e) = PlatformCli.ParseConfigureArgs(["--ui-port", "8081", "--bind", "10.0.0.5", "--ui-https", "ON", "--reset-ui"], Local);
        Assert.Null(e);
        Assert.Equal(8081, o!.UiPort);
        Assert.Equal("10.0.0.5", o.Bind);
        Assert.True(o.Https);
        Assert.True(o.ResetUi);
        Assert.False(PlatformCli.ParseConfigureArgs([], Local).Options!.Any);
        Assert.False(PlatformCli.ParseConfigureArgs(["--ui-https", "off"], Local).Options!.Https);

        Assert.Contains("'on' or 'off'", PlatformCli.ParseConfigureArgs(["--ui-https", "maybe"], Local).Error);
        Assert.Contains("between 1 and 65535", PlatformCli.ParseConfigureArgs(["--ui-port", "0"], Local).Error);
        Assert.Contains("Unknown or incomplete option '--bind'", PlatformCli.ParseConfigureArgs(["--bind"], Local).Error);
        Assert.Contains("Unknown", PlatformCli.ParseConfigureArgs(["--frobnicate"], Local).Error);
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("fe80::1", true)]
    [InlineData("10.0.0.6", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("server01", false)]
    public void AcceptsOnlyLocalBindAddresses(string bind, bool ok)
    {
        var error = PlatformCli.CheckBindAddress(bind, Local);
        Assert.Equal(ok, error is null);
        if (!ok && IPAddress.TryParse(bind, out _)) Assert.Contains("10.0.0.5", error);
    }

    [Fact]
    public void ConfigureWritesAndResetsTheUiListener()
    {
        var root = Path.Combine(Path.GetTempPath(), "cpm-configure-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(root);
        try
        {
            Assert.Equal(0, PlatformCli.Configure(["--ui-port", "8081", "--bind", "127.0.0.1", "--ui-https", "on"], paths, Local));
            using (var store = new LiteStore(paths))
            {
                var ui = store.GetSettings<UiSettings>();
                Assert.Equal(8081, ui.Port);
                Assert.Equal("127.0.0.1", ui.BindAddress);
                Assert.True(ui.HttpsEnabled);
                ui.RedirectHttpToHttps = true;
                store.SaveSettings(ui);
            }
            Assert.Equal(2, PlatformCli.Configure(["--bind", "203.0.113.7"], paths, Local));
            Assert.Equal(2, PlatformCli.Configure(["--ui-port", "8443"], paths, Local)); // clashes with the HTTPS port

            Assert.Equal(0, PlatformCli.Configure(["--reset-ui"], paths, Local));
            using (var store = new LiteStore(paths))
            {
                var ui = store.GetSettings<UiSettings>();
                Assert.Equal(81, ui.Port);
                Assert.Equal("0.0.0.0", ui.BindAddress);
                Assert.False(ui.HttpsEnabled);
                Assert.False(ui.RedirectHttpToHttps);
            }
            Assert.Equal(0, PlatformCli.Configure([], paths, Local));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HelpListsConfigure()
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, await PlatformCli.TryRunAsync(["help"]));
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Contains("configure [--ui-port N] [--bind ADDR] [--ui-https on|off] [--reset-ui]", output.ToString());
    }
}

// ============================================================================ GitHub checks (network)

[Trait("Category", "Network")]
public class ManagerUpdateNetworkTests
{
    [Fact]
    public async Task ReportsManagerUpdatesOncePerVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        // Any public repository with releases works; Caddy's own tags (v2.x) are "newer" than the manager's 1.x.
        env.Store.SaveSettings(new BinarySettings { ManagerReleaseRepo = "caddyserver/caddy" });
        using var svc = new PlatformServices(env, 12289, "{}");
        var checker = svc.Get<UpdateChecker>();

        var version = await checker.CheckManagerAsync(ct);
        Assert.NotNull(version);
        Assert.DoesNotContain("v", version);
        await checker.CheckManagerAsync(ct);
        var events = svc.Events.Events.Where(e => e.Key == $"manager-update-available:{version}").ToList();
        Assert.Single(events);
        Assert.Equal("updateAvailable", events[0].AlertRule);

        var overview = await svc.Get<ICaddyBinaryManager>().GetOverviewAsync(ct);
        Assert.Equal(CaddyBinaryManager.ManagerVersion, overview.ManagerVersion);
        Assert.Equal(version, overview.ManagerLatestVersion);
        Assert.StartsWith("https://github.com/caddyserver/caddy/releases/tag/", overview.ManagerLatestUrl);
        Assert.True(overview.ManagerUpdateAvailable);
        Assert.False(overview.CanRollback);
        Assert.Null(overview.PreviousVersion);

        env.Store.SaveSettings(new BinarySettings { ManagerReleaseRepo = "caddyserver/this-repo-does-not-exist-cpm" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.Get<CaddyBinaryManager>().GetManagerLatestAsync(force: true, ct));
        Assert.Contains("no published release", ex.Message);
    }
}
