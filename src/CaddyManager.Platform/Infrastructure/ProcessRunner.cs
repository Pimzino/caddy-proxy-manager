using System.Diagnostics;
using System.Text;

namespace CaddyManager.Platform.Infrastructure;

/// <summary>Result of a finished child process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string Combined)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Options for <see cref="ProcessRunner.RunAsync"/>.</summary>
public sealed record ProcessOptions
{
    public string? StdIn { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    /// <summary>Variables removed from the inherited environment before start.</summary>
    public IReadOnlyCollection<string>? RemoveEnvironment { get; init; }
    /// <summary>Encoding used to decode stdout/stderr (UTF-8 when null).</summary>
    public Encoding? OutputEncoding { get; init; }
}

/// <summary>Runs short-lived child processes (caddy, sc.exe, powershell.exe) with timeout, stdin and captured output.</summary>
public static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lazy<Encoding> Oem = new(CreateOemEncoding);

    /// <summary>
    /// Encoding of Windows console tools (sc.exe, netsh.exe ...): the system OEM code page (437, 850, 866 ...).
    /// UTF-8 elsewhere or when the code page is unavailable.
    /// </summary>
    public static Encoding OemEncoding => Oem.Value;

    private static Encoding CreateOemEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Utf8NoBom;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Utf8NoBom;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments (passed via ArgumentList, so no manual quoting).
    /// Throws <see cref="TimeoutException"/> when the timeout elapses (the process tree is killed) and
    /// <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, ProcessOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ProcessOptions();
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options.StdIn is not null,
            StandardOutputEncoding = options.OutputEncoding ?? Utf8NoBom,
            StandardErrorEncoding = options.OutputEncoding ?? Utf8NoBom,
            WorkingDirectory = options.WorkingDirectory ?? "",
        };
        if (options.StdIn is not null) psi.StandardInputEncoding = Utf8NoBom;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (options.RemoveEnvironment is not null)
            foreach (var k in options.RemoveEnvironment) psi.Environment.Remove(k);
        if (options.Environment is not null)
            foreach (var (k, v) in options.Environment) psi.Environment[k] = v;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var combined = new StringBuilder();
        var gate = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate) { stdout.AppendLine(e.Data); combined.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate) { stderr.AppendLine(e.Data); combined.AppendLine(e.Data); }
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start '{fileName}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{fileName}': {ex.Message}", ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (options.StdIn is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(options.StdIn.AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException)
            {
                // The process exited before reading all input; its output explains why.
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }
        }

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException(
                    $"'{Path.GetFileName(fileName)} {string.Join(' ', psi.ArgumentList)}' did not finish within {options.Timeout.TotalSeconds:0}s and was terminated.");
            throw;
        }

        // WaitForExitAsync waits for the redirected streams to reach EOF as well.
        lock (gate)
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), combined.ToString());
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
