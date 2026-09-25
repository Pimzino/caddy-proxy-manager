using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Ops;
using CaddyManager.Ops.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Tests;

/// <summary>A minimal host equivalent to Program.cs (Core + Ops + fakes for Config/Platform) on a TestServer.</summary>
public sealed class TestApp : IAsyncDisposable
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "correct horse battery";

    public WebApplication App { get; }
    public AppPaths Paths { get; }
    public string DataDir { get; }
    public FakeCaddyHost CaddyHost { get; } = new();
    public FakeBinaryManager Binary { get; } = new();
    public FakeCertificateInventory Certificates { get; } = new();
    public FakeReadiness Readiness { get; } = new();
    public FakeAdminClient Admin { get; } = new();
    public FakeNotifier? Notifier { get; }
    public ManualTimeProvider? Time { get; }

    public IServiceProvider Services => App.Services;
    public IStore Store => Services.GetRequiredService<IStore>();

    private readonly bool _ownsDataDir;

    private TestApp(Action<OpsOptions>? configure, bool fakeNotifier, ManualTimeProvider? time, bool registerFakes, string? dataDir,
        Action<WebApplication>? mapExtra, Action<IServiceCollection>? services, Action<WebApplication>? pipeline)
    {
        _ownsDataDir = dataDir is null;
        DataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "cpm-ops-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(DataDir);
        Time = time;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddCore(Paths).AddOpsModule();
        builder.Services.Configure<OpsOptions>(o =>
        {
            o.EnableBackgroundServices = false;
            configure?.Invoke(o);
        });
        builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
        builder.Services.AddProblemDetails();
        if (time is not null) builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
        if (fakeNotifier)
        {
            Notifier = new FakeNotifier();
            builder.Services.Replace(ServiceDescriptor.Singleton<INotifier>(Notifier));
        }
        if (registerFakes)
        {
            builder.Services.AddSingleton<ICaddyHost>(CaddyHost);
            builder.Services.AddSingleton<ICaddyBinaryManager>(Binary);
            builder.Services.AddSingleton<ICertificateInventory>(Certificates);
            builder.Services.AddSingleton<IReadinessService>(Readiness);
            builder.Services.AddSingleton<ICaddyAdminClient>(Admin);
        }

        services?.Invoke(builder.Services);

        App = builder.Build();
        App.UseExceptionHandler();
        pipeline?.Invoke(App);
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapCoreEndpoints();
        App.MapOpsEndpoints();
        mapExtra?.Invoke(App);
    }

    public static async Task<TestApp> StartAsync(Action<OpsOptions>? configure = null, bool fakeNotifier = true,
        ManualTimeProvider? time = null, bool registerFakes = true, string? dataDir = null, Action<WebApplication>? mapExtra = null,
        Action<IServiceCollection>? services = null, Action<WebApplication>? pipeline = null)
    {
        var app = new TestApp(configure, fakeNotifier, time, registerFakes, dataDir, mapExtra, services, pipeline);
        await app.App.StartAsync();
        return app;
    }

    /// <summary>HTTP client with a cookie jar; sends the CSRF header unless told not to.</summary>
    public HttpClient Client(bool csrfHeader = true, string? remoteIp = null, CookieContainer? jar = null, bool https = false)
    {
        var server = App.GetTestServer();
        HttpMessageHandler inner = server.CreateHandler(ctx =>
        {
            if (remoteIp is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            if (https) ctx.Request.Scheme = "https";
        });
        var client = new HttpClient(new CookieHandler(jar ?? new CookieContainer()) { InnerHandler = inner })
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        if (csrfHeader) client.DefaultRequestHeaders.Add("X-CPM-Request", "1");
        return client;
    }

    public string ReadSetupToken() => File.ReadAllText(Paths.SetupTokenFile).Trim();

    /// <summary>Completes first-run setup and returns a signed-in admin client.</summary>
    public async Task<HttpClient> SetupAdminAsync()
    {
        var c = Client();
        var resp = await c.PostAsJsonAsync("api/setup", new { token = ReadSetupToken(), email = AdminEmail, name = "Admin", password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return c;
    }

    /// <summary>Creates a user directly in the store (fast path for role tests).</summary>
    public User CreateUser(string email, UserRole role, string password = "another long password", bool disabled = false)
    {
        var u = new User { Email = email, Name = email.Split('@')[0], Role = role, PasswordHash = Passwords.Hash(password), Disabled = disabled };
        Store.Col<User>().Insert(u);
        return u;
    }

    public async Task<HttpClient> LoginAsync(string email, string password = "another long password", string? remoteIp = null)
    {
        var c = Client(remoteIp: remoteIp);
        var resp = await c.PostAsJsonAsync("api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        if (_ownsDataDir)
            try { Directory.Delete(DataDir, recursive: true); } catch { /* temp */ }
    }

    private sealed class CookieHandler(CookieContainer jar) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = jar.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(header)) request.Headers.Add("Cookie", header);
            var resp = await base.SendAsync(request, ct);
            if (resp.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var v in values) jar.SetCookies(request.RequestUri!, v);
            return resp;
        }
    }
}

public static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient c, string url, object body) =>
        c.PutAsJsonAsync(url, body);

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
