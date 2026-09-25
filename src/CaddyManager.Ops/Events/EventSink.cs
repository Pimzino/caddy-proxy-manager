using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Events;

/// <summary>Read-only view of which alert keys are currently "active" (raised and not yet recovered).</summary>
internal interface IAlertState
{
    bool IsActive(string key);
    IReadOnlyList<string> ActiveKeys(string prefix);
}

/// <summary>
/// Central event pipeline: persist → cooldown per key → Windows Event Log → notifications (SMTP/webhook)
/// for enabled alert rules. Recovery notices are sent once when a previously active key recovers.
/// Raise() never throws and never blocks on network I/O.
/// </summary>
internal sealed class EventSink(
    IStore store,
    INotifier notifier,
    IEventLogWriter eventLog,
    TimeProvider time,
    ILogger<EventSink> logger) : IEventSink, IAlertState
{
    public const string NotificationCategory = "notification";

    private sealed class KeyState
    {
        public DateTime LastRaisedAt;
        public EventSeverity LastSeverity;
        public bool Active;
        public string? AlertRule;
    }

    private readonly object _lock = new();
    private Dictionary<string, KeyState>? _keys;
    private readonly HashSet<string> _unknownRulesLogged = new(StringComparer.OrdinalIgnoreCase);
    private int _pending;

    /// <summary>Number of notification/event-log dispatches still in flight (for diagnostics/tests).</summary>
    public int PendingDispatches => Volatile.Read(ref _pending);

    public void Raise(EventSeverity severity, string category, string message, string? details = null, string? key = null, string? alertRule = null)
    {
        try
        {
            RaiseCore(severity, category, message, details, key, alertRule);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record event {Category}: {Message}", category, message);
        }
    }

    private void RaiseCore(EventSeverity severity, string category, string message, string? details, string? key, string? alertRule)
    {
        var settings = LoadSettings();
        var now = time.GetUtcNow().UtcDateTime;
        var shouldNotify = false;

        if (!string.IsNullOrEmpty(key))
        {
            lock (_lock)
            {
                var keys = EnsureKeys();
                keys.TryGetValue(key, out var st);
                if (severity == EventSeverity.Recovered)
                {
                    if (st is null || !st.Active)
                    {
                        logger.LogDebug("Ignoring recovery for {Key}: no active alert", key);
                        return;
                    }
                    alertRule ??= st.AlertRule;
                    st.Active = false;
                    st.LastSeverity = EventSeverity.Recovered;
                    st.LastRaisedAt = now;
                    shouldNotify = settings.SendRecoveryNotices && IsRuleEnabled(settings, alertRule);
                }
                else
                {
                    var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.CooldownMinutes));
                    if (st is not null && st.LastSeverity != EventSeverity.Recovered && now - st.LastRaisedAt < cooldown &&
                        Rank(severity) <= Rank(st.LastSeverity))
                    {
                        logger.LogDebug("Suppressed event {Key} (cooldown {Minutes} min): {Message}", key, settings.CooldownMinutes, message);
                        return;
                    }
                    st ??= keys[key] = new KeyState();
                    st.LastRaisedAt = now;
                    st.LastSeverity = severity;
                    st.Active = severity is EventSeverity.Warning or EventSeverity.Error;
                    st.AlertRule = alertRule ?? st.AlertRule;
                    shouldNotify = IsRuleEnabled(settings, alertRule);
                }
            }
        }
        else if (severity != EventSeverity.Recovered)
        {
            shouldNotify = IsRuleEnabled(settings, alertRule);
        }

        if (category == NotificationCategory) shouldNotify = false; // never notify about notification failures
        if (shouldNotify && notifier is Notifier && !Notifier.HasEnabledChannel(settings)) shouldNotify = false;

        var entry = new EventEntry
        {
            Severity = severity,
            Category = category,
            Message = message,
            Details = details,
            Key = key,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.Col<EventEntry>().Insert(entry);

        var level = severity switch
        {
            EventSeverity.Error => LogLevel.Error,
            EventSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "Event [{Severity}] {Category}: {Message}{Key}", severity, category, message, key is null ? "" : $" (key {key})");

        var writeEventLog = settings.WriteWindowsEventLog && OperatingSystem.IsWindows();
        if (!shouldNotify && !writeEventLog) return;

        Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
        {
            try
            {
                if (writeEventLog) eventLog.Write(entry);
                if (shouldNotify) await NotifyAsync(entry);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    private async Task NotifyAsync(EventEntry entry)
    {
        List<string> errors;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            errors = notifier is Notifier rich
                ? await rich.SendEventAsync(entry, cts.Token)
                : await notifier.SendAsync(NotificationFormatter.Subject(entry, Environment.MachineName),
                    NotificationFormatter.PlainText(entry, Environment.MachineName, null), cts.Token);
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }

        if (errors.Count == 0)
        {
            try
            {
                entry.Notified = true;
                store.Col<EventEntry>().Update(entry);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not mark event {Id} as notified", entry.Id);
            }
            return;
        }

        logger.LogWarning("Notification for event {Id} failed: {Errors}", entry.Id, string.Join("; ", errors));
        // Recorded without an alert rule so it can never trigger another notification.
        Raise(EventSeverity.Warning, NotificationCategory, "Notification delivery failed",
            $"Event: {entry.Message}\n" + string.Join("\n", errors), key: "notification-failure");
    }

    public bool IsActive(string key)
    {
        lock (_lock)
            return EnsureKeys().TryGetValue(key, out var st) && st.Active;
    }

    public IReadOnlyList<string> ActiveKeys(string prefix)
    {
        lock (_lock)
            return EnsureKeys().Where(kv => kv.Value.Active && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
    }

    /// <summary>Latest time an event with this key was recorded (null if never / since retention).</summary>
    public DateTime? LastRaisedAt(string key)
    {
        lock (_lock)
            return EnsureKeys().TryGetValue(key, out var st) ? st.LastRaisedAt : null;
    }

    /// <summary>Rebuild alert state from persisted events so recovery notices survive manager restarts.</summary>
    private Dictionary<string, KeyState> EnsureKeys()
    {
        if (_keys is not null) return _keys;
        var keys = new Dictionary<string, KeyState>(StringComparer.Ordinal);
        try
        {
            foreach (var e in store.Col<EventEntry>().Find(e => e.Key != null).OrderBy(e => e.CreatedAt))
            {
                if (e.Key is null) continue;
                if (!keys.TryGetValue(e.Key, out var st)) keys[e.Key] = st = new KeyState();
                st.LastRaisedAt = e.CreatedAt;
                st.LastSeverity = e.Severity;
                st.Active = e.Severity is EventSeverity.Warning or EventSeverity.Error;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load previous alert state; starting with no active alerts");
        }
        return _keys = keys;
    }

    private NotificationSettings LoadSettings()
    {
        try { return store.GetSettings<NotificationSettings>(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read notification settings; using defaults");
            return new NotificationSettings();
        }
    }

    private static int Rank(EventSeverity s) => s switch
    {
        EventSeverity.Error => 3,
        EventSeverity.Warning => 2,
        EventSeverity.Info => 1,
        _ => 0,
    };

    /// <summary>
    /// Maps an alertRule name to its NotificationSettings toggle. null/unknown = no notification.
    /// "backupFailure" (scheduled backup failed) shares the "configuration failure" toggle: both mean the
    /// server's configuration may not be recoverable/applied and need an administrator.
    /// </summary>
    internal bool IsRuleEnabled(NotificationSettings s, string? alertRule)
    {
        if (string.IsNullOrEmpty(alertRule)) return false;
        bool? enabled = alertRule.ToLowerInvariant() switch
        {
            "caddydown" => s.AlertCaddyDown,
            "configfailure" => s.AlertConfigFailure,
            "backupfailure" => s.AlertConfigFailure,
            "upstreamunhealthy" => s.AlertUpstreamUnhealthy,
            "certificateexpiry" => s.AlertCertificateExpiry,
            "updateavailable" => s.AlertUpdateAvailable,
            "readinessfailure" => s.AlertReadinessFailure,
            _ => null,
        };
        if (enabled is null)
        {
            lock (_unknownRulesLogged)
                if (_unknownRulesLogged.Add(alertRule))
                    logger.LogWarning("Unknown alert rule '{Rule}' — events using it are recorded but not notified", alertRule);
            return false;
        }
        return enabled.Value;
    }
}
