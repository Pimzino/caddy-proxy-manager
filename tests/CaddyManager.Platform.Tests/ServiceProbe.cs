using System.Runtime.InteropServices;
using CaddyManager.Platform.Infrastructure;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// A tiny .NET 10 program built on demand (Windows CI) that behaves like the manager in the situations the SCM tests need:
///   service &lt;mode&gt; &lt;markerDir&gt; &lt;serviceName&gt; — runs under Microsoft.Extensions.Hosting.WindowsServices (like
///     Program.cs) and writes a marker per start; after 2 s mode "exit" calls Environment.Exit(1) (the manager's restart
///     path), "stop-error" sets ServiceBase.ExitCode = 1 and stops gracefully (SERVICE_STOPPED with a non-zero exit code),
///     "run" keeps running.
///   pfx &lt;file&gt; &lt;dispose 0|1&gt; &lt;outFile&gt; — loads a PFX with X509KeyStorageFlags.MachineKeySet like UiListener, writes the
///     CNG key file name, optionally disposes the certificate on ProcessExit (Program.cs), then Environment.Exit(1).
///   caddy-stub &lt;mode&gt; &lt;adminPort&gt; &lt;markerDir&gt; &lt;serviceName&gt; — a raw Win32 service (StartServiceCtrlDispatcher,
///     SetServiceStatus) that reproduces Caddy v2.11.4's START_PENDING race (caddy PR #8012): it reports START_PENDING with
///     no accepted controls, like runner.Execute in service_windows.go when notify.Ready() ran before the status channel was
///     registered, and serves a fake admin API on 127.0.0.1:&lt;adminPort&gt;:
///       GET /config/ returns the config; POST /load reports RUNNING (accepting STOP and SHUTDOWN) in mode "nudge", like
///       caddy.Load's deferred notify.Ready(), and is ignored in "deaf"/"hung"; POST /stop reports STOP_PENDING and exits
///       with code 0 without SERVICE_STOPPED (Caddy's exitProcess → os.Exit) except in "hung", which ignores it.
///       Mode "noadmin" serves no admin API at all. A SCM stop (possible only once RUNNING) reports STOPPED cleanly.
///     Every event (start, state reported, admin request with its Cache-Control header) is appended to markerDir\events.log.
///     https://github.com/caddyserver/caddy/blob/v2.11.4/service_windows.go
///     https://github.com/caddyserver/caddy/blob/v2.11.4/notify/notify_windows.go
///     https://github.com/caddyserver/caddy/pull/8012
/// Built outside the repository (no Directory.Build.props) into %ProgramData%\cpm-e2e so LocalSystem can run it.
/// </summary>
public static class ServiceProbe
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>disable</Nullable>
            <AssemblyName>cpm-probe</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string Program = """
        using System.Net;
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using System.Security.Cryptography.X509Certificates;
        using System.ServiceProcess;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        var mode = args[0];
        if (mode == "caddy-stub")
        {
            CaddyStub.Run(args[1], int.Parse(args[2]), args[3], args[4]);
            return;
        }
        if (mode == "pfx")
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(args[1], null, X509KeyStorageFlags.MachineKeySet);
            using (var rsa = cert.GetRSAPrivateKey())
                File.WriteAllText(args[3], rsa is RSACng cng ? cng.Key.UniqueName : "not-cng:" + rsa?.GetType().Name);
            if (args[2] == "1") AppDomain.CurrentDomain.ProcessExit += (_, _) => cert.Dispose();
            Environment.Exit(1);
        }

        var serviceMode = args[1];
        var markers = args[2];
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, $"start-{Environment.ProcessId}.txt"), DateTime.UtcNow.ToString("O"));
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = args[3]);
        builder.Services.AddHostedService(sp => new Probe(serviceMode, sp));
        builder.Build().Run();

        sealed class Probe(string mode, IServiceProvider sp) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken ct)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (mode == "exit") Environment.Exit(1);
                if (mode == "stop-error")
                {
                    ((ServiceBase)sp.GetRequiredService<IHostLifetime>()).ExitCode = 1;
                    sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                }
            }
        }
        static class CaddyStub
        {
            const int OwnProcess = 0x10, Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
            const int AcceptStop = 1, AcceptShutdown = 4;
            const int ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5;

            [StructLayout(LayoutKind.Sequential)]
            struct ServiceStatus { public int Type, State, Accepts, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            delegate void ServiceMainFn(int argc, IntPtr argv);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            delegate int HandlerFn(int control, int eventType, IntPtr eventData, IntPtr context);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct TableEntry { public string Name; public ServiceMainFn Main; }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceCtrlDispatcherW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool StartServiceCtrlDispatcher([In] TableEntry[] table);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterServiceCtrlHandlerExW")]
            static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerFn handler, IntPtr context);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

            // Kept in static fields so the GC never collects the delegates the SCM calls back.
            static ServiceMainFn _main;
            static HandlerFn _handler;
            static IntPtr _handle;
            static string _mode, _markers, _name;
            static int _port;
            static readonly object Gate = new();
            static readonly ManualResetEventSlim Done = new();

            public static void Run(string mode, int port, string markers, string name)
            {
                (_mode, _port, _markers, _name) = (mode, port, markers, name);
                Directory.CreateDirectory(markers);
                _main = ServiceMain;
                _handler = Handler;
                var table = new[] { new TableEntry { Name = name, Main = _main }, new TableEntry() };
                if (!StartServiceCtrlDispatcher(table))
                {
                    Log("dispatcher-error " + Marshal.GetLastWin32Error());
                    Environment.Exit(2);
                }
            }

            static void Log(string what)
            {
                lock (Gate) File.AppendAllText(Path.Combine(_markers, "events.log"), $"{DateTime.UtcNow:O} {Environment.ProcessId} {what}{Environment.NewLine}");
            }

            static void Report(int state, int accepts)
            {
                var s = new ServiceStatus { Type = OwnProcess, State = state, Accepts = accepts };
                bool ok;
                lock (Gate) ok = SetServiceStatus(_handle, ref s);
                Log($"state {state} accepts {accepts} ok {ok}");
            }

            static void ServiceMain(int argc, IntPtr argv)
            {
                _handle = RegisterServiceCtrlHandlerEx(_name, _handler, IntPtr.Zero);
                File.WriteAllText(Path.Combine(_markers, $"start-{Environment.ProcessId}.txt"), DateTime.UtcNow.ToString("O"));
                // caddy v2.11.4 runner.Execute: status <- svc.Status{State: svc.StartPending} (Accepts 0); the Ready() that
                // would follow was lost because it ran before notify.SetGlobalStatus.
                Report(StartPending, 0);
                if (_mode != "noadmin") new Thread(ServeAdmin) { IsBackground = true }.Start();
                Done.Wait();
            }

            static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
            {
                Log($"control {control}");
                if (control is ControlStop or ControlShutdown)
                {
                    Report(StopPending, 0);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(200);
                        Report(Stopped, 0);
                        Done.Set();
                    });
                    return 0;
                }
                return control == ControlInterrogate ? 0 : 120; // NO_ERROR / ERROR_CALL_NOT_IMPLEMENTED
            }

            static void ServeAdmin()
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                listener.Start();
                var config = $"{{\"admin\":{{\"listen\":\"127.0.0.1:{_port}\"}}}}";
                while (true)
                {
                    var ctx = listener.GetContext();
                    var method = ctx.Request.HttpMethod;
                    var path = ctx.Request.Url.AbsolutePath;
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream)) body = reader.ReadToEnd();
                    Log($"admin {method} {path} cache-control=[{ctx.Request.Headers["Cache-Control"]}] body={body.Length}");
                    var status = 200;
                    var response = "";
                    var exit = false;
                    if (method == "GET" && path.StartsWith("/config", StringComparison.Ordinal)) response = config;
                    else if (method == "POST" && path == "/load") { if (_mode == "nudge") Report(Running, AcceptStop | AcceptShutdown); }
                    else if (method == "POST" && path == "/stop") exit = _mode != "hung";
                    else status = 404;
                    if (exit) Report(StopPending, 0); // exitProcess: notify.Stopping()
                    var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                    if (exit)
                    {
                        Thread.Sleep(300);
                        Log("exit 0 without SERVICE_STOPPED");
                        Environment.Exit(0); // os.Exit(exitCode) in exitProcess's goroutine
                    }
                }
            }
        }
        """;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _exe;

    public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "cpm-e2e");

    /// <summary>The .NET installation running the tests (for DOTNET_ROOT of a LocalSystem service and for building).</summary>
    public static string DotnetRoot =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    public static async Task<string> BuildAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_exe is not null && File.Exists(_exe)) return _exe;
            var src = Path.Combine(WorkRoot, "probe-src");
            var output = Path.Combine(WorkRoot, "probe");
            Directory.CreateDirectory(src);
            await File.WriteAllTextAsync(Path.Combine(src, "cpm-probe.csproj"), Project, ct);
            await File.WriteAllTextAsync(Path.Combine(src, "Program.cs"), Program, ct);
            var dotnet = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(dotnet)) dotnet = "dotnet";
            var r = await ProcessRunner.RunAsync(dotnet, ["publish", src, "-c", "Release", "-o", output, "--nologo"],
                new ProcessOptions { Timeout = TimeSpan.FromMinutes(5), WorkingDirectory = src }, ct);
            Assert.True(r.ExitCode == 0, "Building the probe failed:\n" + r.Combined);
            _exe = Path.Combine(output, OperatingSystem.IsWindows() ? "cpm-probe.exe" : "cpm-probe");
            Assert.True(File.Exists(_exe), $"{_exe} was not produced.");
            return _exe;
        }
        finally
        {
            Gate.Release();
        }
    }
}
