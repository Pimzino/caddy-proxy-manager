using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Services;

/// <summary>
/// The plain-text secrets of CaddySettings (everything stored through ISecretProtector). Values that cannot be decrypted
/// (database restored from another server) are null and listed in <see cref="Undecryptable"/>.
/// </summary>
public sealed record SettingsSecrets
{
    public string? EabMacKey { get; init; }
    public string? AcmeIssuerJson { get; init; }
    public Dictionary<string, string> DnsProviderSecrets { get; init; } = new(StringComparer.Ordinal);
    public string? RedisPassword { get; init; }
    public string? RedisEncryptionKey { get; init; }
    public string? StorageJson { get; init; }
    /// <summary>Wire names of the secrets that could not be decrypted.</summary>
    public List<string> Undecryptable { get; init; } = new();

    public StorageSecrets Storage => new(RedisPassword, RedisEncryptionKey, StorageJson);

    public static SettingsSecrets Read(CaddySettings s, ISecretProtector protector)
    {
        var failed = new List<string>();
        string? Get(string? value, string name)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                return protector.Unprotect(value);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or IOException)
            {
                failed.Add(name);
                return null;
            }
        }
        return new SettingsSecrets
        {
            EabMacKey = Get(s.EabMacKeyProtected, "eabMacKey"),
            AcmeIssuerJson = Get(s.AcmeIssuerJsonProtected, "acmeIssuerJson"),
            DnsProviderSecrets = ParseSecretMap(Get(s.DnsProviderSecretsProtected, "dnsProviderSecrets")),
            RedisPassword = Get(s.RedisPasswordProtected, "redisPassword"),
            RedisEncryptionKey = Get(s.RedisEncryptionKeyProtected, "redisEncryptionKey"),
            StorageJson = Get(s.StorageJsonProtected, "storageJson"),
            Undecryptable = failed,
        };
    }

    /// <summary>{"field": "value"} → dictionary (non-string and empty values are dropped).</summary>
    public static Dictionary<string, string> ParseSecretMap(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            if (JsonNode.Parse(json) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String && jv.GetValue<string>() is { Length: > 0 } str)
                        map[k] = str;
        }
        catch (JsonException)
        {
            // treated as empty
        }
        return map;
    }

    public static string? SerializeSecretMap(IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return null;
        var o = new JsonObject();
        foreach (var (k, v) in map.OrderBy(x => x.Key, StringComparer.Ordinal)) o[k] = v;
        return o.ToJsonString();
    }

    /// <summary>
    /// Every configured secret value, longest first, for scrubbing Caddy error messages: Cloudflare's provisioning error,
    /// for example, contains the API token (docs/research/round3-dns01.md §2). Includes the DNS provider values of the
    /// legacy ACME issuer JSON and every string value of the custom storage JSON except its module name.
    /// </summary>
    public IReadOnlyList<string> Values()
    {
        var list = new List<string>();
        void Add(string? v) { if (!string.IsNullOrEmpty(v) && v.Trim().Length >= MinScrubLength) list.Add(v); }
        Add(EabMacKey);
        foreach (var v in DnsProviderSecrets.Values) Add(v);
        Add(RedisPassword);
        Add(RedisEncryptionKey);
        if (CaddyJson.ParseObject(StorageJson, out _) is { } storage)
            foreach (var (key, value) in storage)
                if (key != "module") CollectStrings(value, Add);
        if (CaddyJson.ParseObject(AcmeIssuerJson, out _) is { } issuer)
        {
            if (issuer["challenges"]?["dns"]?["provider"] is JsonObject provider)
                foreach (var (key, value) in provider)
                    if (key != "name") CollectStrings(value, Add);
            if (issuer["external_account"]?["mac_key"] is JsonValue mac && mac.GetValueKind() == JsonValueKind.String) Add(mac.GetValue<string>());
        }
        return list.Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
    }

    /// <summary>Shorter values are not scrubbed: replacing e.g. "a1" everywhere would garble messages without protecting anything.</summary>
    public const int MinScrubLength = 4;

    private static void CollectStrings(JsonNode? node, Action<string?> add)
    {
        switch (node)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                add(v.GetValue<string>());
                break;
            case JsonObject o:
                foreach (var (_, child) in o) CollectStrings(child, add);
                break;
            case JsonArray a:
                foreach (var child in a) CollectStrings(child, add);
                break;
        }
    }

    /// <summary>Replaces every secret (raw and JSON-escaped) in the text with "***".</summary>
    public static string Scrub(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets.Count == 0) return text;
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, Validation.ConfigRedactor.Mask, StringComparison.Ordinal);
            var escaped = JsonEncodedText.Encode(secret).ToString();
            if (escaped != secret) text = text.Replace(escaped, Validation.ConfigRedactor.Mask, StringComparison.Ordinal);
        }
        return text;
    }
}
