using System.Collections.Concurrent;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Auth;

/// <summary>A signed-out session id (cookie tickets are self-contained, so logout must be remembered server-side).</summary>
public sealed class RevokedSession : Entity
{
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Session ids revoked at logout (or replaced at re-sign-in), kept until the cookie would have expired anyway.
/// Held in memory for the per-request check and persisted so revocations survive a service restart.
/// </summary>
internal sealed class SessionRevocations(IStore store, TimeProvider time, ILogger<SessionRevocations> logger)
{
    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, DateTime>? _revoked;

    private ConcurrentDictionary<string, DateTime> Revoked
    {
        get
        {
            if (_revoked is not null) return _revoked;
            lock (_loadLock)
            {
                if (_revoked is not null) return _revoked;
                var now = time.GetUtcNow().UtcDateTime;
                var map = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
                var col = store.Col<RevokedSession>();
                col.DeleteMany(r => r.ExpiresAt < now);
                foreach (var r in col.FindAll()) map[r.Id] = r.ExpiresAt;
                return _revoked = map;
            }
        }
    }

    public bool IsRevoked(string sessionId) =>
        Revoked.TryGetValue(sessionId, out var until) && until > time.GetUtcNow().UtcDateTime;

    public void Revoke(string sessionId, DateTime expiresUtc)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var now = time.GetUtcNow().UtcDateTime;
        if (expiresUtc <= now) return;
        Revoked[sessionId] = expiresUtc;
        try
        {
            var col = store.Col<RevokedSession>();
            col.Upsert(new RevokedSession { Id = sessionId, ExpiresAt = expiresUtc });
            col.DeleteMany(r => r.ExpiresAt < now);
            foreach (var (id, until) in Revoked)
                if (until <= now) Revoked.TryRemove(id, out _);
        }
        catch (Exception ex)
        {
            // Still revoked in memory for this process lifetime.
            logger.LogWarning(ex, "Could not persist the revocation of a signed-out session");
        }
    }
}
