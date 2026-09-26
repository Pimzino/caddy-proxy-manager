using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Platform.Tests;

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].FirstOrDefault() ?? "admin";
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>Platform (+ Core job) endpoints hosted in memory with the real binary manager and the child-process Caddy host.</summary>
public sealed class PlatformApiHost : IAsyncDisposable
{
    public TempEnvironment Env { get; } = new();
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public FakeAuditLog Audit { get; } = new();
    public FakeEventSink Events { get; } = new();

    public PlatformApiHost(int adminPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCore(Env.Paths, Env.Store);
        builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<IAuditLog>(Audit);
        builder.Services.AddSingleton<IEventSink>(Events);
        builder.Services.AddSingleton<ICaddyAdminClient>(new FakeAdminClient($"http://127.0.0.1:{adminPort}"));
        builder.Services.AddSingleton<ICaddyConfigService>(new FakeConfigService(Env.Paths,
            DevCaddy.MinimalConfig(adminPort, DevCaddy.FreeTcpPort(), Path.Combine(Env.Root, "caddy-runtime.log"))));
        var before = builder.Services.Count;
        builder.Services.AddPlatformModule();
        // No bootstrapper / update checker in endpoint tests (they would install and start Caddy on their own).
        foreach (var d in builder.Services.Skip(before).Where(d => d.ServiceType == typeof(IHostedService)).ToList())
            builder.Services.Remove(d);
        builder.Services.AddSingleton<ICaddyHost>(sp => ActivatorUtilities.CreateInstance<Hosting.ProcessCaddyHost>(sp));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Viewer, p => p.RequireAuthenticatedUser());
            o.AddPolicy(Policies.Operator, p => p.RequireRole("operator", "admin"));
            o.AddPolicy(Policies.Admin, p => p.RequireRole("admin"));
        });
        App = builder.Build();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapCoreEndpoints();
        App.MapPlatformEndpoints();
        App.StartAsync().GetAwaiter().GetResult();
        Client = App.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-CPM-Request", "1");
    }

    public async Task<JobInfo> WaitForJobAsync(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = await Client.GetFromJsonAsync<JobInfo>($"/api/jobs/{id}", JsonDefaults.Api);
            if (job!.State != JobState.Running) return job;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Job {id} did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.Services.GetRequiredService<ICaddyHost>().StopAsync(CancellationToken.None); }
        catch (Exception) { /* not started */ }
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Env.Dispose();
    }
}

[Trait("Category", "Caddy")]
public class PlatformEndpointTests
{
    private static MultipartFormDataContent Form(string file, string name, string? sha = null)
    {
        var form = new MultipartFormDataContent();
        var content = new StreamContent(File.OpenRead(file));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        if (sha is not null) form.Add(new StringContent(sha), "sha512");
        return form;
    }

    [Fact]
    public async Task UploadInstallsCaddyAndRollbackNeedsAPreviousBinary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: installs the development binary as this server's Windows Caddy.");
        var dev = DevCaddy.Find();
        Assert.SkipWhen(dev is null, "Development Caddy binary (.dev/bin/caddy or CM_TEST_CADDY) not found.");
        var ct = TestContext.Current.CancellationToken;
        await using var api = new PlatformApiHost(12299);
        var c = api.Client;

        // Admin only.
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/caddy/binary/upload") { Content = Form(dev!, "caddy") })
        {
            req.Headers.Add("X-Test-Role", "operator");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req, ct)).StatusCode);
        }

        var notMultipart = await c.PostAsync("/api/caddy/binary/upload", new ByteArrayContent([1, 2, 3]), ct);
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
        Assert.Contains("multipart/form-data", await notMultipart.Content.ReadAsStringAsync(ct));

        var wrongSha = await c.PostAsync("/api/caddy/binary/upload", Form(dev!, "caddy", new string('0', 128)), ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongSha.StatusCode);
        var problem = JsonDocument.Parse(await wrongSha.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Contains("SHA-512 mismatch", problem.GetProperty("errors").GetProperty("sha512")[0].GetString());

        var noPrevious = await c.PostAsync("/api/caddy/binary/rollback", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, noPrevious.StatusCode);
        Assert.Contains("no previous Caddy binary", await noPrevious.Content.ReadAsStringAsync(ct));

        string sha;
        await using (var fs = File.OpenRead(dev!)) sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA512.HashDataAsync(fs, ct));
        var upload = await c.PostAsync("/api/caddy/binary/upload", Form(dev!, CaddyPlatform(), sha), ct);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var job = (await upload.Content.ReadFromJsonAsync<JobInfo>(JsonDefaults.Api, ct))!;
        Assert.Equal("caddy-install", job.Kind);
        var done = await api.WaitForJobAsync(job.Id, TimeSpan.FromMinutes(3));
        Assert.True(done.State == JobState.Succeeded, string.Join("\n", done.Log) + done.Error);
        Assert.Contains(api.Audit.Entries, e => e.StartsWith("upload-install-started caddyBinary") && e.Contains("matches the expected checksum"));
        Assert.Contains(api.Audit.Entries, e => e.StartsWith("upload-rejected caddyBinary"));

        var status = (await c.GetFromJsonAsync<CaddyStatus>("/api/caddy/status", JsonDefaults.Api, ct))!;
        Assert.Equal(CaddyRunState.Running, status.State);
        Assert.Equal("v2.11.4", status.Version);
        Assert.Empty(Directory.GetFileSystemEntries(api.Env.Paths.CaddyStagingDir));

        // Proxy settings for Caddy: validation, then a change restarts the running Caddy with the new environment.
        var invalid = await c.PutAsJsonAsync("/api/settings/binary", new { proxyCaddyTraffic = true, checkIntervalHours = 12, noProxy = "localhost" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("proxyCaddyTraffic", await invalid.Content.ReadAsStringAsync(ct));

        var pid = status.ProcessId;
        var put = await c.PutAsJsonAsync("/api/settings/binary", new
        {
            outboundProxy = "http://127.0.0.1:3128", proxyCaddyTraffic = true, noProxy = "localhost, 10.0.0.0/8", checkIntervalHours = 12,
            autoCheckUpdates = true, managerReleaseRepo = "https://github.com/my-org/cpm",
        }, ct);
        var body = await put.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.True(json.GetProperty("proxyCaddyTraffic").GetBoolean());
        Assert.Equal("localhost,10.0.0.0/8", json.GetProperty("noProxy").GetString());
        Assert.Equal("my-org/cpm", json.GetProperty("managerReleaseRepo").GetString());
        Assert.Contains("Caddy was restarted", json.GetProperty("notice").GetString());
        var restarted = (await c.GetFromJsonAsync<CaddyStatus>("/api/caddy/status", JsonDefaults.Api, ct))!;
        Assert.Equal(CaddyRunState.Running, restarted.State);
        Assert.NotEqual(pid, restarted.ProcessId);

        // Unchanged proxy settings: no restart, no notice.
        var same = await c.PutAsJsonAsync("/api/settings/binary", new
        {
            outboundProxy = "http://127.0.0.1:3128", proxyCaddyTraffic = true, noProxy = "localhost,10.0.0.0/8", checkIntervalHours = 24,
        }, ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.False(JsonDocument.Parse(await same.Content.ReadAsStringAsync(ct)).RootElement.TryGetProperty("notice", out _));
        var get = JsonDocument.Parse(await c.GetStringAsync("/api/settings/binary", ct)).RootElement;
        Assert.Equal(24, get.GetProperty("checkIntervalHours").GetInt32());
        Assert.True(get.GetProperty("proxyCaddyTraffic").GetBoolean());
    }

    private static string CaddyPlatform() => Binary.CaddyPlatform.BinaryName;
}
