using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;

namespace CaddyManager.Ops.Settings;

/// <summary>
/// Settings wire shape (SPEC "Settings wire shapes"): the settings class camelCased minus every
/// <c>*Protected</c> property, plus output-only <c>has&lt;Name&gt;</c> and write-only input <c>&lt;name&gt;</c>.
/// Input semantics for secrets: absent/null = unchanged, "" = clear, other = set (protected).
/// Other properties absent from the input keep their current value.
/// </summary>
internal static class SettingsWire
{
    private const string Suffix = "Protected";

    private sealed record SecretProp(PropertyInfo Property, string InputName, string HasName, string StoredName);

    private static List<SecretProp> SecretProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith(Suffix, StringComparison.Ordinal) && p.CanWrite)
            .Select(p =>
            {
                var baseName = p.Name[..^Suffix.Length];
                return new SecretProp(p, JsonNamingPolicy.CamelCase.ConvertName(baseName), "has" + baseName,
                    JsonNamingPolicy.CamelCase.ConvertName(p.Name));
            })
            .ToList();

    public static JsonObject ToWire<T>(T settings) where T : class
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonDefaults.Api)!.AsObject();
        foreach (var s in SecretProps(typeof(T)))
        {
            node.Remove(s.StoredName);
            node[s.HasName] = !string.IsNullOrEmpty((string?)s.Property.GetValue(settings));
        }
        return node;
    }

    /// <summary>Returns the merged settings or throws <see cref="SettingsInputException"/> with a user-facing message.</summary>
    public static T Apply<T>(T current, JsonObject input, ISecretProtector protector) where T : class, new()
    {
        var secrets = SecretProps(typeof(T));
        var merged = JsonSerializer.SerializeToNode(current, JsonDefaults.Storage)!.AsObject();
        var secretInputs = new List<(SecretProp Prop, JsonNode? Value)>();

        foreach (var (key, value) in input)
        {
            var secret = secrets.FirstOrDefault(s => string.Equals(s.InputName, key, StringComparison.OrdinalIgnoreCase));
            if (secret is not null)
            {
                secretInputs.Add((secret, value));
                continue;
            }
            // Never accept stored ciphertext or computed flags from clients.
            if (secrets.Any(s => string.Equals(s.HasName, key, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(s.StoredName, key, StringComparison.OrdinalIgnoreCase)))
                continue;
            var existing = merged.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue; // unknown property: ignore
            merged[existing] = value?.DeepClone();
        }

        T result;
        try
        {
            result = merged.Deserialize<T>(JsonDefaults.Storage) ?? throw new SettingsInputException("Settings body is empty.");
        }
        catch (JsonException ex)
        {
            var path = string.IsNullOrEmpty(ex.Path) ? "" : $" at '{ex.Path.TrimStart('$', '.')}'";
            throw new SettingsInputException($"Invalid value{path}: {ex.Message}");
        }

        foreach (var (prop, value) in secretInputs)
        {
            if (value is null) continue; // null = unchanged
            if (value.GetValueKind() != JsonValueKind.String)
                throw new SettingsInputException($"'{prop.InputName}' must be a string.");
            var text = value.GetValue<string>();
            prop.Property.SetValue(result, text.Length == 0 ? null : protector.Protect(text));
        }
        return result;
    }

    /// <summary>True if the input sets or clears the named secret (camelCase input name).</summary>
    public static bool TouchesSecret(JsonObject input, string inputName) =>
        input.Any(kv => string.Equals(kv.Key, inputName, StringComparison.OrdinalIgnoreCase) && kv.Value is not null);
}

internal sealed class SettingsInputException(string message) : Exception(message);
