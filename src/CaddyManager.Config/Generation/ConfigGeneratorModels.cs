using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Generation;

/// <summary>Everything the generator needs. The generator itself performs no I/O.</summary>
public sealed record ConfigGeneratorInput
{
    public required CaddySettings Settings { get; init; }
    public required AppPaths Paths { get; init; }
    public IReadOnlyList<SiteHost> Hosts { get; init; } = [];
    public IReadOnlyList<StreamHost> Streams { get; init; } = [];
    public IReadOnlyList<AccessList> AccessLists { get; init; } = [];
    public IReadOnlyList<Certificate> Certificates { get; init; } = [];
    /// <summary>Caddy module IDs compiled into the installed binary; null when unknown (binary missing).</summary>
    public IReadOnlyCollection<string>? InstalledModules { get; init; }
    /// <summary>Plain-text EAB MAC key (already unprotected), or null.</summary>
    public string? EabMacKey { get; init; }
    /// <summary>Ids of custom certificates whose files are currently missing or unreadable.</summary>
    public IReadOnlySet<string> UnavailableCertificateIds { get; init; } = new HashSet<string>();
}

public sealed record ConfigGeneratorResult(JsonObject Config, List<string> Warnings)
{
    public string ToJson(bool indented = true) => CaddyJson.Serialize(Config, indented);
}

/// <summary>JSON serialisation helpers for Caddy documents.</summary>
public static class CaddyJson
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode node, bool indented = true) =>
        node.ToJsonString(indented ? Indented : Compact);

    /// <summary>Re-formats a JSON document (pretty or compact). Returns the input unchanged when it is not JSON.</summary>
    public static string Reformat(string json, bool indented = true)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node is null ? json : Serialize(node, indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>SHA-256 (hex, lower case) over the compact form of the document.</summary>
    public static string Hash(string json)
    {
        var compact = Reformat(json, indented: false);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(compact));
        return Convert.ToHexStringLower(bytes);
    }
}
