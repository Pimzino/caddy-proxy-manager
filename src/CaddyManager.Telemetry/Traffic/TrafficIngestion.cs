using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Reads Caddy's stats access log (AppPaths.StatsLogFile, written by the `cpm_stats` sink the Config module generates)
/// and folds every entry into the minute/hour/day buckets. Poll + flush are serialised by one lock; the flush writes the
/// buckets and the tail cursor in a single LiteDB transaction (see <see cref="TrafficStore.Save"/>), so a crash or restart
/// neither loses nor double-counts requests: un-flushed lines are simply read again from the saved cursor.
/// </summary>
public sealed class TrafficIngestion : IDisposable
{
    private readonly AppPaths _paths;
    private readonly TrafficStore _store;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IServiceProvider? _services;
    private readonly object _lock = new();
    private readonly TrafficAggregator _aggregator;
    private readonly RequestRate _rate = new();

    private LogTailer? _tailer;
    private TrafficCursorDoc _cursor = new();
    private bool _cursorLoaded;
    private bool _dirty;
    private DateTime _lastFlush;
    private long _missedReported;
    private int _flushFailures;
    private bool _disposed;

    public TrafficIngestion(AppPaths paths, TrafficStore store, IOptions<TelemetryOptions> options, TimeProvider time,
        ILogger<TrafficIngestion> logger, IServiceProvider? services = null)
    {
        _paths = paths;
        _store = store;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _services = services;
        _aggregator = new TrafficAggregator(store);
        _lastFlush = time.GetUtcNow().UtcDateTime;
    }

    /// <summary>When new log lines were last read (UTC).</summary>
    public DateTime? LastIngestAt { get { lock (_lock) { EnsureCursor(); return _cursor.LastIngestAt; } } }
    /// <summary>Lines ingested since statistics began (persisted).</summary>
    public long LinesIngested { get { lock (_lock) { EnsureCursor(); return _cursor.LinesIngested; } } }
    /// <summary>Malformed / oversized lines skipped (persisted).</summary>
    public long MalformedLines { get { lock (_lock) { EnsureCursor(); return _cursor.MalformedLines; } } }
    /// <summary>Rotated log files deleted before they could be read completely (persisted).</summary>
    public long FilesMissed { get { lock (_lock) { EnsureCursor(); return _cursor.FilesMissed; } } }
    /// <summary>Log files read to the end by this instance (rotations followed).</summary>
    public long FilesCompleted { get { lock (_lock) return _tailer?.FilesCompleted ?? 0; } }

    /// <summary>Requests whose log timestamp falls in [from, to) — the last hour is kept in memory for the sampler.</summary>
    public long RequestsBetween(DateTime from, DateTime to) => _rate.Between(from, to);

    /// <summary>Reads everything available and flushes when the flush interval has elapsed. Never throws for I/O problems.</summary>
    public int PollOnce()
    {
        lock (_lock)
        {
            if (_disposed) return 0;
            var now = _time.GetUtcNow().UtcDateTime;
            var lines = 0;
            try
            {
                EnsureTailer();
                var oversizedBefore = _tailer!.OversizedLines;
                lines = _tailer.Poll(OnLine);
                if (_tailer.OversizedLines != oversizedBefore)
                {
                    _cursor.MalformedLines += _tailer.OversizedLines - oversizedBefore;
                    _dirty = true;
                }
                if (lines > 0) _cursor.LastIngestAt = now;
                if (_tailer.FileId != _cursor.FileId || _tailer.Offset != _cursor.Offset) _dirty = true;
                ReportMissedFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Reading the traffic statistics log failed: {Message}", ex.Message);
            }
            if (_dirty && now - _lastFlush >= _options.FlushInterval) FlushLocked(now);
            return lines;
        }
    }

    /// <summary>Writes pending counters and the cursor now (reports call this so they include the latest requests).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed || !_dirty) return;
            FlushLocked(_time.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>Deletes expired buckets (SPEC retention). Returns the number removed.</summary>
    public int Cleanup()
    {
        try { return _store.Cleanup(_time.GetUtcNow().UtcDateTime); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Traffic statistics retention cleanup failed");
            return 0;
        }
    }

    private void EnsureTailer()
    {
        if (_tailer is not null) return;
        EnsureCursor();
        _tailer = new LogTailer(_paths.StatsLogFile, _cursor);
    }

    /// <summary>Loads the persisted cursor once (also for reports when the background ingester is not running).</summary>
    private void EnsureCursor()
    {
        if (_cursorLoaded) return;
        try { _cursor = _store.LoadCursor() ?? new TrafficCursorDoc(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not read the traffic statistics cursor"); }
        _cursorLoaded = true;
    }

    private void OnLine(ReadOnlySpan<byte> line)
    {
        _cursor.LinesIngested++;
        _dirty = true;
        if (!AccessLogParser.TryParse(line, _time.GetUtcNow().UtcDateTime, out var entry))
        {
            _cursor.MalformedLines++;
            if (_cursor.MalformedLines <= 5 || _cursor.MalformedLines % 1000 == 0)
                _logger.LogWarning("Skipped a malformed traffic statistics line ({Count} so far): {Line}", _cursor.MalformedLines,
                    AccessLogParser.Preview(line));
            return;
        }
        _aggregator.Add(entry);
        _rate.Add(entry.At);
    }

    private void FlushLocked(DateTime now)
    {
        _cursor.FileId = _tailer?.FileId;
        _cursor.Offset = _tailer?.Offset ?? 0;
        _cursor.FileLastWriteUtc = _tailer?.FileLastWriteUtc ?? default;
        try
        {
            _store.Save(_aggregator.Collect(), _cursor);
            _aggregator.Clear();
            _dirty = false;
            _lastFlush = now;
            if (_flushFailures >= 3)
                RaiseEvent(EventSeverity.Recovered, "Traffic statistics are being saved again", null, "traffic-stats-save");
            _flushFailures = 0;
        }
        catch (Exception ex)
        {
            // Keep the in-memory state: the next flush retries with everything read so far.
            _lastFlush = now;
            _flushFailures++;
            _logger.LogError(ex, "Saving traffic statistics failed ({Failures} in a row)", _flushFailures);
            if (_flushFailures == 3)
                RaiseEvent(EventSeverity.Warning, "Traffic statistics cannot be saved", ex.Message, "traffic-stats-save");
        }
    }

    private void ReportMissedFiles()
    {
        if (_tailer is null || _tailer.FilesMissed == _missedReported) return;
        _cursor.FilesMissed += _tailer.FilesMissed - _missedReported;
        _missedReported = _tailer.FilesMissed;
        _dirty = true;
        RaiseEvent(EventSeverity.Warning, "Some traffic statistics were lost",
            "A rotated stats log file was deleted before the manager read it completely (the manager was stopped for a long " +
            "time or traffic was very high). Requests in that file are missing from the traffic statistics.", null);
    }

    private void RaiseEvent(EventSeverity severity, string message, string? details, string? key)
    {
        try
        {
            _services?.GetService<IEventSink>()?.Raise(severity, "traffic", message, details, key: key);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not raise the traffic statistics event"); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _tailer?.Dispose();
            _tailer = null;
        }
    }

    /// <summary>Per-second request counts by log timestamp over the last hour (ring buffer).</summary>
    private sealed class RequestRate
    {
        private const int Seconds = 3600;
        private readonly long[] _second = new long[Seconds];
        private readonly long[] _count = new long[Seconds];

        public void Add(DateTime at)
        {
            var s = at.Ticks / TimeSpan.TicksPerSecond;
            var i = (int)(s % Seconds);
            lock (_count)
            {
                if (_second[i] != s)
                {
                    if (_second[i] > s) return; // older than the ring
                    _second[i] = s;
                    _count[i] = 0;
                }
                _count[i]++;
            }
        }

        public long Between(DateTime from, DateTime to)
        {
            var a = from.Ticks / TimeSpan.TicksPerSecond;
            var b = to.Ticks / TimeSpan.TicksPerSecond;
            if (b - a > Seconds) a = b - Seconds;
            long sum = 0;
            lock (_count)
            {
                for (var s = a; s < b; s++)
                {
                    var i = (int)(s % Seconds);
                    if (_second[i] == s) sum += _count[i];
                }
            }
            return sum;
        }
    }
}

/// <summary>Tails the stats log every <see cref="TelemetryOptions.IngestInterval"/> and runs the retention cleanup hourly.</summary>
internal sealed class StatsIngesterService(TrafficIngestion ingestion, IOptions<TelemetryOptions> options, TimeProvider time,
    ILogger<StatsIngesterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ingestion.PollOnce();
                var now = time.GetUtcNow().UtcDateTime;
                if (now - lastCleanup >= o.RetentionInterval)
                {
                    lastCleanup = now;
                    ingestion.Cleanup();
                }
                await Task.Delay(o.IngestInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Traffic statistics ingestion pass failed");
                try { await Task.Delay(o.IngestInterval, time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Final read + save so a clean shutdown leaves nothing to re-read.
        try
        {
            ingestion.PollOnce();
            ingestion.Flush();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Final traffic statistics flush failed"); }
    }
}
