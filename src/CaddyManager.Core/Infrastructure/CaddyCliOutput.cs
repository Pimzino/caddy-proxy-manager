using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaddyManager.Core;

/// <summary>
/// Reads why a caddy CLI command (`caddy validate`, `caddy adapt`) failed.
/// Up to v2.11.4 the reason is the last line on stderr, "Error: ...". Since v2.11.6 Caddy reports it through its default
/// logger instead (cmd/cobra.go, caddy PR 7768): a JSON log line with level "error". `caddy validate` configures logging
/// from the configuration it checks, and the generated configuration sends the default log to caddy.log, so with those
/// versions the reason is appended to that file and stderr ends with "redirected default logger".
/// https://github.com/caddyserver/caddy/blob/v2.11.7/cmd/cobra.go ; https://github.com/caddyserver/caddy/blob/v2.11.7/logging.go
/// </summary>
public static partial class CaddyCliOutput
{
    [GeneratedRegex(@"^\s*Error:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex CliErrorRegex();

    /// <summary>The reason in the output of a failed command, or null when the output does not carry one.</summary>
    public static string? FindError(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = CliErrorRegex().Matches(output);
        if (m.Count > 0) return m[^1].Groups[1].Value.Trim();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var (level, msg) = ParseLogLine(lines[i]);
            if (level is "error" or "fatal" or "panic") return msg;
        }
        return null;
    }

    /// <summary>
    /// The reason of a failed command for display: <see cref="FindError"/>, else the last line that is not a JSON log
    /// line, else the last line.
    /// </summary>
    public static string ExtractError(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "Caddy reported an error without details.";
        if (FindError(output) is { } found) return found;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
            if (ParseLogLine(lines[i]).Level.Length == 0) return lines[i];
        return ParseLogLine(lines[^1]).Message;
    }

    /// <summary>Level (lower case; "" when the line is not a JSON log line) and readable message of one output line.</summary>
    public static (string Level, string Message) ParseLogLine(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o)
                {
                    var level = o["level"]?.GetValue<string>() ?? "";
                    var msg = o["msg"]?.GetValue<string>() ?? line;
                    var file = o["file"]?.ToString();
                    var ln = o["line"]?.ToString();
                    var err = o["error"]?.ToString();
                    if (file is not null) msg = $"{file}{(ln is null ? "" : ":" + ln)}: {msg}";
                    if (err is not null) msg += ": " + err;
                    return (level.ToLowerInvariant(), msg);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }
        return ("", line);
    }

    /// <summary>
    /// A copy of <paramref name="configJson"/> whose default log writes to stderr, for a second `caddy validate` run
    /// that only exists to read the reason of a failure (never to decide whether the configuration is valid). Null
    /// when the configuration does not send the default log elsewhere (the reason was on stderr already) or is not a
    /// JSON object.
    /// </summary>
    public static string? WithDefaultLogOnStderr(string configJson)
    {
        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root) return null;
            if (root["logging"]?["logs"] is not JsonObject logs || logs["default"] is not JsonObject def) return null;
            if (def["writer"] is not JsonObject writer) return null;
            if (writer["output"]?.GetValueKind() == JsonValueKind.String && writer["output"]!.GetValue<string>() == "stderr") return null;
            // The whole default log is replaced: a level, include or exclude list of the original could filter the
            // reason out, and the JSON encoder is what ParseLogLine reads.
            logs["default"] = new JsonObject
            {
                ["writer"] = new JsonObject { ["output"] = "stderr" },
                ["encoder"] = new JsonObject { ["format"] = "json" },
            };
            return root.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
