using System.Diagnostics;
using System.Text;

namespace CaddyManager.Config.Services;

/// <summary>Runs the caddy binary directly with separate stdout/stderr capture (used for `caddy adapt`, and as a validation fallback).</summary>
public static class CaddyProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : string.IsNullOrEmpty(StdOut) ? StdErr : StdOut + "\n" + StdErr;
    }

    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string? workingDirectory = null,
        IDictionary<string, string>? environment = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException($"Caddy binary not found at {exe}.", exe);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"caddy {string.Join(' ', psi.ArgumentList)} did not finish in time.");
        }
        p.WaitForExit(); // flush async output handlers
        lock (stdout) lock (stderr)
            return new Result(p.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
