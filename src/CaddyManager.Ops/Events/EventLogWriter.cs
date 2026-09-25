using System.Diagnostics;
using System.Runtime.Versioning;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaddyManager.Ops.Events;

internal interface IEventLogWriter
{
    /// <summary>Writes the event to the Windows Application log. No-op elsewhere. Never throws.</summary>
    void Write(EventEntry entry);
}

/// <summary>
/// Registers the "Caddy Proxy Manager" source in the Windows Application log with a message file that exists.
/// .NET 10's EventLog.CreateEventSource (EventLog.GetDllPath, runtime release/10.0) already prefers the .NET Framework 4
/// EventLogMessages.dll (installed by default on every supported Windows, including Server Core) and only falls back to
/// System.Diagnostics.EventLog.Messages.dll beside the app, which does not exist for a single-file executable. A source
/// registered with such a missing file (older builds, or a machine without .NET Framework 4) makes Event Viewer show
/// "The description for Event ID ... cannot be found"; <see cref="Ensure"/> repairs that registration. The Framework
/// EventLogMessages.dll provides the same "%1" message for every event ID.
/// https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Diagnostics.EventLog/src/System/Diagnostics/EventLog.cs
/// </summary>
public static class EventLogSource
{
    public const string Name = AppPaths.ProductName;
    public const string LogName = "Application";
    public const string MessageFile = @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll";
    private const string RegistryBase = @"SYSTEM\CurrentControlSet\Services\EventLog\";

    /// <summary>
    /// Creates the source when missing and repairs a registration whose message file does not exist.
    /// Returns a warning for the log when that was not possible (requires LocalSystem/administrator), else null.
    /// No-op off Windows.
    /// </summary>
    public static string? Ensure()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return EnsureWindows();
        }
        catch (Exception ex)
        {
            return $"The Windows Event Log source '{Name}' could not be registered or repaired ({ex.GetType().Name}: {ex.Message}). " +
                   "Run the service as LocalSystem, or register it once from an elevated PowerShell: " +
                   $"New-EventLog -LogName {LogName} -Source '{Name}' -MessageResourceFile '{Environment.ExpandEnvironmentVariables(MessageFile)}'";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? EnsureWindows()
    {
        var expanded = Environment.ExpandEnvironmentVariables(MessageFile);
        if (!EventLog.SourceExists(Name))
        {
            var data = new EventSourceCreationData(Name, LogName);
            if (File.Exists(expanded)) data.MessageResourceFile = expanded;
            EventLog.CreateEventSource(data);
        }

        using var key = Registry.LocalMachine.OpenSubKey(RegistryBase + LogName + @"\" + Name, writable: true);
        if (key is null) return null; // registered under another log by an administrator: leave it alone
        var current = key.GetValue("EventMessageFile", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (current is not null && MessageFilesExist(current)) return null;
        if (!File.Exists(expanded))
            return $"The Event Log message file {expanded} (.NET Framework 4) is missing; events from '{Name}' will show without a description in Event Viewer.";
        key.SetValue("EventMessageFile", MessageFile, RegistryValueKind.ExpandString);
        return null;
    }

    internal static bool MessageFilesExist(string value)
    {
        var files = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return files.Length > 0 && files.All(f => File.Exists(Environment.ExpandEnvironmentVariables(f)));
    }
}

/// <summary>
/// Writes events to the Windows "Application" log under source "Caddy Proxy Manager".
/// The source is registered on first use (LocalSystem may do this). Event IDs: category base + severity
/// (caddy 1000, config 1100, upstream 1200, certificate 1300, update 1400, readiness 1500,
/// notification 1600, backup 1700, other 1900; +0 info, +1 warning, +2 error, +3 recovered).
/// </summary>
internal sealed class WindowsEventLogWriter(ILogger<WindowsEventLogWriter> logger) : IEventLogWriter
{
    public const string Source = EventLogSource.Name;
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
            if (EventLogSource.Ensure() is { } warning) logger.LogWarning("{Warning}", warning);
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
            "backup" => 1700,
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
