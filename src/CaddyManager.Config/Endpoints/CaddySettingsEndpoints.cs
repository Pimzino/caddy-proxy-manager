using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.DnsProviders;
using CaddyManager.Config.Services;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

/// <summary>GET/PUT /api/settings/caddy and the DNS provider catalog.</summary>
internal static class SettingsEndpoints
{
    /// <summary>Write-only secret inputs: wire input name → CaddySettings property (camelCase) that stores it protected.</summary>
    private static readonly (string Input, string Output, string Protected)[] SimpleSecrets =
    [
        ("eabMacKey", "hasEabMacKey", "eabMacKeyProtected"),
        ("acmeIssuerJson", "hasAcmeIssuerJson", "acmeIssuerJsonProtected"),
        ("redisPassword", "hasRedisPassword", "redisPasswordProtected"),
        ("redisEncryptionKey", "hasRedisEncryptionKey", "redisEncryptionKeyProtected"),
        ("storageJson", "hasStorageJson", "storageJsonProtected"),
    ];

    private const string DnsSecretsInput = "dnsProviderSecrets";
    private const string DnsSecretsClearInput = "dnsProviderSecretsClear";
    private const string DnsSecretsOutput = "dnsProviderSecretFields";
    private const string DnsSecretsProtected = "dnsProviderSecretsProtected";

    /// <summary>Inputs whose value is JSON (text or object).</summary>
    private static readonly HashSet<string> JsonSecretInputs = new(StringComparer.OrdinalIgnoreCase) { "acmeIssuerJson", "storageJson" };

    /// <summary>Settings only administrators may read (they can contain credentials or reveal internals).</summary>
    internal static readonly string[] AdminOnlyFields = ["rawCaddyfile", "serverOptionsJson", "extraAppsJson"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/caddy").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", async (IStore store, ISecretProtector secrets, HttpContext http) =>
            Results.Ok(ToWire(store.GetSettings<CaddySettings>(), secrets, await EndpointSecurity.IsAdminAsync(http))));

        // The caddy-dns providers with typed fields; installed = the provider module is compiled into the installed Caddy.
        g.MapGet("/dns-providers", async (CaddyConfigService config, CancellationToken ct) =>
        {
            IReadOnlyCollection<string>? modules = null;
            try
            {
                modules = await config.RefreshModulesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // unknown binary: nothing is reported as installed
            }
            return Results.Ok(DnsProviderCatalog.WithInstalled(modules));
        });

        g.MapPut("/", async (JsonObject? body, IStore store, ISecretProtector secrets, AppPaths paths, CaddyConfigService config, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("A settings object is required.");
            // Everything from reading the current settings to persisting the new ones runs under the mutation gate: the PUT
            // rewrites the whole document, so building it from a copy read before a concurrent mutation (cluster replication
            // on a node holds the same gate) would silently revert that mutation.
            return await ConfigTransaction.RunPreparedAsync(http, () => PrepareAsync(body, store, secrets, paths, config, http),
                affectsCaddyfileMode: true);
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>Validates the PUT against the settings current under the gate and returns the change to persist and apply.</summary>
    private static async Task<PreparedChange> PrepareAsync(JsonObject body, IStore store, ISecretProtector secrets, AppPaths paths,
        CaddyConfigService config, HttpContext http)
    {
        var previous = store.GetSettings<CaddySettings>();
        MergeResult merged;
        try
        {
            merged = Merge(previous, body, secrets);
        }
        catch (JsonException ex)
        {
            return PreparedChange.Stop(ApiResults.BadRequest("The settings could not be read: " + ex.Message));
        }
        var next = merged.Settings;

        // A managed cluster node only changes its own listeners and local paths; everything else comes from the primary.
        if (http.RequestServices.GetService<IClusterRole>() is { IsManagedNode: true } role &&
            (merged.SecretInputs.Count > 0 || ReplicatedChanges(previous, next).Count > 0))
            return PreparedChange.Stop(ApiResults.ManagedByPrimary(role.PrimaryName));

        var ui = store.GetSettings<UiSettings>();
        if (ModelValidation.Validate(next, merged.Plain.AcmeIssuerJson, ui) is { } problem) return PreparedChange.Stop(problem);
        var storageChanged = StorageChanged(previous, next, merged.SecretInputs);
        IReadOnlyCollection<string>? modules = null;
        try
        {
            modules = await config.RefreshModulesAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unknown binary: plugin checks are skipped
        }
        if (ModelValidation.ValidateDnsAndStorage(next, merged.Plain, store, modules, storageChanged) is { } round3) return PreparedChange.Stop(round3);

        // Changing the admin endpoint must not turn an existing upstream into a path to it.
        var before = EndpointSecurity.ExistingTargetProblems(store, LocalEndpointGuard.Create(previous, ui, includeUi: false)).ToHashSet(StringComparer.Ordinal);
        var targets = EndpointSecurity.ExistingTargetProblems(store, LocalEndpointGuard.Create(next, ui, includeUi: false)).Where(t => !before.Contains(t)).ToList();
        if (targets.Count > 0)
            return PreparedChange.Stop(ApiResults.BadRequest("These enabled hosts/streams would target a protected endpoint with the new settings: " + string.Join("; ", targets)));

        // A new shared storage folder or certificate store must not be (inside) a folder a static site already serves:
        // Caddy would write every private key, the internal CA's included, into a published folder. Checked before the
        // storage is copied there. (The generator refuses such roots too, on every server.)
        var roots = NewStaticRootConflicts(store, paths, previous, next);
        if (roots.Count > 0)
            return PreparedChange.Stop(ApiResults.BadRequest(
                "These enabled static sites would publish certificates and private keys with the new storage or certificate store folder: "
                + string.Join("; ", roots.Select(r => r.Message)) + ". Choose a folder outside every static site root.",
                roots.GroupBy(r => r.Field).ToDictionary(g => g.Key, g => g.Select(r => r.Message).ToArray())));

        // Local → shared folder: carry the issued certificates, ACME accounts and the internal CA over, so switching to
        // shared storage neither re-issues every certificate nor creates a new internal root.
        var warnings = new List<string>();
        if (previous.StorageBackend == StorageBackend.Local && next.StorageBackend == StorageBackend.FileSystem &&
            CaddyStorage.FileSystemRoot(next, paths) is { } sharedRoot)
        {
            try
            {
                var copied = CaddyStorage.CopyMissingFolders(paths.CaddyStorageDir, sharedRoot);
                if (copied.Count > 0)
                    warnings.Add($"Copied {string.Join(", ", copied.Select(c => c + "/"))} from the local Caddy storage to '{sharedRoot}' (issued certificates, ACME accounts and the internal CA are kept). Folders that already existed there were left unchanged.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PreparedChange.Stop(ApiResults.BadRequest($"The existing certificates could not be copied to '{sharedRoot}': {ex.Message}"));
            }
        }

        var changed = ChangedFields(previous, next);
        return PreparedChange.Run("Caddy settings updated",
            persist: () => store.SaveSettings(next),
            rollback: () => store.SaveSettings(previous),
            onSuccess: apply =>
            {
                ConfigTransaction.Audit(http, "updated", "settings", "caddy", "Caddy settings",
                    changed.Count == 0 ? null : "changed: " + string.Join(", ", changed));
                return Results.Ok(new { item = ToWire(next, secrets, isAdmin: true), apply = apply.WithWarnings(warnings) });
            });
    }

    /// <summary>
    /// For every enabled static host whose root the new settings make unservable on this server (a new shared storage
    /// folder or certificate store at, inside or above it) while the current settings allow it: the settings field that
    /// causes it (storagePath or certificateStorePath) and "host (root): reason".
    /// </summary>
    internal static List<(string Field, string Message)> NewStaticRootConflicts(IStore store, AppPaths paths, CaddySettings previous, CaddySettings next)
    {
        // The new storage with the current certificate store: a conflict there comes from the storage change.
        var storageOnly = JsonSerializer.Deserialize<CaddySettings>(JsonSerializer.Serialize(next, JsonDefaults.Storage), JsonDefaults.Storage)!;
        storageOnly.CertificateStorePath = previous.CertificateStorePath;
        var list = new List<(string, string)>();
        foreach (var h in store.Col<SiteHost>().FindAll().Where(h => h.Enabled && h.Kind == HostKind.Static && !string.IsNullOrWhiteSpace(h.RootPath)))
        {
            if (CaddyStorage.StaticRootProblem(h.RootPath, next, paths) is not { } problem) continue;
            if (CaddyStorage.StaticRootProblem(h.RootPath, previous, paths) is not null) continue; // not caused by this change
            var field = CaddyStorage.StaticRootProblem(h.RootPath, storageOnly, paths) is not null ? "storagePath" : "certificateStorePath";
            list.Add((field, $"{h.Domains.FirstOrDefault() ?? h.Id} ({h.RootPath!.Trim()}): {problem.Message}"));
        }
        return list;
    }

    /// <summary>
    /// Settings camelCased, minus *Protected, plus has* flags and dnsProviderSecretFields (names only); admin-only fields
    /// are absent for other roles. Secrets are never returned.
    /// </summary>
    internal static JsonObject ToWire(CaddySettings s, ISecretProtector secrets, bool isAdmin)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(s, JsonDefaults.Api)!;
        foreach (var (_, output, prot) in SimpleSecrets)
        {
            node.Remove(prot);
            node[output] = !string.IsNullOrEmpty(ProtectedValue(s, prot));
        }
        node.Remove(DnsSecretsProtected);
        var fields = new JsonArray();
        foreach (var f in DnsSecretFieldNames(s, secrets)) fields.Add(f);
        node[DnsSecretsOutput] = fields;
        if (!isAdmin)
            foreach (var f in AdminOnlyFields) node.Remove(f);
        return node;
    }

    /// <summary>Names of the stored DNS provider secrets (for the undecryptable case: none).</summary>
    private static List<string> DnsSecretFieldNames(CaddySettings s, ISecretProtector secrets)
    {
        if (string.IsNullOrEmpty(s.DnsProviderSecretsProtected)) return [];
        try
        {
            return SettingsSecrets.ParseSecretMap(secrets.Unprotect(s.DnsProviderSecretsProtected)).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or IOException)
        {
            return [];
        }
    }

    private static string? ProtectedValue(CaddySettings s, string protectedName) => protectedName switch
    {
        "eabMacKeyProtected" => s.EabMacKeyProtected,
        "acmeIssuerJsonProtected" => s.AcmeIssuerJsonProtected,
        "redisPasswordProtected" => s.RedisPasswordProtected,
        "redisEncryptionKeyProtected" => s.RedisEncryptionKeyProtected,
        "storageJsonProtected" => s.StorageJsonProtected,
        _ => null,
    };

    private static void SetProtected(CaddySettings s, string protectedName, string? value)
    {
        switch (protectedName)
        {
            case "eabMacKeyProtected": s.EabMacKeyProtected = value; break;
            case "acmeIssuerJsonProtected": s.AcmeIssuerJsonProtected = value; break;
            case "redisPasswordProtected": s.RedisPasswordProtected = value; break;
            case "redisEncryptionKeyProtected": s.RedisEncryptionKeyProtected = value; break;
            case "storageJsonProtected": s.StorageJsonProtected = value; break;
        }
    }

    internal sealed record MergeResult(CaddySettings Settings, SettingsSecrets Plain, List<string> SecretInputs);

    /// <summary>
    /// Overlays the request on the current settings. Secret inputs (eabMacKey, acmeIssuerJson, redisPassword,
    /// redisEncryptionKey, storageJson): absent/null = unchanged, "" = clear, other = set; the JSON ones may be sent as text
    /// or as an object. dnsProviderSecrets: key → non-empty string sets, "" removes, absent keys unchanged;
    /// dnsProviderSecretsClear: true removes all first. Changing dnsProvider drops options and secrets that are not
    /// fields of the new provider. Returns the new settings, their effective plain-text secrets and the secret inputs sent.
    /// </summary>
    internal static MergeResult Merge(CaddySettings current, JsonObject body, ISecretProtector secrets)
    {
        var merged = (JsonObject)JsonSerializer.SerializeToNode(current, JsonDefaults.Storage)!;
        var simple = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // input → action ("" = clear)
        JsonObject? dnsSecretsInput = null;
        var dnsClear = false;
        var ignored = SimpleSecrets.SelectMany(x => new[] { x.Output, x.Protected })
            .Concat([DnsSecretsOutput, DnsSecretsProtected]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in body)
        {
            var secretInput = SimpleSecrets.Select(x => x.Input).FirstOrDefault(i => i.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (secretInput is not null)
            {
                if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String) simple[secretInput] = v.GetValue<string>();
                else if (value is JsonObject o && JsonSecretInputs.Contains(secretInput)) simple[secretInput] = o.ToJsonString();
                else if (value is not null)
                    throw new JsonException(JsonSecretInputs.Contains(secretInput) ? $"{secretInput} must be a JSON object (or its text)." : $"{secretInput} must be a string.");
                continue;
            }
            if (key.Equals(DnsSecretsInput, StringComparison.OrdinalIgnoreCase))
            {
                if (value is JsonObject o) dnsSecretsInput = o;
                else if (value is not null) throw new JsonException("dnsProviderSecrets must be an object of field name → value.");
                continue;
            }
            if (key.Equals(DnsSecretsClearInput, StringComparison.OrdinalIgnoreCase))
            {
                if (value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False) dnsClear = b.GetValue<bool>();
                else if (value is not null) throw new JsonException("dnsProviderSecretsClear must be true or false.");
                continue;
            }
            if (ignored.Contains(key)) continue;
            var existingKey = merged.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            merged[existingKey] = value?.DeepClone();
        }

        var next = merged.Deserialize<CaddySettings>(JsonDefaults.Storage) ?? new CaddySettings();
        var plain = SettingsSecrets.Read(current, secrets);
        var secretInputs = new List<string>();

        // simple secrets
        foreach (var (input, _, prot) in SimpleSecrets)
        {
            SetProtected(next, prot, ProtectedValue(current, prot));
            if (!simple.TryGetValue(input, out var action)) continue;
            var value = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
            var currentValue = input switch
            {
                "eabMacKey" => plain.EabMacKey,
                "acmeIssuerJson" => plain.AcmeIssuerJson,
                "redisPassword" => plain.RedisPassword,
                "redisEncryptionKey" => plain.RedisEncryptionKey,
                _ => plain.StorageJson,
            };
            // Re-sending the stored value is not a change (a managed node must accept it; the audit must not list it).
            if (value == currentValue && !plain.Undecryptable.Contains(input)) continue;
            secretInputs.Add(input);
            SetProtected(next, prot, value is null ? null : secrets.Protect(value));
            plain = input switch
            {
                "eabMacKey" => plain with { EabMacKey = value },
                "acmeIssuerJson" => plain with { AcmeIssuerJson = value },
                "redisPassword" => plain with { RedisPassword = value },
                "redisEncryptionKey" => plain with { RedisEncryptionKey = value },
                "storageJson" => plain with { StorageJson = value },
                _ => plain,
            };
        }

        // DNS provider: options and secrets that do not belong to a newly selected provider are dropped.
        next.DnsProvider = DnsProviderCatalog.Normalize(next.DnsProvider);
        next.DnsProviderOptions = (next.DnsProviderOptions ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.Ordinal);
        var dnsSecrets = new Dictionary<string, string>(plain.DnsProviderSecrets, StringComparer.Ordinal);
        if (dnsClear) dnsSecrets.Clear();
        if (!string.Equals(DnsProviderCatalog.Normalize(current.DnsProvider), next.DnsProvider, StringComparison.Ordinal))
        {
            var fields = DnsProviderCatalog.Find(next.DnsProvider)?.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var k in dnsSecrets.Keys.Where(k => !fields.Contains(k)).ToList()) dnsSecrets.Remove(k);
            if (next.DnsProvider is null || DnsProviderCatalog.Find(next.DnsProvider) is not null)
                foreach (var k in next.DnsProviderOptions.Keys.Where(k => !fields.Contains(k)).ToList()) next.DnsProviderOptions.Remove(k);
        }
        if (dnsSecretsInput is not null)
        {
            foreach (var (k, v) in dnsSecretsInput)
            {
                var name = k.Trim();
                if (name.Length == 0) continue;
                if (v is null || (v is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.GetValue<string>().Trim().Length == 0))
                    dnsSecrets.Remove(name);
                else if (v is JsonValue s && s.GetValueKind() == JsonValueKind.String) dnsSecrets[name] = s.GetValue<string>().Trim();
                else throw new JsonException($"dnsProviderSecrets.{name} must be a string.");
            }
        }
        var dnsChanged = !DictEquals(dnsSecrets, plain.DnsProviderSecrets);
        // Counted as a secret input only when it changes something (an echoed empty object is not a change).
        if (dnsChanged) secretInputs.Add(DnsSecretsInput);
        next.DnsProviderSecretsProtected = !dnsChanged
            ? current.DnsProviderSecretsProtected
            : SettingsSecrets.SerializeSecretMap(dnsSecrets) is { } json ? secrets.Protect(json) : null;
        plain = plain with { DnsProviderSecrets = dnsSecrets };

        next.DnsResolvers = (next.DnsResolvers ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeResolver).Distinct().ToList();

        // storage
        next.StoragePath = string.IsNullOrWhiteSpace(next.StoragePath) ? null : next.StoragePath.Trim();
        next.RedisAddresses = (next.RedisAddresses ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        next.RedisUsername = string.IsNullOrWhiteSpace(next.RedisUsername) ? null : next.RedisUsername.Trim();
        next.RedisKeyPrefix = string.IsNullOrWhiteSpace(next.RedisKeyPrefix) ? "caddy" : next.RedisKeyPrefix.Trim();

        next.BindAddresses = (next.BindAddresses ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        next.TrustedProxies = (next.TrustedProxies ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        next.AcmeEmail = next.AcmeEmail?.Trim() ?? "";
        next.AdminListen = next.AdminListen?.Trim() ?? "";
        next.LogLevel = next.LogLevel?.Trim().ToLowerInvariant() ?? "info";
        next.RawCaddyfile ??= "";
        next.EabKeyId = string.IsNullOrWhiteSpace(next.EabKeyId) ? null : next.EabKeyId.Trim();
        next.CustomAcmeDirectory = string.IsNullOrWhiteSpace(next.CustomAcmeDirectory) ? null : next.CustomAcmeDirectory.Trim();
        next.CustomAcmeRootPath = string.IsNullOrWhiteSpace(next.CustomAcmeRootPath) ? null : next.CustomAcmeRootPath.Trim();
        next.CertificateStorePath = string.IsNullOrWhiteSpace(next.CertificateStorePath) ? null : next.CertificateStorePath.Trim();
        next.DefaultRedirectUrl = string.IsNullOrWhiteSpace(next.DefaultRedirectUrl) ? null : next.DefaultRedirectUrl.Trim();
        next.ServerOptionsJson = string.IsNullOrWhiteSpace(next.ServerOptionsJson) ? null : next.ServerOptionsJson.Trim();
        next.ExtraAppsJson = string.IsNullOrWhiteSpace(next.ExtraAppsJson) ? null : next.ExtraAppsJson.Trim();
        next.TlsConnectionPolicyJson = string.IsNullOrWhiteSpace(next.TlsConnectionPolicyJson) ? null : next.TlsConnectionPolicyJson.Trim();
        return new MergeResult(next, plain, secretInputs);
    }

    private static bool DictEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>"1.1.1.1" → "1.1.1.1:53", "2606:4700::1111" → "[2606:4700::1111]:53"; host:port is kept.</summary>
    internal static string NormalizeResolver(string value)
    {
        var v = value.Trim();
        if (NetUtil.TryParseIp(v, out var ip)) return NetUtil.HostPort(ip.ToString(), 53);
        if (!v.Contains(':') && NetUtil.IsValidHost(v)) return v + ":53";
        return v;
    }

    /// <summary>The storage backend or its settings/secrets changed (the FileSystem write test runs only then).</summary>
    private static bool StorageChanged(CaddySettings a, CaddySettings b, List<string> secretInputs) =>
        a.StorageBackend != b.StorageBackend || !string.Equals(a.StoragePath, b.StoragePath, StringComparison.Ordinal)
        || !a.RedisAddresses.SequenceEqual(b.RedisAddresses) || secretInputs.Contains("storageJson");

    /// <summary>
    /// Properties that differ between the two settings, ignoring CaddySettings.NodeLocalProperties (what a managed node
    /// may still change) and the protected secrets (reported separately as secret inputs).
    /// </summary>
    internal static List<string> ReplicatedChanges(CaddySettings a, CaddySettings b)
    {
        var local = CaddySettings.NodeLocalProperties.Select(JsonNamingPolicy.CamelCase.ConvertName).ToHashSet(StringComparer.Ordinal);
        var ja = (JsonObject)JsonSerializer.SerializeToNode(a, JsonDefaults.Storage)!;
        var jb = (JsonObject)JsonSerializer.SerializeToNode(b, JsonDefaults.Storage)!;
        return jb.Where(p => !local.Contains(p.Key) && !p.Key.EndsWith("Protected", StringComparison.Ordinal) && !JsonNode.DeepEquals(p.Value, ja[p.Key]))
            .Select(p => p.Key).ToList();
    }

    private static List<string> ChangedFields(CaddySettings a, CaddySettings b)
    {
        var ja = (JsonObject)JsonSerializer.SerializeToNode(a, JsonDefaults.Storage)!;
        var jb = (JsonObject)JsonSerializer.SerializeToNode(b, JsonDefaults.Storage)!;
        var list = new List<string>();
        foreach (var (k, v) in jb)
        {
            if (JsonNode.DeepEquals(v, ja[k])) continue;
            var input = SimpleSecrets.FirstOrDefault(x => x.Protected == k).Input;
            list.Add(input ?? (k == DnsSecretsProtected ? DnsSecretsInput : k));
        }
        return list;
    }
}
