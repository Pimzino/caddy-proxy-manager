using System.Text;
using System.Text.Json;
using CaddyManager.Platform.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Readiness;

/// <summary>Raised when a PowerShell script fails (the script's own error message is in Message).</summary>
public sealed class PowerShellException(string message) : Exception(message);

/// <summary>
/// Runs Windows PowerShell 5.1 as
/// <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -</c> with the script on stdin.
/// The script is sent as a single line that decodes a Base64 (UTF-8) payload into a ScriptBlock, which sidesteps
/// both command-line length limits and the line-by-line parsing quirks of "-Command -" for multi-line scripts.
/// The script's result object is serialised with ConvertTo-Json -Depth 5; errors come back as { "__error": "..." }.
/// </summary>
public partial class PowerShellRunner(ILogger<PowerShellRunner> logger)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Alternative PowerShell executable (e.g. pwsh for tests on non-Windows). Null = Windows PowerShell.</summary>
    public string? ExecutableOverride { get; init; }

    public static string Executable
    {
        get
        {
            var system = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(system) ? system : "powershell.exe";
        }
    }

    /// <summary>Wraps a script so it always prints exactly one JSON document.</summary>
    public static string Wrap(string script) =>
        "$ErrorActionPreference = 'Stop'\n" +
        "$ProgressPreference = 'SilentlyContinue'\n" +
        "$WarningPreference = 'SilentlyContinue'\n" +
        // [Console]::OutputEncoding is deliberately left at the OEM code page: PowerShell decodes the output of native
        // tools (netsh.exe) with it, and the JSON written below is pure ASCII, so it survives any code page.
        // Windows PowerShell 5.1 ETS quirk: arrays may serialise as {"value":[...],"Count":n} unless this type data is removed.
        "try { Remove-TypeData -TypeName System.Array -ErrorAction Stop } catch { }\n" +
        // Escape non-ASCII characters so the result survives any console code page (localised names).
        "function __Ascii([string]$j) { [regex]::Replace($j, '[^\\x00-\\x7F]', { param($m) '\\u{0:x4}' -f [int][char]$m.Value }) }\n" +
        "try {\n" +
        "  $__result = & {\n" + script + "\n}\n" +
        "  if ($null -eq $__result) { '{}' } else { __Ascii (ConvertTo-Json -InputObject $__result -Depth 5 -Compress) }\n" +
        "} catch {\n" +
        "  __Ascii (ConvertTo-Json -InputObject ([ordered]@{ __error = [string]$_.Exception.Message; __at = [string]$_.InvocationInfo.PositionMessage }) -Compress)\n" +
        "}\n";

    /// <summary>The single stdin line: decode the Base64 payload and run it as a script block.</summary>
    public static string BuildStdin(string script)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wrap(script)));
        return "$__s = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + payload + "')); " +
               "& ([ScriptBlock]::Create($__s))\n";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-Z\\-_=>])")]
    private static partial System.Text.RegularExpressions.Regex AnsiEscape();

    /// <summary>
    /// Quotes a value as a PowerShell single-quoted string literal. PowerShell also treats the typographic quotes
    /// U+2018..U+201B as single quotes, so they are normalised to ' before doubling.
    /// </summary>
    public static string Quote(string? value) =>
        "'" + (value ?? "").Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
            .Replace("'", "''") + "'";

    /// <summary>Runs the script and returns its JSON result. Throws PowerShellException for script errors.</summary>
    public virtual async Task<JsonElement> RunJsonAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() && ExecutableOverride is null)
            throw new PlatformNotSupportedException("PowerShell checks are only available on Windows.");
        var result = await ProcessRunner.RunAsync(ExecutableOverride ?? Executable,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-"],
            new ProcessOptions { StdIn = BuildStdin(script), Timeout = timeout ?? DefaultTimeout }, ct);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            logger.LogDebug("PowerShell stderr: {StdErr}", result.StdErr.Trim());
        return ParseOutput(result.StdOut, result.StdErr, result.ExitCode);
    }

    /// <summary>Extracts and parses the JSON document from PowerShell stdout.</summary>
    public static JsonElement ParseOutput(string stdout, string stderr = "", int exitCode = 0)
    {
        // Terminal control sequences (emitted by some PowerShell hosts, e.g. "ESC[?1h") would confuse the JSON search.
        var text = AnsiEscape().Replace(stdout, "").Trim().TrimStart('\uFEFF');
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
            throw new PowerShellException(
                $"PowerShell returned no result (exit code {exitCode}). {(stderr.Trim().Length > 0 ? stderr.Trim() : text)}".Trim());
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text[start..]);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PowerShellException($"PowerShell returned output that is not valid JSON: {ex.Message}");
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("__error", out var err))
        {
            var at = root.TryGetProperty("__at", out var a) ? a.GetString() : null;
            throw new PowerShellException(err.GetString() + (string.IsNullOrWhiteSpace(at) ? "" : "\n" + at.Trim()));
        }
        return root;
    }
}
