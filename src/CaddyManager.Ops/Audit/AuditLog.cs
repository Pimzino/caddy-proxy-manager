using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Audit;

/// <summary>Persists who changed what (user + remote IP from the current request, "system" otherwise). Never throws.</summary>
internal sealed class AuditLog(IStore store, ICurrentUser current, ILogger<AuditLog> logger) : IAuditLog
{
    public void Record(string action, string objectType, string? objectId = null, string? objectName = null, string? details = null)
    {
        try
        {
            var entry = new AuditEntry
            {
                UserId = current.UserId,
                UserName = current.UserName,
                RemoteIp = current.RemoteIp,
                Action = action,
                ObjectType = objectType,
                ObjectId = objectId,
                ObjectName = objectName,
                Details = Truncate(details, 4000),
            };
            store.Col<AuditEntry>().Insert(entry);
            logger.LogInformation("Audit: {User} {Action} {ObjectType} {ObjectName} {ObjectId}",
                entry.UserName, action, objectType, objectName, objectId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write audit entry {Action} {ObjectType} {ObjectId}", action, objectType, objectId);
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}

internal static class Paging
{
    public static (int Skip, int Take) Clamp(int? skip, int? take, int defaultTake = 50, int maxTake = 500) =>
        (Math.Max(0, skip ?? 0), Math.Clamp(take ?? defaultTake, 1, maxTake));
}

internal static class AuditEventEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", (int? skip, int? take, string? q, IStore store) =>
        {
            var (s, t) = Paging.Clamp(skip, take);
            var query = store.Col<AuditEntry>().Query();
            if (!string.IsNullOrWhiteSpace(q))
            {
                // LIKE uses the database collation (case-insensitive by default).
                var pattern = "%" + EscapeLike(q.Trim()) + "%";
                query = query.Where(BsonExpression.Create(
                    "Action LIKE @0 OR ObjectType LIKE @0 OR ObjectName LIKE @0 OR ObjectId LIKE @0 OR UserName LIKE @0 OR Details LIKE @0 OR RemoteIp LIKE @0",
                    new BsonValue(pattern)));
            }
            var total = query.Count();
            var items = query.OrderByDescending(a => a.CreatedAt).Skip(s).Limit(t).ToList();
            return new Page<AuditEntry>(items, total);
        }).RequireAuthorization(Policies.Admin);

        app.MapGet("/api/events", (int? skip, int? take, string? severity, string? category, IStore store) =>
        {
            var (s, t) = Paging.Clamp(skip, take);
            var query = store.Col<EventEntry>().Query();
            if (!string.IsNullOrWhiteSpace(severity))
            {
                var wanted = new List<EventSeverity>();
                foreach (var part in severity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<EventSeverity>(part, ignoreCase: true, out var sev) || !Enum.IsDefined(sev))
                        return ApiResults.BadRequest($"Unknown severity '{part}'. Use info, warning, error or recovered.");
                    wanted.Add(sev);
                }
                var arr = new BsonArray(wanted.Select(w => new BsonValue(w.ToString())));
                query = query.Where(BsonExpression.Create("Severity IN @0", arr));
            }
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(BsonExpression.Create("Category = @0", new BsonValue(category.Trim())));
            var total = query.Count();
            var items = query.OrderByDescending(e => e.CreatedAt).Skip(s).Limit(t).ToList();
            return Results.Ok(new Page<EventEntry>(items, total));
        }).RequireAuthorization(Policies.Viewer);
    }

    private static string EscapeLike(string s) => s.Replace("%", "").Replace("_", "");
}
