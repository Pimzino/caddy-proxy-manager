using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaddyManager.Ops.Events;

/// <summary>
/// Microsoft Entra ID (Azure AD) client-credentials tokens for SMTP AUTH XOAUTH2 with Exchange Online.
/// POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token (grant_type=client_credentials,
/// scope=https://outlook.office365.com/.default). Tokens are cached until shortly before they expire.
/// The request goes through the outbound proxy configured in Settings → Updates (<see cref="NotificationHttp"/>).
/// </summary>
internal sealed partial class OAuthTokenProvider(NotificationHttp http, TimeProvider time)
{
    public const string Scope = "https://outlook.office365.com/.default";
    /// <summary>Authority base URL (tests point it at a fake).</summary>
    internal string Authority { get; set; } = "https://login.microsoftonline.com";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, string Token, DateTimeOffset ExpiresAt)? _cached;

    /// <summary>True for a tenant GUID or a verified domain name (contoso.onmicrosoft.com / contoso.com).</summary>
    public static bool IsValidTenant(string? tenant) => tenant is { Length: > 0 and <= 253 } && TenantPattern().IsMatch(tenant);

    public async Task<string> GetTokenAsync(string tenant, string clientId, string clientSecret, CancellationToken ct)
    {
        tenant = tenant.Trim();
        clientId = clientId.Trim();
        if (!IsValidTenant(tenant))
            throw new InvalidOperationException($"'{tenant}' is not a valid Microsoft Entra tenant (use the tenant ID GUID or a domain such as contoso.onmicrosoft.com).");
        var key = CacheKey(tenant, clientId, clientSecret);

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_cached is { } c && c.Key == key && c.ExpiresAt - RefreshMargin > now) return c.Token;

            var client = http.Client;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Authority.TrimEnd('/')}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = Scope,
                    ["grant_type"] = "client_credentials",
                }),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await client.SendAsync(request, timeout.Token);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token);

            JsonElement json = default;
            var parsed = false;
            try
            {
                json = JsonDocument.Parse(body).RootElement.Clone();
                parsed = json.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException) { /* reported below */ }

            if (!resp.IsSuccessStatusCode || !parsed || !json.TryGetProperty("access_token", out var tokenEl) || tokenEl.GetString() is not { Length: > 0 } token)
                throw new InvalidOperationException(DescribeError(resp, parsed ? json : null, body));

            var seconds = 3599;
            if (json.TryGetProperty("expires_in", out var exp))
            {
                if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt32(out var n)) seconds = n;
                else if (exp.ValueKind == JsonValueKind.String && int.TryParse(exp.GetString(), out var m)) seconds = m;
            }
            _cached = (key, token, now.AddSeconds(Math.Max(60, seconds)));
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the cached token (e.g. after the SMTP server rejected it).</summary>
    public void Invalidate()
    {
        _gate.Wait();
        try { _cached = null; }
        finally { _gate.Release(); }
    }

    private static string DescribeError(HttpResponseMessage resp, JsonElement? json, string body)
    {
        var sb = new StringBuilder($"Microsoft Entra ID token request failed (HTTP {(int)resp.StatusCode})");
        if (json is { } j && j.TryGetProperty("error", out var err))
        {
            sb.Append(": ").Append(err.GetString());
            if (j.TryGetProperty("error_description", out var desc) && desc.GetString() is { Length: > 0 } d)
            {
                // AADSTS messages carry trace/correlation ids on following lines; the first line is what matters.
                var first = d.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                sb.Append(" — ").Append(first.Length > 400 ? first[..400] + "…" : first);
            }
        }
        else if (!string.IsNullOrWhiteSpace(body))
        {
            sb.Append(": ").Append(body.Length > 200 ? body[..200] + "…" : body);
        }
        sb.Append(". Check the tenant ID, client ID and client secret (Settings → Notifications), and that the app registration has the " +
                  "Office 365 Exchange Online 'SMTP.SendAsApp' application permission with admin consent.");
        return sb.ToString();
    }

    private static string CacheKey(string tenant, string clientId, string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant.ToLowerInvariant()}\n{clientId.ToLowerInvariant()}\n{secret}")));

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex TenantPattern();
}
