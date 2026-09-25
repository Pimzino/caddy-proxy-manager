using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Events;

/// <summary>
/// Webhook bodies per <see cref="WebhookFormat"/>:
/// generic — <c>{ title, text, severity, category, server, time }</c> (the "text" field also works with simple Slack/Teams receivers);
/// slack — Slack incoming webhook <c>{ text }</c> in mrkdwn;
/// teamsWorkflow — Microsoft Teams "Workflows" (Power Automate "When a Teams webhook request is received") message with an
/// Adaptive Card 1.4 attachment (the replacement for retired Office 365 connectors).
/// </summary>
internal static class WebhookPayloads
{
    private const int MaxDetails = 1500;

    public static JsonObject Build(WebhookFormat format, Notification n, string server, string? uiUrl) => format switch
    {
        WebhookFormat.Slack => Slack(n, server, uiUrl),
        WebhookFormat.TeamsWorkflow => Teams(n, server, uiUrl),
        _ => Generic(n, server, uiUrl),
    };

    internal static JsonObject Generic(Notification n, string server, string? uiUrl) => new()
    {
        ["title"] = n.Title,
        ["text"] = NotificationFormatter.WebhookText(n, server, uiUrl),
        ["severity"] = n.Severity,
        ["category"] = n.Category,
        ["server"] = server,
        ["time"] = n.TimeUtc.ToString("O"),
    };

    internal static JsonObject Slack(Notification n, string server, string? uiUrl)
    {
        var sb = new StringBuilder()
            .Append('*').Append(Esc(n.Title)).Append("*\n")
            .Append(Esc(n.Text)).Append('\n')
            .Append($"*Server:* {Esc(server)} · *Severity:* {Esc(Capitalize(n.Severity))} · *Category:* {Esc(n.Category)} · {UtcText(n.TimeUtc)}");
        if (!string.IsNullOrWhiteSpace(n.Details))
            sb.Append("\n```").Append(Esc(Truncate(n.Details).Replace("```", "'''"))).Append("```");
        if (uiUrl is not null) sb.Append($"\n<{uiUrl}|Open {Esc(AppPaths.ProductName)}>");
        return new JsonObject { ["text"] = sb.ToString() };
    }

    internal static JsonObject Teams(Notification n, string server, string? uiUrl)
    {
        var color = n.Severity.ToLowerInvariant() switch
        {
            "error" => "Attention",
            "warning" => "Warning",
            "recovered" => "Good",
            _ => "Default",
        };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = n.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["color"] = color, ["wrap"] = true,
            },
            new JsonObject { ["type"] = "TextBlock", ["text"] = n.Text, ["wrap"] = true },
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray
                {
                    Fact("Severity", Capitalize(n.Severity)),
                    Fact("Server", server),
                    Fact("Category", n.Category),
                    Fact("Time", UtcText(n.TimeUtc)),
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(n.Details))
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = Truncate(n.Details), ["wrap"] = true, ["fontType"] = "Monospace", ["size"] = "Small", ["isSubtle"] = true,
            });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
            ["msteams"] = new JsonObject { ["width"] = "Full" },
        };
        if (uiUrl is not null)
            card["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = $"Open {AppPaths.ProductName}", ["url"] = uiUrl },
            };
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = card,
                },
            },
        };
    }

    private static JsonObject Fact(string title, string value) => new() { ["title"] = title, ["value"] = value };

    /// <summary>Slack mrkdwn requires &amp;, &lt; and &gt; to be escaped.</summary>
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string s) => s.Length > MaxDetails ? s[..MaxDetails] + "…" : s;

    private static string UtcText(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
