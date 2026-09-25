using System.Diagnostics;
using System.Runtime.Versioning;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Events;

internal interface IEventLogWriter
{
    /// <summary>Writes the event to the Windows Application log. No-op elsewhere. Never throws.</summary>
    void Write(EventEntry entry);
}

/// <summary>
/// Writes events to the Windows "Application" log under source "Caddy Proxy Manager".
/// The source is created on first use (LocalSystem may do this). Event IDs: category base + severity
/// (caddy 1000, config 1100, upstream 1200, certificate 1300, update 1400, readiness 1500,
/// notification 1600, other 1900; +0 info, +1 warning, +2 error, +3 recovered).
/// </summary>
internal sealed class WindowsEventLogWriter(ILogger<WindowsEventLogWriter> logger) : IEventLogWriter
{
    public const string Source = AppPaths.ProductName;
    private const string LogName = "Application";
    private bool _disabled;
    private bool _sourceChecked;

    public void Write(EventEntry entry)
    {
        if (!OperatingSystem.IsWindows() || _disabled) return;
        try
        {
            WriteWindows(entry);
        }
        catch (Exception ex)
        {
            _disabled = true;
            logger.LogWarning(ex,
                "Writing to the Windows Event Log failed; Event Log output is disabled until the manager restarts. " +
                "Make sure the service runs as LocalSystem or create the event source '{Source}' manually.", Source);
        }
    }

    [SupportedOSPlatform("windows")]
    private void WriteWindows(EventEntry e)
    {
        if (!_sourceChecked)
        {
            if (!EventLog.SourceExists(Source))
                EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
            _sourceChecked = true;
        }
        var type = e.Severity switch
        {
            EventSeverity.Error => EventLogEntryType.Error,
            EventSeverity.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        var text = $"{e.Message}\r\n\r\nCategory: {e.Category}\r\nSeverity: {e.Severity}" +
                   (e.Key is null ? "" : $"\r\nKey: {e.Key}") +
                   (string.IsNullOrWhiteSpace(e.Details) ? "" : $"\r\n\r\n{e.Details}");
        if (text.Length > 30000) text = text[..30000] + "…";
        EventLog.WriteEntry(Source, text, type, EventId(e));
    }

    internal static int EventId(EventEntry e)
    {
        var baseId = e.Category.ToLowerInvariant() switch
        {
            "caddy" => 1000,
            "config" => 1100,
            "upstream" => 1200,
            "certificate" => 1300,
            "update" => 1400,
            "readiness" => 1500,
            "notification" => 1600,
            _ => 1900,
        };
        return baseId + e.Severity switch
        {
            EventSeverity.Warning => 1,
            EventSeverity.Error => 2,
            EventSeverity.Recovered => 3,
            _ => 0,
        };
    }
}
