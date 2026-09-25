using System.Text;
using System.Text.Json;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>The fields of one Caddy access-log entry that traffic statistics use.</summary>
internal struct AccessEntry
{
    /// <summary>Entry time (UTC).</summary>
    public DateTime At;
    /// <summary>Lower-case host without port; "" when the request had no Host.</summary>
    public string Host;
    /// <summary>request.client_ip (honours trusted_proxies), falling back to request.remote_ip.</summary>
    public string ClientIp;
    public long BytesIn;
    public long BytesOut;
    /// <summary>0 when nothing wrote a status (aborted request).</summary>
    public int Status;
    public double DurationSeconds;
}

/// <summary>
/// Parses the JSON lines Caddy v2.11.4 writes for the `http.log.access` loggers (SPEC "Traffic statistics logging";
/// field list in docs/research/round3-accesslog.md §1, source:
/// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/server.go logRequest / LoggableHTTPRequest).
/// Unknown fields and nested objects are skipped, so the filter encoder's deletions or additions do not matter.
/// </summary>
internal static class AccessLogParser
{
    private static readonly byte[] Ts = "ts"u8.ToArray();
    private static readonly byte[] Request = "request"u8.ToArray();
    private static readonly byte[] ClientIp = "client_ip"u8.ToArray();
    private static readonly byte[] RemoteIp = "remote_ip"u8.ToArray();
    private static readonly byte[] HostName = "host"u8.ToArray();
    private static readonly byte[] BytesRead = "bytes_read"u8.ToArray();
    private static readonly byte[] Size = "size"u8.ToArray();
    private static readonly byte[] StatusName = "status"u8.ToArray();
    private static readonly byte[] Duration = "duration"u8.ToArray();

    /// <summary>
    /// Parses one line (without the newline). Returns false for anything that is not a JSON object with a `request`
    /// object (malformed or foreign line). <paramref name="now"/> is used when `ts` is missing or not numeric.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> line, DateTime now, out AccessEntry entry)
    {
        entry = default;
        entry.Host = "";
        entry.ClientIp = "";
        var sawRequest = false;
        double? ts = null;
        string? clientIp = null, remoteIp = null;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                if (reader.ValueTextEquals(Request))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                    sawRequest = true;
                    ReadRequest(ref reader, ref entry, ref clientIp, ref remoteIp);
                    continue;
                }
                if (reader.ValueTextEquals(Ts)) { reader.Read(); if (reader.TokenType == JsonTokenType.Number) ts = reader.GetDouble(); }
                else if (reader.ValueTextEquals(BytesRead)) { reader.Read(); entry.BytesIn = ReadLong(ref reader); }
                else if (reader.ValueTextEquals(Size)) { reader.Read(); entry.BytesOut = ReadLong(ref reader); }
                else if (reader.ValueTextEquals(StatusName)) { reader.Read(); entry.Status = (int)ReadLong(ref reader); }
                else if (reader.ValueTextEquals(Duration)) { reader.Read(); if (reader.TokenType == JsonTokenType.Number) entry.DurationSeconds = Math.Max(0, reader.GetDouble()); }
                else { reader.Read(); reader.Skip(); }
            }
            // Anything after the object (other than whitespace) makes the line malformed.
            if (reader.Read()) return false;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        if (!sawRequest) return false;
        entry.ClientIp = !string.IsNullOrEmpty(clientIp) ? clientIp : remoteIp ?? "";
        entry.At = ts is { } t && double.IsFinite(t) && t > 0 && t < 253402300799
            ? DateTime.UnixEpoch.AddTicks((long)(t * TimeSpan.TicksPerSecond))
            : now;
        return true;
    }

    private static void ReadRequest(ref Utf8JsonReader reader, ref AccessEntry entry, ref string? clientIp, ref string? remoteIp)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("property expected");
            if (reader.ValueTextEquals(ClientIp)) { reader.Read(); clientIp = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
            else if (reader.ValueTextEquals(RemoteIp)) { reader.Read(); remoteIp = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
            else if (reader.ValueTextEquals(HostName)) { reader.Read(); entry.Host = reader.TokenType == JsonTokenType.String ? NormalizeHost(reader.GetString()) : ""; }
            else { reader.Read(); reader.Skip(); }
        }
        throw new JsonException("unterminated request object");
    }

    private static long ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number) return 0;
        if (reader.TryGetInt64(out var v)) return Math.Max(0, v);
        return reader.TryGetDouble(out var d) && double.IsFinite(d) && d > 0 ? (long)Math.Min(d, long.MaxValue) : 0;
    }

    /// <summary>
    /// request.host is the raw Host header and may carry a port (research §1): lower-case it and strip the port.
    /// "[::1]:443" → "[::1]"; "Example.COM:8080" → "example.com"; a bare IPv6 literal without brackets is kept whole.
    /// </summary>
    public static string NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var h = raw.Trim();
        if (h.StartsWith('['))
        {
            var close = h.IndexOf(']');
            if (close > 0) h = h[..(close + 1)];
        }
        else
        {
            var colon = h.IndexOf(':');
            // Exactly one colon = host:port. More than one = an unbracketed IPv6 literal (no port possible).
            if (colon >= 0 && h.IndexOf(':', colon + 1) < 0) h = h[..colon];
        }
        return h.ToLowerInvariant();
    }

    /// <summary>For logging/diagnostics: a printable prefix of a malformed line.</summary>
    public static string Preview(ReadOnlySpan<byte> line) =>
        Encoding.UTF8.GetString(line[..Math.Min(line.Length, 200)]);
}
