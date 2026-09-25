using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Config.Generation;

namespace CaddyManager.Config.Validation;

/// <summary>
/// Removes secrets from Caddy JSON shown to non-administrators: ACME EAB MAC keys, DNS-provider credentials (everything
/// under an issuer's challenges.dns.provider except its name), http_basic password hashes, inline PEM private keys and any value whose
/// key name marks it as a secret (password, secret, token, api_key, ...). Values are replaced with "***".
/// </summary>
public static partial class ConfigRedactor
{
    public const string Mask = "***";

    [GeneratedRegex(@"(password|passwd|secret|token|api_?key|apikey|mac_key|private_?key|client_secret|credential|auth_key|access_key|salt)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyRegex();

    /// <summary>Redacts a JSON document (returned pretty-printed). Non-JSON input is returned unchanged.</summary>
    public static string Redact(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
        if (node is null) return json;
        RedactNode(node, new List<string>());
        return CaddyJson.Serialize(node);
    }

    /// <summary>
    /// Redacts a host's advanced routes for non-administrators. Returns the input unchanged (same text) when it holds no
    /// secrets, so ordinary round trips by operators are not mistaken for changes.
    /// </summary>
    public static string? RedactRoutes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var original = JsonNode.Parse(json);
            if (original is null) return json;
            var copy = original.DeepClone();
            RedactNode(copy, new List<string>());
            return JsonNode.DeepEquals(original, copy) ? json : CaddyJson.Serialize(copy);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    internal static void RedactNode(JsonNode node, List<string> path)
    {
        switch (node)
        {
            case JsonObject o:
                if (path.Count >= 3 && path[^1] == "provider" && path[^2] == "dns" && path[^3] == "challenges")
                {
                    // challenges.dns.provider: keep only the provider name.
                    foreach (var key in o.Select(p => p.Key).ToList())
                        if (key != "name") o[key] = Mask;
                    return;
                }
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var child = o[key];
                    if (child is null) continue;
                    if (child is JsonValue v && (IsSecretKey(key) || IsPrivateKeyPem(v)))
                    {
                        o[key] = Mask;
                        continue;
                    }
                    path.Add(key);
                    RedactNode(child, path);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                    if (child is not null) RedactNode(child, path);
                break;
        }
    }

    private static bool IsPrivateKeyPem(JsonValue v) =>
        v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is var s && s.Contains("-----BEGIN", StringComparison.Ordinal) &&
        s.Contains("PRIVATE KEY", StringComparison.Ordinal);

    private static bool IsSecretKey(string key) =>
        !key.Equals("key_id", StringComparison.OrdinalIgnoreCase) && SecretKeyRegex().IsMatch(key);
}
