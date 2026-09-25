using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Infrastructure;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// The integration tests must run against the Caddy release the product is verified with (<see cref="CaddyVersion.Tested"/>),
/// not whatever GitHub calls "latest" today: Caddy changes behaviour between minor versions
/// (https://github.com/caddyserver/caddy/releases — v2.11.0 changed the Host header sent to HTTPS upstreams).
///
/// Ways this can fail (written before the fix):
///  1. CI downloads releases/latest, so the suites test a different Caddy than the one the product was verified with.
///  2. .github/scripts/get-caddy.ps1 extracts the version from CaddyVersion.cs with a regex that no longer matches the
///     declaration after a refactor, and CI silently falls back to something else (or fails with an unclear error).
///  3. The downloaded binary reports another version than the tag (wrong asset, cached file).
///  4. The local development binary (.dev/bin/caddy) is older/newer than the pin, so a developer's green run proves nothing.
///  5. `caddy version` output changes ("v2.11.4 h1:..."), so the comparison always fails or always passes.
/// </summary>
[Trait("Category", "Caddy")]
public class CaddyPinE2ETests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CaddyManager.sln"))) dir = dir.Parent;
        Assert.SkipWhen(dir is null, "Repository root not found.");
        return dir!.FullName;
    }

    [Fact]
    public async Task IntegrationTestsUseTheTestedCaddyRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepoRoot();
        var report = E2EArtifacts.Report(nameof(IntegrationTestsUseTheTestedCaddyRelease));
        report["tested"] = CaddyVersion.Tested;
        Assert.True(CaddyVersion.TryParse(CaddyVersion.Tested, out var tested));
        Assert.False(tested!.IsPreRelease);

        // (2) The CI script finds exactly this constant with its own regex.
        var script = File.ReadAllText(Path.Combine(root, ".github", "scripts", "get-caddy.ps1"));
        var pattern = Regex.Match(script, @"\[regex\]::Match\(\$source, '(?<p>[^']+)'\)").Groups["p"].Value;
        Assert.False(string.IsNullOrEmpty(pattern), "get-caddy.ps1 no longer reads CaddyVersion.Tested with [regex]::Match.");
        var source = File.ReadAllText(Path.Combine(root, "src", "CaddyManager.Platform", "Binary", "CaddyVersion.cs"));
        var found = Regex.Match(source, pattern);
        Assert.True(found.Success, $"get-caddy.ps1's pattern {pattern} does not match CaddyVersion.cs.");
        Assert.Equal(CaddyVersion.Tested, found.Groups[1].Value);
        report["scriptPattern"] = pattern;

        // (1) The workflow's test job uses the pinned download, not releases/latest.
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        Assert.Contains("get-caddy.ps1 -Which tested", workflow);

        // (3)(4)(5) The binary the suites run reports the pinned version (skipped in the informational "latest" job).
        var expected = Environment.GetEnvironmentVariable("CPM_EXPECT_CADDY_VERSION") is { Length: > 0 } e ? e : CaddyVersion.Tested;
        report["expected"] = expected;
        var caddy = DevCaddy.Find();
        if (caddy is not null && Environment.GetEnvironmentVariable("CPM_CADDY_CHANNEL") != "latest")
        {
            var r = await ProcessRunner.RunAsync(caddy, ["version"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) }, ct);
            report["binary"] = caddy;
            report["caddyVersionOutput"] = r.StdOut.Trim();
            Assert.Equal(0, r.ExitCode);
            Assert.StartsWith(expected + " ", r.StdOut.Trim() + " ");
            Assert.Equal(CaddyVersion.Tested, expected);
        }
        else
        {
            report["binary"] = caddy is null ? null : JsonValue.Create("skipped (latest channel)");
        }
        E2EArtifacts.Write("caddy-pin.json", report);
    }
}
