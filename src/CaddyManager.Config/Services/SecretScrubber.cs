using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Services;

/// <summary>
/// ISecretScrubber for text that may contain Caddy output (caddy.log lines for the log viewer, certificate events and
/// notifications, Caddy start errors): every configured secret (SettingsSecrets.Values: DNS provider secrets, the Redis
/// password / encryption key, string values of the custom storage JSON, the EAB key, provider values of the legacy ACME
/// issuer JSON) in all its textual forms (SettingsSecrets.Variants: raw, JSON-escaped, URL- and form-encoded) becomes
/// "***". Secrets configured earlier in this process's lifetime stay scrubbed after they are changed or removed, because
/// caddy.log still holds the old lines. As a second line of defence, the values of credential-like query parameters
/// (token=, key=, apikey=, password=, ...) in URLs are masked too, which also covers secrets from before the last restart.
/// </summary>
public sealed partial class SecretScrubber : ISecretScrubber
{
    /// <summary>The expanded secret list is re-read at the latest after this long (also after a database restore).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly IStore _store;
    private readonly ISecretProtector _protector;
    private readonly ILogger<SecretScrubber> _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _variants = [];
    private DateTime _loadedAt = DateTime.MinValue;
    private bool _stale = true;

    public SecretScrubber(IStore store, ISecretProtector protector, ILogger<SecretScrubber> logger)
    {
        _store = store;
        _protector = protector;
        _logger = logger;
        store.SettingsChanged += t =>
        {
            if (t == typeof(CaddySettings)) lock (_gate) _stale = true;
        };
    }

    // Query parameters that carry credentials in DNS provider APIs (DuckDNS token=, NameSilo key=, Namecheap ApiKey=, ...).
    // Also matches "&" JSON-escaped as & (encoding/json output).
    [GeneratedRegex(@"(?i)((?:[?&;]|\\u0026)(?:token|key|apikey|api_key|api_token|apitoken|access_token|auth_token|password|passwd|pwd|secret|client_secret|secret_key|signature|sig)=)[^&\s""'\]\\<>]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialQueryParameter();

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = SettingsSecrets.ScrubVariants(text, Current());
        return CredentialQueryParameter().Replace(text, m => m.Groups[1].Value + Validation.ConfigRedactor.Mask);
    }

    private IReadOnlyList<string> Current()
    {
        lock (_gate)
        {
            if (!_stale && DateTime.UtcNow - _loadedAt < MaxAge) return _variants;
            try
            {
                foreach (var v in SettingsSecrets.Read(_store.GetSettings<CaddySettings>(), _protector).Values()) _seen.Add(v);
                _variants = SettingsSecrets.Variants(_seen);
            }
            catch (Exception ex)
            {
                // Keep the values known so far; never let scrubbing break the caller.
                _logger.LogDebug(ex, "Could not read the configured secrets for scrubbing");
            }
            _loadedAt = DateTime.UtcNow;
            _stale = false;
            return _variants;
        }
    }
}
