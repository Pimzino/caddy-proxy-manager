using System.Text.RegularExpressions;

namespace CaddyManager.Platform.Windows;

/// <summary>Parsed `sc.exe queryex &lt;name&gt;` output.</summary>
public sealed record ScQueryResult(int State, string StateName, int? ProcessId, int Win32ExitCode, int ServiceExitCode)
{
    /// <summary>SERVICE_TYPE bits (0x10 = own process, 0x20 = shared svchost process; 0x100 = interactive); 0 when unknown.</summary>
    public int ServiceType { get; init; }

    /// <summary>True when the service has its own process (safe to wait for / terminate that process).</summary>
    public bool IsOwnProcess => (ServiceType & 0x10) != 0 && (ServiceType & 0x20) == 0;
}

/// <summary>Parsed `sc.exe qfailure &lt;name&gt;` output.</summary>
public sealed record ScFailureConfig(int ResetPeriodSeconds, IReadOnlyList<(string Action, int DelayMs)> Actions)
{
    public int RestartActions => Actions.Count(a => a.Action.Equals("RESTART", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Parsers for sc.exe output. The field labels (STATE, PID, RESET_PERIOD ...) are not localised by Windows,
/// only the surrounding messages are, so they are safe to match on.
/// </summary>
public static partial class ScOutputParser
{
    [GeneratedRegex(@"^\s*STATE\s*:\s*(?<n>\d+)\s+(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex StateLine();

    [GeneratedRegex(@"^\s*TYPE\s*:\s*(?<n>[0-9a-fA-F]+)", RegexOptions.Multiline)]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"^\s*PID\s*:\s*(?<n>\d+)", RegexOptions.Multiline)]
    private static partial Regex PidLine();

    [GeneratedRegex(@"^\s*WIN32_EXIT_CODE\s*:\s*(?<n>-?\d+)", RegexOptions.Multiline)]
    private static partial Regex Win32ExitLine();

    [GeneratedRegex(@"^\s*SERVICE_EXIT_CODE\s*:\s*(?<n>-?\d+)", RegexOptions.Multiline)]
    private static partial Regex ServiceExitLine();

    [GeneratedRegex(@"^\s*RESET_PERIOD[^:]*:\s*(?<n>\d+)", RegexOptions.Multiline)]
    private static partial Regex ResetLine();

    [GeneratedRegex(@"(?<action>RESTART|REBOOT|RUN PROCESS|RUN_COMMAND|NONE)\s*--\s*Delay\s*=\s*(?<ms>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ActionItem();

    /// <summary>Returns null when the output does not contain a service status block (e.g. service missing).</summary>
    public static ScQueryResult? ParseQueryEx(string output)
    {
        var state = StateLine().Match(output);
        if (!state.Success) return null;
        var pid = PidLine().Match(output);
        int? processId = pid.Success && int.TryParse(pid.Groups["n"].Value, out var p) && p > 0 ? p : null;
        return new ScQueryResult(
            int.Parse(state.Groups["n"].Value),
            state.Groups["name"].Value,
            processId,
            Win32ExitLine().Match(output) is { Success: true } w && int.TryParse(w.Groups["n"].Value, out var wc) ? wc : 0,
            ServiceExitLine().Match(output) is { Success: true } s && int.TryParse(s.Groups["n"].Value, out var sc) ? sc : 0)
        {
            // sc.exe prints the type in hex without a prefix ("10  WIN32_OWN_PROCESS", "20  WIN32_SHARE_PROCESS").
            ServiceType = TypeLine().Match(output) is { Success: true } t
                          && int.TryParse(t.Groups["n"].Value, System.Globalization.NumberStyles.HexNumber, null, out var type) ? type : 0,
        };
    }

    public static ScFailureConfig? ParseQFailure(string output)
    {
        var reset = ResetLine().Match(output);
        if (!reset.Success) return null;
        var actions = ActionItem().Matches(output)
            .Select(m => (m.Groups["action"].Value.ToUpperInvariant(), int.Parse(m.Groups["ms"].Value)))
            .ToList();
        return new ScFailureConfig(int.Parse(reset.Groups["n"].Value), actions);
    }
}
