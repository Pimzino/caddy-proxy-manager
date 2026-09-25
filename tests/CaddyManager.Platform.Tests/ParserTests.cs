using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Windows;

namespace CaddyManager.Platform.Tests;

public class CaddyVersionTests
{
    [Theory]
    [InlineData("v2.11.4", 2, 11, 4, null)]
    [InlineData("2.11.4", 2, 11, 4, null)]
    [InlineData(" v2.10.0-beta.3 ", 2, 10, 0, "beta.3")]
    [InlineData("v2.9.0-rc.1+build.5", 2, 9, 0, "rc.1")]
    [InlineData("v3.0", 3, 0, 0, null)]
    public void Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(CaddyVersion.TryParse(text, out var v));
        Assert.Equal(new CaddyVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("(devel)")]
    [InlineData("v2")]
    [InlineData("v2.x.1")]
    public void RejectsInvalid(string text) => Assert.False(CaddyVersion.TryParse(text, out _));

    [Fact]
    public void OrdersBySemVerPrecedence()
    {
        string[] ordered =
        [
            "v2.9.1", "v2.10.0-alpha.1", "v2.10.0-beta", "v2.10.0-beta.2", "v2.10.0-beta.11", "v2.10.0-rc.1", "v2.10.0",
            "v2.10.1", "v2.11.0-beta.1", "v2.11.4", "v10.0.0",
        ];
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(CaddyVersion.Compare(ordered[i], ordered[i + 1]) < 0, $"{ordered[i]} < {ordered[i + 1]}");
            Assert.True(CaddyVersion.Compare(ordered[i + 1], ordered[i]) > 0, $"{ordered[i + 1]} > {ordered[i]}");
        }
        Assert.Equal(0, CaddyVersion.Compare("v2.11.4", "2.11.4"));
        Assert.Equal(0, CaddyVersion.Compare("v2.11.4+meta", "v2.11.4"));
    }

    [Fact]
    public void IsNewerHandlesUnknownInstalledVersions()
    {
        Assert.True(CaddyVersion.IsNewer("v2.11.4", "v2.11.3"));
        Assert.False(CaddyVersion.IsNewer("v2.11.4", "v2.11.4"));
        Assert.False(CaddyVersion.IsNewer("v2.11.4-rc.1", "v2.11.4"));
        Assert.True(CaddyVersion.IsNewer("v2.11.4", "(devel)"));
        Assert.False(CaddyVersion.IsNewer("garbage", "v2.0.0"));
    }

    [Fact]
    public void FormatsTagAndBare()
    {
        var v = CaddyVersion.Parse("2.11.4");
        Assert.Equal("v2.11.4", v.ToString());
        Assert.Equal("2.11.4", v.Bare);
        Assert.Throws<FormatException>(() => CaddyVersion.Parse("nope"));
    }
}

public class CaddyOutputParserTests
{
    [Fact]
    public void ParsesVersionOutputFromRealBinary()
    {
        Assert.Equal("v2.11.4", CaddyOutputParser.ParseVersion(Fixture.Read("caddy-version.txt")));
        Assert.Equal("v2.10.0-beta.1", CaddyOutputParser.ParseVersion("\nv2.10.0-beta.1 h1:abc=\n"));
        Assert.Equal("(devel)", CaddyOutputParser.ParseVersion("(devel)\n"));
        Assert.Null(CaddyOutputParser.ParseVersion("   \n"));
    }

    [Fact]
    public void ParsesStandardListModulesText()
    {
        var modules = CaddyOutputParser.ParseModulesText(Fixture.Read("list-modules-packages-standard.txt"));
        Assert.Equal(132, modules.Count);
        Assert.All(modules, m => Assert.True(m.Standard));
        Assert.All(modules, m => Assert.Equal(CaddyOutputParser.StandardPackage, m.Package));
        Assert.Contains(modules, m => m.Name == "http.handlers.reverse_proxy");
        Assert.Empty(CaddyOutputParser.PluginPackages(modules));
    }

    [Fact]
    public void ParsesPluginBuildListModulesText()
    {
        var modules = CaddyOutputParser.ParseModulesText(Fixture.Read("list-modules-packages-l4.txt"));
        Assert.Equal(132 + 43, modules.Count);
        Assert.Equal(43, modules.Count(m => !m.Standard));
        Assert.Contains(modules, m => m.Name == "layer4" && !m.Standard && m.Package == "github.com/mholt/caddy-l4");
        Assert.Equal(["github.com/mholt/caddy-l4"], CaddyOutputParser.PluginPackages(modules));
    }

    [Fact]
    public void ParsesListModulesJson()
    {
        var standard = CaddyOutputParser.ParseModulesJson(Fixture.Read("list-modules-json-standard.json"));
        Assert.Equal(132, standard.Count);
        Assert.All(standard, m => Assert.True(m.Standard));

        var l4 = CaddyOutputParser.ParseModulesJson(Fixture.Read("list-modules-json-l4.json"));
        Assert.Equal(175, l4.Count);
        var layer4 = Assert.Single(l4, m => m.Name == "layer4");
        Assert.False(layer4.Standard);
        Assert.StartsWith("v", layer4.Version);
        Assert.Equal(["github.com/mholt/caddy-l4"], CaddyOutputParser.PluginPackages(l4));
    }

    [Fact]
    public void TextParserFallsBackToPackageWhenFooterMissing()
    {
        var modules = CaddyOutputParser.ParseModulesText("http.handlers.x github.com/caddyserver/caddy/v2\nfoo.bar github.com/acme/plugin\n");
        Assert.True(modules.Single(m => m.Name == "http.handlers.x").Standard);
        Assert.False(modules.Single(m => m.Name == "foo.bar").Standard);
    }

    [Fact]
    public void ParsesChecksumsFile()
    {
        var sums = CaddyOutputParser.ParseChecksums(Fixture.Read("caddy_2.11.4_checksums.txt"));
        Assert.True(sums.Count > 20);
        Assert.Matches("^[0-9a-f]{128}$", sums["caddy_2.11.4_windows_amd64.zip"]);
        Assert.Matches("^[0-9a-f]{128}$", sums["caddy_2.11.4_mac_arm64.tar.gz"]);
        Assert.Matches("^[0-9a-f]{128}$", sums["caddy_2.11.4_linux_amd64.tar.gz"]);
        Assert.Equal("3b7842961de67b5496822f546875353face5b69757bc88a58664946c062a5555a6208368eeec6b3d992d33a809e7376e9c8b39d120e97877cbd3ab3885e7aa8c",
            sums["caddy_2.11.4_buildable-artifact.tar.gz"]);
    }

    [Fact]
    public void ParsesGitHubRelease()
    {
        var r = CaddyOutputParser.ParseGitHubRelease(Fixture.Read("github-release-latest.json"));
        Assert.Equal("v2.11.4", r.Version);
        Assert.Equal("https://github.com/caddyserver/caddy/releases/tag/v2.11.4", r.Url);
        Assert.Equal(new DateTime(2026, 6, 3, 6, 52, 22, DateTimeKind.Utc), r.PublishedAt);
        Assert.Contains("Changelog", r.Notes);

        var truncated = CaddyOutputParser.ParseGitHubRelease("""{"tag_name":"v2.0.0","body":"0123456789abcdef"}""", maxNotesLength: 10);
        Assert.StartsWith("0123456789", truncated.Notes);
        Assert.EndsWith("…", truncated.Notes);
        Assert.Throws<FormatException>(() => CaddyOutputParser.ParseGitHubRelease("{}"));
    }

    [Fact]
    public void ParsesPackageCatalog()
    {
        var list = CaddyOutputParser.ParsePackageCatalog(Fixture.Read("packages.json"));
        Assert.DoesNotContain(list, p => p.Path == "example.com/unlisted/plugin");
        var noModules = Assert.Single(list, p => p.Path == "example.com/nomodules/plugin");
        Assert.Empty(noModules.Modules);
        var l4 = Assert.Single(list, p => p.Path == "github.com/mholt/caddy-l4");
        Assert.Contains("layer4", l4.Modules);
        Assert.True(l4.Downloads > 0);
        Assert.StartsWith("https://", l4.Repo);
        Assert.Throws<FormatException>(() => CaddyOutputParser.ParsePackageCatalog("""{"status_code":500,"result":[]}"""));
    }

    [Theory]
    [InlineData("github.com/mholt/caddy-l4", "github.com/mholt/caddy-l4")]
    [InlineData("github.com/mholt/caddy-l4@v0.1.2", "github.com/mholt/caddy-l4")]
    [InlineData(" github.com/a/b@master ", "github.com/a/b")]
    public void StripsPackageVersion(string input, string expected) =>
        Assert.Equal(expected, CaddyOutputParser.PackageWithoutVersion(input));
}

public class CaddyPlatformTests
{
    [Fact]
    public void BuildsReleaseAssetNames()
    {
        var v = CaddyVersion.Parse("v2.11.4");
        Assert.Equal("caddy_2.11.4_windows_amd64.zip", new CaddyPlatform("windows", "amd64").ReleaseAssetName(v));
        Assert.Equal("caddy_2.11.4_mac_arm64.tar.gz", new CaddyPlatform("darwin", "arm64").ReleaseAssetName(v));
        Assert.Equal("caddy_2.11.4_linux_armv7.tar.gz", new CaddyPlatform("linux", "arm").ReleaseAssetName(v));
        Assert.Equal("caddy_2.11.4_checksums.txt", CaddyPlatform.ChecksumsAssetName(v));
        Assert.Equal("https://github.com/caddyserver/caddy/releases/download/v2.11.4/caddy_2.11.4_windows_amd64.zip",
            CaddyPlatform.ReleaseDownloadUrl(v, "caddy_2.11.4_windows_amd64.zip"));
    }

    [Fact]
    public void BuildsBuildServerUrl()
    {
        var url = new CaddyPlatform("windows", "amd64").BuildServerUrl(["github.com/mholt/caddy-l4", "github.com/caddy-dns/cloudflare"]);
        Assert.Equal("https://caddyserver.com/api/download?os=windows&arch=amd64&p=github.com%2Fmholt%2Fcaddy-l4&p=github.com%2Fcaddy-dns%2Fcloudflare", url);
    }

    [Fact]
    public void CurrentPlatformMatchesTheSampleAssetsOnThisMachine()
    {
        var sums = CaddyOutputParser.ParseChecksums(Fixture.Read("caddy_2.11.4_checksums.txt"));
        Assert.True(sums.ContainsKey(CaddyPlatform.Current.ReleaseAssetName(CaddyVersion.Parse("v2.11.4"))),
            $"No release asset for {CaddyPlatform.Current}");
    }
}

public class ScOutputParserTests
{
    [Fact]
    public void ParsesRunningQueryEx()
    {
        var q = ScOutputParser.ParseQueryEx(Fixture.Read("sc-queryex-running.txt"));
        Assert.NotNull(q);
        Assert.Equal(4, q.State);
        Assert.Equal("RUNNING", q.StateName);
        Assert.Equal(4712, q.ProcessId);
        Assert.Equal(0, q.Win32ExitCode);
        Assert.Equal(0x10, q.ServiceType);
        Assert.True(q.IsOwnProcess);
    }

    [Fact]
    public void SharedSvchostServicesAreNotOwnProcess()
    {
        var q = ScOutputParser.ParseQueryEx("""

            SERVICE_NAME: W32Time
                    TYPE               : 20  WIN32_SHARE_PROCESS
                    STATE              : 4  RUNNING
                                            (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
                    WIN32_EXIT_CODE    : 0  (0x0)
                    SERVICE_EXIT_CODE  : 0  (0x0)
                    CHECKPOINT         : 0x0
                    WAIT_HINT          : 0x0
                    PID                : 1536
                    FLAGS              :
            """);
        Assert.NotNull(q);
        Assert.Equal(0x20, q.ServiceType);
        Assert.False(q.IsOwnProcess);
        Assert.Equal(1536, q.ProcessId);
    }

    [Fact]
    public void ParsesStoppedQueryEx()
    {
        var q = ScOutputParser.ParseQueryEx(Fixture.Read("sc-queryex-stopped.txt"));
        Assert.NotNull(q);
        Assert.Equal(1, q.State);
        Assert.Null(q.ProcessId);
        Assert.Equal(1067, q.Win32ExitCode);
    }

    [Fact]
    public void ReturnsNullForMissingService() =>
        Assert.Null(ScOutputParser.ParseQueryEx("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service.\r\n"));

    [Fact]
    public void ParsesQFailure()
    {
        var f = ScOutputParser.ParseQFailure(Fixture.Read("sc-qfailure.txt"));
        Assert.NotNull(f);
        Assert.Equal(86400, f.ResetPeriodSeconds);
        Assert.Equal(3, f.RestartActions);
        Assert.Equal([5000, 5000, 30000], f.Actions.Select(a => a.DelayMs));

        var none = ScOutputParser.ParseQFailure(Fixture.Read("sc-qfailure-none.txt"));
        Assert.NotNull(none);
        Assert.Equal(0, none.RestartActions);
    }
}
