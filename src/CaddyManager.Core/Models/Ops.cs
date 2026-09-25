namespace CaddyManager.Core.Models;

public enum UserRole
{
    /// <summary>Read-only.</summary>
    Viewer,
    /// <summary>Manage hosts, certificates, access lists; start/stop/reload Caddy.</summary>
    Operator,
    /// <summary>Everything incl. users, settings, binary updates, readiness fixes.</summary>
    Admin,
}

public sealed class User : Entity
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool Disabled { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Bumped on password change / disable to invalidate sessions.</summary>
    public int SecurityStamp { get; set; }
}

public sealed class AuditEntry : Entity
{
    public string? UserId { get; set; }
    public string UserName { get; set; } = "system";
    /// <summary>e.g. "created", "updated", "deleted", "enabled", "applied", "login".</summary>
    public string Action { get; set; } = "";
    /// <summary>e.g. "host", "certificate", "accessList", "settings", "caddy", "user".</summary>
    public string ObjectType { get; set; } = "";
    public string? ObjectId { get; set; }
    public string? ObjectName { get; set; }
    public string? Details { get; set; }
    public string? RemoteIp { get; set; }
}

public enum EventSeverity { Info, Warning, Error, Recovered }

/// <summary>Operational event (alert). Raised via IEventSink; may trigger notifications.</summary>
public sealed class EventEntry : Entity
{
    public EventSeverity Severity { get; set; }
    /// <summary>e.g. "caddy", "config", "upstream", "certificate", "update", "readiness", "notification".</summary>
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    /// <summary>Dedup key for cooldown/recovery tracking, e.g. "caddy-down" or "cert-expiry:{id}".</summary>
    public string? Key { get; set; }
    public bool Notified { get; set; }
}

public sealed class ConfigRevision : Entity
{
    public string Json { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Reason { get; set; } = "";
    public string AppliedBy { get; set; } = "system";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
