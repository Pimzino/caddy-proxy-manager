using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// SEC-1 (Platform half): Caddy's start errors reach every viewer (CaddyStatus.LastError, the start endpoint's error)
/// and quote credentials — the real Caddy with the Cloudflare plugin prints "API token '&lt;token&gt;' appears invalid"
/// when it cannot provision the DNS provider. The host passes that text through the Config module's ISecretScrubber
/// (a stand-in that masks the token here; its rules are tested in the Config tests). Artifact: caddy-start-error-scrub.json.
///
/// Ways it could fail:
/// (1) Caddy no longer quotes the token (control: caddy.log on disk, which only administrators' tools read, has it);
/// (2) the exception thrown by StartAsync, or the status' LastError, contains the token;
/// (3) CaddyHostSupport.TailLog (used for the Windows service's start errors) returns the token;
/// (4) scrubbing hides the reason (the provider and "appears invalid" must stay readable).
/// </summary>
public sealed class CaddyStartErrorScrubE2ETests
{
    private const string Token = "not a token! cpm-secret-4711";

    private sealed class TokenScrubber : ISecretScrubber
    {
        public string Scrub(string text) => text.Replace(Token, "***", StringComparison.Ordinal);
    }

    /// <summary>A Caddy binary with dns.providers.cloudflare (CPM_TEST_CADDY_PLUGINS or .dev/bin/caddy-plugins).</summary>
    private static string? CloudflareCaddy()
    {
        var env = Environment.GetEnvironmentVariable("CPM_TEST_CADDY_PLUGINS");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var name = OperatingSystem.IsWindows() ? "caddy-plugins.exe" : "caddy-plugins";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".dev", "bin", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    [Trait("Category", "Caddy")]
    public async Task Caddy_start_errors_quoting_a_dns_token_are_scrubbed_before_they_reach_the_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var src = CloudflareCaddy();
        Assert.SkipWhen(src is null, "No Caddy binary with dns.providers.cloudflare (CPM_TEST_CADDY_PLUGINS or .dev/bin/caddy-plugins).");
        var report = E2EArtifacts.Report(nameof(Caddy_start_errors_quoting_a_dns_token_are_scrubbed_before_they_reach_the_status));
        using var env = new TempEnvironment();
        Directory.CreateDirectory(env.Paths.CaddyBinDir);
        File.Copy(src!, env.Paths.CaddyExe, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(env.Paths.CaddyExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var adminPort = DevCaddy.FreeTcpPort();
        var config = new JsonObject
        {
            ["admin"] = new JsonObject { ["listen"] = $"127.0.0.1:{adminPort}" },
            ["apps"] = new JsonObject
            {
                ["tls"] = new JsonObject
                {
                    ["automation"] = new JsonObject
                    {
                        ["policies"] = new JsonArray(new JsonObject
                        {
                            ["subjects"] = new JsonArray("scrub.example.com"),
                            ["issuers"] = new JsonArray(new JsonObject
                            {
                                ["module"] = "acme",
                                ["challenges"] = new JsonObject { ["dns"] = new JsonObject { ["provider"] = new JsonObject { ["name"] = "cloudflare", ["api_token"] = Token } } },
                            }),
                        }),
                    },
                },
            },
        };
        using var svc = new PlatformServices(env, adminPort, config.ToJsonString(), s => s.AddSingleton<ISecretScrubber>(new TokenScrubber()));
        var host = svc.Get<ICaddyHost>();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(ct));
            var status = await host.GetStatusAsync(ct);
            var tail = svc.Get<CaddyHostSupport>().TailLog(20);
            var onDisk = File.ReadAllText(env.Paths.CaddyProcessLog);
            report["startError"] = ex.Message;
            report["lastError"] = status.LastError;
            report["tailLog"] = tail;
            report["caddyLogHasToken"] = onDisk.Contains(Token, StringComparison.Ordinal);
            Assert.Contains(Token, onDisk); // (1)
            Assert.DoesNotContain("cpm-secret-4711", ex.Message); // (2)
            Assert.DoesNotContain("cpm-secret-4711", status.LastError ?? "");
            Assert.DoesNotContain("cpm-secret-4711", tail); // (3)
            Assert.Contains("appears invalid", ex.Message + status.LastError); // (4)
            Assert.Contains("API token '***'", tail);
            report["passed"] = true;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            E2EArtifacts.Write("caddy-start-error-scrub.json", report);
        }
    }
}
