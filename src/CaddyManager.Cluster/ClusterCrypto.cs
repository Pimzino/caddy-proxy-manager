using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaddyManager.Cluster;

/// <summary>Join token <c>cpmj1.&lt;base64url(json {v:1, primary, nodeId, secret})&gt;</c> (SPEC "Cluster module / Security").</summary>
public sealed record JoinToken(string Primary, string NodeId, byte[] Secret)
{
    public const string Prefix = "cpmj1.";

    private sealed record Payload(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("primary")] string Primary,
        [property: JsonPropertyName("nodeId")] string NodeId,
        [property: JsonPropertyName("secret")] string Secret);

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, Primary, NodeId, Convert.ToBase64String(Secret)));
        return Prefix + Base64Url.EncodeToString(json);
    }

    /// <summary>Parses a token; throws FormatException with a user-facing message when it is not a valid join token.</summary>
    public static JoinToken Parse(string? token)
    {
        const string invalid = "This is not a valid join token. Copy the whole token (it starts with \"cpmj1.\") from Servers → Add server on the primary.";
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException(invalid);
        Payload? p;
        try
        {
            p = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(token.AsSpan(Prefix.Length)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new FormatException(invalid);
        }
        if (p is null || p.V != 1 || string.IsNullOrWhiteSpace(p.NodeId) || string.IsNullOrWhiteSpace(p.Primary)) throw new FormatException(invalid);
        byte[] secret;
        try { secret = Convert.FromBase64String(p.Secret ?? ""); }
        catch (FormatException) { throw new FormatException(invalid); }
        if (secret.Length != ClusterCrypto.SecretLength) throw new FormatException(invalid);
        return new JoinToken(p.Primary, p.NodeId, secret);
    }
}

/// <summary>Wire format of every POST /api/cluster/rpc request and response.</summary>
public sealed record RpcEnvelope
{
    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("nodeId")] public string NodeId { get; init; } = "";
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("ts")] public long Ts { get; init; }
    /// <summary>Base64 of the 12-byte AES-GCM nonce.</summary>
    [JsonPropertyName("nonce")] public string Nonce { get; init; } = "";
    /// <summary>Base64 of ciphertext || 16-byte tag.</summary>
    [JsonPropertyName("ct")] public string Ct { get; init; } = "";
}

/// <summary>
/// Envelope encryption for primary ↔ node RPC: key = HKDF-SHA256(secret, salt "cpm-cluster-v1", info "aes-256-gcm"),
/// AES-256-GCM with a random 96-bit nonce, and associated data that binds direction, node id, timestamp and nonce (the
/// response also binds the request nonce, so a response can never be replayed for another request).
/// Only System.Security.Cryptography primitives are used.
/// </summary>
public static class ClusterCrypto
{
    public const int SecretLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    private static readonly byte[] Salt = "cpm-cluster-v1"u8.ToArray();
    private static readonly byte[] Info = "aes-256-gcm"u8.ToArray();

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static byte[] DeriveKey(byte[] secret) => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Salt, Info);

    public static string RequestAad(string nodeId, long ts, string nonce) => $"cpm1|req|{nodeId}|{ts}|{nonce}";

    public static string ResponseAad(string nodeId, long ts, string nonce, string requestNonce) =>
        $"cpm1|resp|{nodeId}|{ts}|{nonce}|{requestNonce}";

    /// <summary>Encrypts a request plaintext ({op, args}).</summary>
    public static RpcEnvelope SealRequest(byte[] key, string nodeId, byte[] plaintext, long ts)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceLength));
        return new RpcEnvelope { NodeId = nodeId, Ts = ts, Nonce = nonce, Ct = Seal(key, nonce, plaintext, RequestAad(nodeId, ts, nonce)) };
    }

    public static RpcEnvelope SealResponse(byte[] key, string nodeId, byte[] plaintext, long ts, string requestNonce)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceLength));
        return new RpcEnvelope
        {
            NodeId = nodeId, Ts = ts, Nonce = nonce, Ct = Seal(key, nonce, plaintext, ResponseAad(nodeId, ts, nonce, requestNonce)),
        };
    }

    /// <summary>Decrypts and authenticates; null when the envelope is malformed or does not authenticate.</summary>
    public static byte[]? Open(byte[] key, RpcEnvelope e, string aad)
    {
        try
        {
            var nonce = Convert.FromBase64String(e.Nonce);
            var blob = Convert.FromBase64String(e.Ct);
            if (nonce.Length != NonceLength || blob.Length < TagLength) return null;
            var cipher = blob.AsSpan(0, blob.Length - TagLength);
            var tag = blob.AsSpan(blob.Length - TagLength);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(aad));
            return plain;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string Seal(byte[] key, string nonceB64, byte[] plaintext, string aad)
    {
        var nonce = Convert.FromBase64String(nonceB64);
        var blob = new byte[plaintext.Length + TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(0, plaintext.Length), blob.AsSpan(plaintext.Length), Encoding.UTF8.GetBytes(aad));
        return Convert.ToBase64String(blob);
    }

    /// <summary>SHA-256 certificate fingerprint as upper-case hex pairs separated by colons (the form browsers show).</summary>
    public static string Fingerprint(byte[] sha256) => Convert.ToHexString(sha256).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b);

    /// <summary>Case- and separator-insensitive fingerprint comparison.</summary>
    public static bool SameFingerprint(string? a, string? b)
    {
        static string N(string s) => s.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
        return a is not null && b is not null && N(a).Length > 0 && N(a) == N(b);
    }
}

/// <summary>Remembers RPC nonces seen by this node so a captured request cannot be replayed.</summary>
public sealed class NonceCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private DateTime _nextPrune = DateTime.MinValue;

    /// <summary>False when the nonce was already used within <paramref name="retention"/>.</summary>
    public bool TryUse(string nonce, TimeSpan retention)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now >= _nextPrune)
        {
            _nextPrune = now.AddMinutes(1);
            foreach (var (k, exp) in _seen)
                if (exp <= now) _seen.TryRemove(k, out _);
        }
        if (_seen.TryGetValue(nonce, out var expiry) && expiry > now) return false;
        return _seen.TryAdd(nonce, now + retention) || _seen.TryUpdate(nonce, now + retention, expiry);
    }
}
