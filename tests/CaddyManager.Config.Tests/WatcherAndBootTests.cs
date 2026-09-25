using CaddyManager.Config.Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

internal sealed class CountingConfigService : ICaddyConfigService
{
    public List<string> Reasons { get; } = new();
    public string BuildConfigJson() => "{}";
    public Task<ApplyResult> ApplyAsync(string reason, CancellationToken ct = default)
    {
        Reasons.Add(reason);
        return Task.FromResult(new ApplyResult { Success = true });
    }
    public Task<ValidationResult> ValidateAsync(string json, CancellationToken ct = default) => Task.FromResult(new ValidationResult { Valid = true });
    public void EnsureBootConfig() { }
}

public sealed class WatcherAndBootTests
{
    [Fact]
    public async Task File_path_certificate_change_refreshes_metadata_and_reapplies()
    {
        using var env = new TempEnv();
        using var first = TestCerts.SelfSigned(["renew.example.com"]);
        var certPath = Path.Combine(env.Dir, "renew.pem");
        var keyPath = Path.Combine(env.Dir, "renew.key");
        File.WriteAllText(certPath, first.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(first));
        env.Store.Col<Certificate>().Insert(new Certificate { Id = "c1", Name = "renew", Source = CertificateSource.FilePath, CertPath = certPath, KeyPath = keyPath, Thumbprint = first.Thumbprint });
        env.Store.Col<SiteHost>().Insert(new SiteHost { Id = "h1", Domains = ["renew.example.com"], Tls = TlsMode.Custom, CertificateId = "c1" });

        var config = new CountingConfigService();
        using var watcher = new CertificateWatcher(env.Store, config, TestSync.Create(env), NullLogger<CertificateWatcher>.Instance);
        Assert.False(await watcher.CheckAsync(CancellationToken.None)); // initial snapshot
        Assert.False(await watcher.CheckAsync(CancellationToken.None)); // unchanged

        using var renewed = TestCerts.SelfSigned(["renew.example.com", "www.renew.example.com"]);
        File.WriteAllText(certPath, renewed.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(renewed));
        File.SetLastWriteTimeUtc(certPath, DateTime.UtcNow.AddMinutes(1));

        Assert.True(await watcher.CheckAsync(CancellationToken.None));
        Assert.Single(config.Reasons);
        Assert.Contains("renew", config.Reasons[0]);
        var stored = env.Store.Col<Certificate>().FindById("c1");
        Assert.Equal(renewed.Thumbprint, stored.Thumbprint);
        Assert.Contains("www.renew.example.com", stored.Subjects);
    }

    [Fact]
    public async Task Unused_certificate_change_only_refreshes_metadata()
    {
        using var env = new TempEnv();
        using var first = TestCerts.SelfSigned(["idle.example.com"]);
        var certPath = Path.Combine(env.Dir, "idle.pem");
        var keyPath = Path.Combine(env.Dir, "idle.key");
        File.WriteAllText(certPath, first.ExportCertificatePem());
        File.WriteAllText(keyPath, TestCerts.KeyPem(first));
        env.Store.Col<Certificate>().Insert(new Certificate { Id = "c1", Name = "idle", Source = CertificateSource.FilePath, CertPath = certPath, KeyPath = keyPath });
        var config = new CountingConfigService();
        using var watcher = new CertificateWatcher(env.Store, config, TestSync.Create(env), NullLogger<CertificateWatcher>.Instance);
        await watcher.CheckAsync(CancellationToken.None);

        File.Delete(keyPath);
        Assert.False(await watcher.CheckAsync(CancellationToken.None));
        Assert.Empty(config.Reasons);
    }

    [Fact]
    public void Boot_config_is_written_once()
    {
        using var s = new ConfigServices(installBinary: false);
        s.Config.EnsureBootConfig();
        var first = File.ReadAllText(s.Paths.CaddyConfigFile);
        Assert.Contains("\"admin\"", first);
        Assert.Contains("\"storage\"", first);
        Assert.DoesNotContain("\"apps\"", first);
        File.WriteAllText(s.Paths.CaddyConfigFile, "{\"custom\":true}");
        s.Config.EnsureBootConfig();
        Assert.Equal("{\"custom\":true}", File.ReadAllText(s.Paths.CaddyConfigFile));
    }

    [CaddyFact]
    public async Task Boot_config_passes_caddy_validate()
    {
        using var s = new ConfigServices(installBinary: true);
        s.Config.EnsureBootConfig();
        var v = await s.Config.ValidateAsync(File.ReadAllText(s.Paths.CaddyConfigFile));
        Assert.True(v.Valid, v.Error);
    }

    [Fact]
    public void Admin_base_url_parsing()
    {
        Assert.Equal("http://127.0.0.1:2019", Admin.CaddyAdminClient.BaseUrlFor("127.0.0.1:2019"));
        Assert.Equal("http://localhost:2020", Admin.CaddyAdminClient.BaseUrlFor("localhost:2020"));
        Assert.Equal("http://[::1]:2019", Admin.CaddyAdminClient.BaseUrlFor("[::1]:2019"));
        Assert.Equal("http://127.0.0.1:2019", Admin.CaddyAdminClient.BaseUrlFor(":2019"));
        Assert.Equal("http://127.0.0.1:2019", Admin.CaddyAdminClient.BaseUrlFor("tcp/127.0.0.1:2019"));
        Assert.Throws<CaddyAdminException>(() => Admin.CaddyAdminClient.BaseUrlFor("unix//run/caddy.sock"));
    }

    [Fact]
    public void Admin_error_and_upstream_parsing()
    {
        Assert.Equal("boom", Admin.CaddyAdminClient.ExtractError("{\"error\":\"boom\"}"));
        Assert.Equal("plain text", Admin.CaddyAdminClient.ExtractError("plain text\n"));
        var ups = Admin.CaddyAdminClient.ParseUpstreams("""[{"address":"b:80","num_requests":2,"fails":1},{"address":"a:80","num_requests":0,"fails":0}]""");
        Assert.Equal(["a:80", "b:80"], ups.Select(u => u.Address).ToArray());
        Assert.True(ups[0].Healthy);
        Assert.False(ups[1].Healthy);
        Assert.Equal(2, ups[1].NumRequests);
    }
}
