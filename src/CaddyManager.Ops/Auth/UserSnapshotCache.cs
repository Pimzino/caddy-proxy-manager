using System.Collections.Concurrent;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops.Auth;

/// <summary>
/// Short-lived cache of user records used when validating session cookies, so every API request
/// does not hit LiteDB. Mutations through the user/auth endpoints invalidate entries immediately;
/// changes made elsewhere (CLI while the service is stopped) are picked up after the cache duration.
/// </summary>
internal sealed class UserSnapshotCache(IStore store, TimeProvider time, IOptions<OpsOptions> options)
{
    private sealed record Entry(User? User, DateTimeOffset LoadedAt);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public User? Get(string userId)
    {
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(userId, out var e) && now - e.LoadedAt < options.Value.PrincipalCacheDuration)
            return e.User;
        var user = store.Col<User>().FindById(userId);
        _entries[userId] = new Entry(user, now);
        return user;
    }

    public void Invalidate(string userId) => _entries.TryRemove(userId, out _);
}
