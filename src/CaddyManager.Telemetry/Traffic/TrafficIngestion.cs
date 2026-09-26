using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Reads Caddy's stats access log (AppPaths.StatsLogFile, written by the `cpm_stats` sink the Config module generates)
/// and folds every entry into the minute/hour/day buckets of the request's host key (<see cref="HostMatcher"/>: configured
/// names only, everything else under "(other)"). Poll and flush are serialised by one lock.
///
/// Persistence keeps two positions in the log, each saved in the same transaction as the state it describes
/// (<see cref="TrafficStore.Save"/>):
/// <list type="bullet">
/// <item>the counter cursor ("stats"): the small counter documents are written with it every
///   <see cref="TelemetryOptions.FlushInterval"/> (≤ 5 s);</item>
/// <item>the blob cursor ("blobs", never after the counter cursor): the unique-client sketches and top clients — the
///   large part — are written with it at most every <see cref="TelemetryOptions.BlobFlushInterval"/>, on shutdown, and
///   with the first save of a fresh start.</item>
/// </list>
/// After a restart (or after a failed save, which discards the in-memory state instead of letting it grow) reading resumes
/// at the blob cursor; lines up to the counter cursor are REPLAYED into the sketches and top clients only, then everything
/// continues normally. So a crash or restart neither loses nor double-counts requests, unique clients or top clients.
/// </summary>
public sealed class TrafficIngestion : IDisposable
{
    private readonly AppPaths _paths;
    private readonly TrafficStore _store;
    private readonly IStore _config;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IServiceProvider? _services;
    private readonly object _lock = new();
    private readonly TrafficAggregator _aggregator;
    private readonly RequestRate _rate = new();

    private LogTailer? _tailer;
    /// <summary>Counter cursor: persisted state plus what was read since (position is set at flush time).</summary>
    private TrafficCursorDoc _cursor = new();
    /// <summary>The persisted blob cursor; null = none yet (the next save includes the blobs).</summary>
    private TrafficCursorDoc? _blobCursor;
    private bool _cursorLoaded;
    /// <summary>Counter cursor fields changed since the last save.</summary>
    private bool _dirty;
    private Replay? _replay;
    private DateTime _lastFlush;
    private DateTime _lastBlobFlush;
    private DateTime _nextFlushAllowed;
    private int _flushFailures;
    private (string? FileId, long Offset) _failedLineAt;
    private int _failedLineCount;
    private long _handlerErrors;
    private HostMatcher? _matcher;
    private DateTime _matcherAt;
    private volatile bool _matcherStale;
    private IConfigChangeFeed? _feed;
    private bool _disposed;

    /// <summary>While replaying after a restart: the counter cursor; lines before it only feed sketches and top clients.</summary>
    private sealed class Replay(string? fileId, long offset)
    {
        public string? FileId { get; } = fileId;
        public long Offset { get; } = offset;
        /// <summary>A line of the target file has been seen.</summary>
        public bool Seen { get; set; }
    }

    internal TrafficIngestion(AppPaths paths, TrafficStore store, IStore config, ISecretProtector protector,
        IOptions<TelemetryOptions> options, TimeProvider time, ILogger<TrafficIngestion> logger, IServiceProvider? services = null)
    {
        _paths = paths;
        _store = store;
        _config = config;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _services = services;
        _aggregator = new TrafficAggregator(store, ClientHasher.LoadOrCreate(paths, protector, logger));
        _lastFlush = _lastBlobFlush = time.GetUtcNow().UtcDateTime;
    }

    /// <summary>When new log lines were last read (UTC).</summary>
    public DateTime? LastIngestAt { get { lock (_lock) { EnsureCursor(); return _cursor.LastIngestAt; } } }
    /// <summary>Lines ingested since statistics began (persisted).</summary>
    public long LinesIngested { get { lock (_lock) { EnsureCursor(); return _cursor.LinesIngested; } } }
    /// <summary>Malformed / oversized lines skipped (persisted).</summary>
    public long MalformedLines { get { lock (_lock) { EnsureCursor(); return _cursor.MalformedLines; } } }
    /// <summary>Rotated log files deleted before they could be read completely (persisted).</summary>
    public long FilesMissed { get { lock (_lock) { EnsureCursor(); return _cursor.FilesMissed; } } }
    /// <summary>Log files read to the end by the current tailer (rotations followed).</summary>
    public long FilesCompleted { get { lock (_lock) return _tailer?.FilesCompleted ?? 0; } }
    /// <summary>Saves that failed in a row (0 = the last one worked).</summary>
    public int FlushFailures { get { lock (_lock) return _flushFailures; } }
    /// <summary>True while lines before the counter cursor are replayed into the sketches after a restart.</summary>
    internal bool Replaying { get { lock (_lock) return _replay is not null; } }
    /// <summary>Buckets currently held in memory.</summary>
    internal int CachedBuckets { get { lock (_lock) return _aggregator.CachedBuckets; } }

    /// <summary>Requests whose log timestamp falls in [from, to) — the last hour is kept in memory for the sampler.</summary>
    public long RequestsBetween(DateTime from, DateTime to) => _rate.Between(from, to);

    /// <summary>Reads everything available and flushes when the flush interval has elapsed. Never throws.</summary>
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
                var before = _cursor.LinesIngested;
                lines = _tailer!.Poll(OnLine, OnOversized, OnFileMissed);
                if (_cursor.LinesIngested != before) _cursor.LastIngestAt = now;
                // Reached the counter cursor without a further line (it was at the end of what was written).
                if (_replay is not null && _tailer.FileId is not null) InReplay();
                if (_replay is null && (_tailer.FileId != _cursor.FileId || _tailer.Offset != _cursor.Offset)) _dirty = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Reading the traffic statistics log failed: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                // The line handler failed (e.g. the telemetry database could not be read): the tailer rewound to that line
                // and the next poll retries it; what was read before it is kept.
                if (++_handlerErrors <= 5 || _handlerErrors % 1000 == 0)
                    _logger.LogError(ex, "Processing the traffic statistics log failed ({Count} so far)", _handlerErrors);
            }
            var blobsDue = _aggregator.HasBlobChanges && (_blobCursor is null || now - _lastBlobFlush >= _options.BlobFlushInterval);
            if ((_dirty || _aggregator.HasCounterChanges || blobsDue) && now - _lastFlush >= _options.FlushInterval && now >= _nextFlushAllowed)
                FlushLocked(now, forceBlobs: false);
            return lines;
        }
    }

    /// <summary>Writes everything pending — counters, sketches and top clients — now (shutdown, tests).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed || _tailer is null) return;
            if (_dirty || _aggregator.HasCounterChanges || _aggregator.HasBlobChanges || BlobCursorBehind())
                FlushLocked(_time.GetUtcNow().UtcDateTime, forceBlobs: true);
        }
    }

    /// <summary>
    /// Before a report: saves pending counters unless that happened less than <see cref="TelemetryOptions.ReportFlushMinAge"/>
    /// ago (viewers poll reports; each poll must not cost a write). Sketches and top clients are not written here — reports
    /// overlay the unsaved ones (<see cref="PendingBlobs"/>).
    /// </summary>
    internal void FlushForReport()
    {
        lock (_lock)
        {
            if (_disposed || _tailer is null) return;
            var now = _time.GetUtcNow().UtcDateTime;
            if (now - _lastFlush < _options.ReportFlushMinAge || now < _nextFlushAllowed) return;
            if (_dirty || _aggregator.HasCounterChanges) FlushLocked(now, forceBlobs: false);
        }
    }

    /// <summary>Unsaved sketches / top clients of buckets with from ≤ Start &lt; to, by bucket Id.</summary>
    internal Dictionary<string, TrafficBlobDoc> PendingBlobs(BucketScale scale, DateTime from, DateTime to)
    {
        lock (_lock) return _disposed ? [] : _aggregator.PendingBlobs(scale, from, to);
    }

    /// <summary>The host key a report filter refers to (a name under a configured wildcard → that wildcard).</summary>
    internal string ResolveHostFilter(string host)
    {
        lock (_lock) return Matcher(_time.GetUtcNow().UtcDateTime).ResolveFilter(host);
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
        if (_feed is null && _services?.GetService<IConfigChangeFeed>() is { } feed)
        {
            _feed = feed;
            _feed.Applied += OnConfigApplied;
        }
        // Positions must be known before reading: starting from the beginning instead would count everything again.
        LoadCursors();
        var counter = _cursor;
        TrafficCursorDoc? start = counter.FileId is null ? null : counter;
        _replay = null;
        if (_blobCursor is { } blobs)
        {
            start = blobs.FileId is null ? null : blobs;
            if (blobs.FileId != counter.FileId || blobs.Offset != counter.Offset)
            {
                _replay = new Replay(counter.FileId, counter.Offset);
                _logger.LogInformation("Traffic statistics: replaying the log from the last saved sketches to the saved counters");
            }
        }
        _tailer = new LogTailer(_paths.StatsLogFile, start);
    }

    private void LoadCursors()
    {
        if (_cursorLoaded) return;
        _cursor = _store.LoadCursor() ?? new TrafficCursorDoc();
        _blobCursor = _store.LoadBlobCursor();
        _cursorLoaded = true;
    }

    /// <summary>Loads the persisted cursor once (also for reports when the background ingester is not running).</summary>
    private void EnsureCursor()
    {
        if (_cursorLoaded) return;
        try { LoadCursors(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not read the traffic statistics cursor"); }
    }

    private void OnConfigApplied(ApplyResult result, string reason) => _matcherStale = true;

    /// <summary>Host keys from the enabled hosts; rebuilt after every configuration apply and periodically.</summary>
    private HostMatcher Matcher(DateTime now)
    {
        if (_matcher is not null && !_matcherStale && now - _matcherAt < _options.HostListRefreshInterval) return _matcher;
        _matcherStale = false;
        _matcherAt = now;
        try { _matcher = HostMatcher.FromHosts(_config.Col<SiteHost>().FindAll()); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the configured hosts for traffic statistics");
            _matcher ??= HostMatcher.Empty;
        }
        return _matcher;
    }

    /// <summary>
    /// Is the line at the tailer's position before the replay target (sketches / top clients only)? Ends the replay when
    /// the target is reached, when the target file is left behind, or when the live file is reached without having seen
    /// the target file (it was deleted by rotation: the requests after the target in it are lost, counted as missed).
    /// </summary>
    private bool InReplay()
    {
        if (_replay is not { } r) return false;
        var fileId = _tailer!.FileId;
        if (fileId is not null && fileId == r.FileId)
        {
            r.Seen = true;
            if (_tailer.Offset < r.Offset) return true;
            EndReplay(targetMissing: false);
            return false;
        }
        if (r.Seen || r.FileId is null) EndReplay(targetMissing: false);
        else if (_tailer.IsLive) EndReplay(targetMissing: true);
        else return true;
        return false;
    }

    private void EndReplay(bool targetMissing)
    {
        _replay = null;
        _dirty = true;
        if (!targetMissing) return;
        _cursor.FilesMissed++;
        RaiseMissedEvent();
    }

    private void OnLine(ReadOnlySpan<byte> line)
    {
        var replay = InReplay();
        var now = _time.GetUtcNow().UtcDateTime;
        bool parsed;
        AccessEntry entry;
        try { parsed = AccessLogParser.TryParse(line, now, out entry); }
        catch (Exception) { parsed = false; entry = default; }
        if (!parsed)
        {
            if (replay) return;
            _cursor.LinesIngested++;
            Malformed(line);
            return;
        }
        var host = Matcher(now).Resolve(entry.Host ?? "");
        try
        {
            _aggregator.Add(entry, host, replay ? ApplyMode.BlobsOnly : ApplyMode.Full);
            _failedLineCount = 0;
        }
        catch (Exception ex)
        {
            // Nothing of the line was applied. Let the tailer rewind and retry it; a line that fails again and again is
            // skipped (counted as unreadable) so that one bad line cannot stop the statistics.
            var at = (_tailer!.FileId, _tailer.Offset);
            _failedLineCount = at == _failedLineAt ? _failedLineCount + 1 : 1;
            _failedLineAt = at;
            if (_failedLineCount < 3) throw;
            _logger.LogError(ex, "Skipping a traffic statistics line that could not be processed three times");
            _failedLineCount = 0;
            if (replay) return;
            _cursor.LinesIngested++;
            Malformed(line);
            return;
        }
        if (replay) return;
        _cursor.LinesIngested++;
        _dirty = true;
        _rate.Add(entry.At);
    }

    private void Malformed(ReadOnlySpan<byte> line)
    {
        _cursor.MalformedLines++;
        _dirty = true;
        if (_cursor.MalformedLines <= 5 || _cursor.MalformedLines % 1000 == 0)
            _logger.LogWarning("Skipped a malformed traffic statistics line ({Count} so far): {Line}", _cursor.MalformedLines,
                AccessLogParser.Preview(line));
    }

    private void OnOversized()
    {
        if (InReplay()) return;
        _cursor.MalformedLines++;
        _dirty = true;
    }

    private void OnFileMissed()
    {
        // During a replay a missing file lies before the counter cursor: its requests are already counted (only the
        // sketches lose them). A missing target file is counted when the replay ends.
        if (_replay is not null)
        {
            _logger.LogWarning("A rotated stats log file was deleted before the unique-client statistics were replayed from it");
            return;
        }
        _cursor.FilesMissed++;
        _dirty = true;
        RaiseMissedEvent();
    }

    private void RaiseMissedEvent() => RaiseEvent(EventSeverity.Warning, "Some traffic statistics were lost",
        "A rotated stats log file was deleted before the manager read it completely (the manager was stopped for a long " +
        "time or traffic was very high). Requests in that file are missing from the traffic statistics.", null);

    private bool BlobCursorBehind() =>
        _tailer is not null && (_blobCursor is null || _blobCursor.FileId != _tailer.FileId || _blobCursor.Offset != _tailer.Offset);

    private void FlushLocked(DateTime now, bool forceBlobs)
    {
        var withBlobs = forceBlobs || _blobCursor is null || now - _lastBlobFlush >= _options.BlobFlushInterval;
        var tailer = _tailer;
        // During a replay the counters stay where they are (the tailer is behind them).
        if (_replay is null && tailer is not null)
        {
            _cursor.FileId = tailer.FileId;
            _cursor.Offset = tailer.Offset;
            _cursor.FileLastWriteUtc = tailer.FileLastWriteUtc;
        }
        TrafficCursorDoc? blobCursor = null;
        List<(BucketScale, TrafficBlobDoc)>? blobs = null;
        if (withBlobs)
        {
            blobs = _aggregator.CollectBlobs();
            blobCursor = tailer is null
                ? _cursor.Clone()
                : new TrafficCursorDoc { FileId = tailer.FileId, Offset = tailer.Offset, FileLastWriteUtc = tailer.FileLastWriteUtc };
            blobCursor.Id = TrafficCursorDoc.BlobCursorId;
        }
        try
        {
            _store.Save(_aggregator.CollectCounters(), _cursor, blobs, blobCursor);
            _aggregator.MarkSaved(withBlobs);
            _dirty = false;
            _lastFlush = now;
            if (withBlobs)
            {
                _lastBlobFlush = now;
                _blobCursor = blobCursor;
            }
            if (_flushFailures >= 3)
                RaiseEvent(EventSeverity.Recovered, "Traffic statistics are being saved again", null, "traffic-stats-save");
            _flushFailures = 0;
        }
        catch (Exception ex)
        {
            _lastFlush = now;
            _flushFailures++;
            _logger.LogError(ex, "Saving traffic statistics failed ({Failures} in a row)", _flushFailures);
            if (_flushFailures == 3)
                RaiseEvent(EventSeverity.Warning, "Traffic statistics cannot be saved", ex.Message, "traffic-stats-save");
            // Do not keep collecting in memory what cannot be saved: forget it and read the log again from the saved
            // cursors (nothing is lost while the log files exist). Back off so a lasting problem costs little.
            RewindToStore();
            _nextFlushAllowed = now + TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks,
                _options.FlushInterval.Ticks * (1L << Math.Min(_flushFailures, 10))));
        }
    }

    private void RewindToStore()
    {
        _tailer?.Dispose();
        _tailer = null;
        _aggregator.Clear();
        _replay = null;
        _dirty = false;
        _cursorLoaded = false;
        _failedLineCount = 0;
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
            if (_feed is not null) _feed.Applied -= OnConfigApplied;
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
