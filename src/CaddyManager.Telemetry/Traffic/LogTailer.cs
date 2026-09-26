using Microsoft.Win32.SafeHandles;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Receives one complete log line (without the line break). While it runs, <see cref="LogTailer.FileId"/> and
/// <see cref="LogTailer.Offset"/> give the position of the START of that line. If it throws, the tailer rewinds to that
/// line (it is delivered again by the next poll) and the exception propagates.
/// </summary>
internal delegate void LineHandler(ReadOnlySpan<byte> line);

/// <summary>
/// Follows Caddy's stats log across size rotations without losing or repeating lines.
///
/// Caddy v2.11.4's file writer (timberjack) rotates by closing the active file, RENAMING it to
/// "&lt;name&gt;-&lt;time&gt;-&lt;reason&gt;&lt;ext&gt;" and creating a new file at the same path
/// (docs/research/round3-accesslog.md §3; https://github.com/DeRuina/timberjack/blob/v1.4.2/timberjack.go rotate/openNew).
/// The tailer holds the file open with FileShare.ReadWrite | FileShare.Delete, so after a rename it keeps reading the
/// same (renamed) file: on every poll it reads to EOF, then compares the identity of the file now at the path with the
/// open one. When they differ it drains the old handle to EOF (no more writes can arrive there), reads any backups that
/// were rotated in between (several rotations between two polls), and only then opens the new file from offset 0.
///
/// <see cref="FileId"/>/<see cref="Offset"/> (offset just after the last complete line) are what the ingester persists.
/// On a restart the saved file is found again by identity — at the path, or among the backups if it was rotated while
/// the manager was down — and reading resumes at the saved offset. A partial last line is never consumed until its line
/// break arrives (or its file has been rotated away, which means it is complete).
///
/// A path that exists but cannot be opened for a moment (sharing violation by a third-party tool, access denied) is NOT a
/// rotation: the tailer keeps its handle and looks again at the next poll (a rotation read as "gone" would make it read the
/// same file again from offset 0).
/// </summary>
internal sealed class LogTailer : IDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly string _path;
    private readonly string _dir;
    private readonly string _backupPattern;
    private readonly int _maxLineBytes;
    private readonly byte[] _chunk = new byte[ChunkSize];
    private readonly Queue<string> _chain = new();
    /// <summary>Files read to their end (identity and the offset reached), newest last.</summary>
    private readonly LinkedList<(string Id, long Offset)> _completed = new();

    private TrafficCursorDoc? _resumeFrom;
    private SafeFileHandle? _handle;
    private bool _handleIsLive;
    private long _readPos;
    private byte[] _pending = new byte[4096];
    private int _pendingLen;
    private bool _pendingOversized;
    private Action? _onOversized;
    private Action? _onFileMissed;

    public LogTailer(string path, TrafficCursorDoc? resumeFrom, int maxLineBytes = 1024 * 1024)
    {
        _path = Path.GetFullPath(path);
        _dir = Path.GetDirectoryName(_path)!;
        _backupPattern = Path.GetFileNameWithoutExtension(_path) + "-*" + Path.GetExtension(_path);
        _maxLineBytes = maxLineBytes;
        _resumeFrom = resumeFrom?.FileId is null ? null : resumeFrom;
        if (_resumeFrom is not null)
        {
            FileId = _resumeFrom.FileId;
            Offset = _resumeFrom.Offset;
            FileLastWriteUtc = _resumeFrom.FileLastWriteUtc;
        }
    }

    /// <summary>Identity of the file being read (or last read).</summary>
    public string? FileId { get; private set; }
    /// <summary>Bytes of that file consumed (just after the last complete line).</summary>
    public long Offset { get; private set; }
    public DateTime FileLastWriteUtc { get; private set; }
    /// <summary>Rotated files that disappeared (deleted by roll_keep) before they were read completely.</summary>
    public long FilesMissed { get; private set; }
    /// <summary>Files read to the end (rotated away or drained backups).</summary>
    public long FilesCompleted { get; private set; }
    /// <summary>Lines longer than the limit (skipped, count as malformed).</summary>
    public long OversizedLines { get; private set; }
    /// <summary>The open file is the one at the path (not a rotated file being drained).</summary>
    public bool IsLive => _handle is not null && _handleIsLive;

    /// <summary>
    /// Reads everything available. Returns the number of lines delivered. <paramref name="onOversized"/> is called (at the
    /// position of that line, like <paramref name="onLine"/>) for every line skipped because it is too long;
    /// <paramref name="onFileMissed"/> for every rotated file found deleted before it was read to its end.
    /// </summary>
    public int Poll(LineHandler onLine, Action? onOversized = null, Action? onFileMissed = null)
    {
        _onOversized = onOversized;
        _onFileMissed = onFileMissed;
        try { return PollCore(onLine); }
        finally
        {
            _onOversized = null;
            _onFileMissed = null;
        }
    }

    private int PollCore(LineHandler onLine)
    {
        var lines = 0;
        if (_resumeFrom is not null)
        {
            // Deferred while the files cannot be opened (not while they are missing): try again next poll.
            if (!Resume(_resumeFrom)) return 0;
            _resumeFrom = null;
        }
        // Bounded: each iteration either returns or finishes one file.
        for (var guard = 0; guard < 1000; guard++)
        {
            if (_handle is null)
            {
                if (_chain.Count > 0)
                {
                    // A backup that exists but cannot be opened now stays first in line (order matters).
                    if (OpenFile(_chain.Peek(), 0, live: false) == PathState.Unavailable) return lines;
                    _chain.Dequeue();
                    continue;
                }
                if (OpenFile(_path, 0, live: true) != PathState.Present) return lines;
            }

            lines += ReadAvailable(onLine);
            if (!_handleIsLive)
            {
                lines += CompleteFile(onLine);
                continue;
            }

            var pathState = FileIdentity.OfPath(_path, out var pathId);
            // Exists but cannot be opened right now: not a rotation. Keep reading the open handle; look again next poll.
            if (pathState == PathState.Unavailable) return lines;
            if (pathId == FileId)
            {
                // Same file. A file that became shorter than what we read was truncated in place: start over.
                if (RandomAccess.GetLength(_handle!) < _readPos)
                {
                    _readPos = 0;
                    Offset = 0;
                    _pendingLen = 0;
                    _pendingOversized = false;
                    continue;
                }
                return lines;
            }

            // Rotated (or removed): the renamed file gets no further writes, so read it to the end now.
            lines += ReadAvailable(onLine);
            var oldId = FileId;
            var oldWrite = FileLastWriteUtc;
            lines += CompleteFile(onLine);
            foreach (var b in NewerBackups(oldId, oldWrite, pathId)) _chain.Enqueue(b);
            if (pathId is null && _chain.Count == 0) return lines; // wait for the new file
        }
        return lines;
    }

    /// <summary>Positions the tailer at the saved cursor. False = deferred (a file exists but cannot be opened now).</summary>
    private bool Resume(TrafficCursorDoc saved)
    {
        // Prefer the live file: open it first so its identity and the handle we read from are the same file.
        SafeFileHandle? live = null;
        string? liveId = null;
        try
        {
            live = FileIdentity.OpenShared(_path);
            liveId = FileIdentity.Of(live);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            live?.Dispose();
            live = null;
            // Only "not there" lets us conclude anything; a live file we cannot open now might be the saved one.
            if (!FileIdentity.IsMissing(ex)) return false;
        }
        if (live is not null && liveId == saved.FileId)
        {
            var length = RandomAccess.GetLength(live);
            AdoptHandle(live, liveId!, saved.Offset <= length ? saved.Offset : 0, isLive: true);
            return true;
        }
        live?.Dispose();

        // The saved file was rotated while we were not running: find it among the backups.
        var backups = Backups(out var unopenable);
        var match = backups.FirstOrDefault(b => b.Id == saved.FileId);
        if (match.Path is not null)
        {
            SafeFileHandle? h = null;
            try
            {
                h = FileIdentity.OpenShared(match.Path);
                if (FileIdentity.Of(h) == saved.FileId)
                {
                    var length = RandomAccess.GetLength(h);
                    AdoptHandle(h, saved.FileId!, Math.Min(saved.Offset, length), isLive: false);
                    foreach (var b in NewerBackups(saved.FileId, match.LastWriteUtc, liveId)) _chain.Enqueue(b);
                    return true;
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                h?.Dispose();
                if (!FileIdentity.IsMissing(ex)) return false;
            }
            h?.Dispose();
        }
        // A backup we could not open might be the saved file: do not declare it lost yet.
        else if (unopenable) return false;

        // Gone (deleted by roll_keep): what followed it is still readable.
        Missed();
        foreach (var b in NewerBackups(saved.FileId, saved.FileLastWriteUtc, liveId)) _chain.Enqueue(b);
        FileId = null;
        Offset = 0;
        return true;
    }

    /// <summary>Opens a file: Present = opened; Missing = not there (a queued backup counts as missed); Unavailable = try later.</summary>
    private PathState OpenFile(string path, long offset, bool live)
    {
        SafeFileHandle? h = null;
        string id;
        try
        {
            h = FileIdentity.OpenShared(path);
            id = FileIdentity.Of(h);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            h?.Dispose();
            if (!FileIdentity.IsMissing(ex)) return PathState.Unavailable;
            // A queued backup deleted by roll_keep before we got to it.
            if (!live) Missed();
            return PathState.Missing;
        }
        // A file we already read to its end (should not happen at the live path, but never read one twice): continue
        // where we stopped instead of starting over.
        for (var n = _completed.First; n is not null; n = n.Next)
        {
            if (n.Value.Id != id) continue;
            offset = Math.Max(offset, Math.Min(n.Value.Offset, RandomAccess.GetLength(h)));
            _completed.Remove(n);
            break;
        }
        AdoptHandle(h, id, offset, live);
        return PathState.Present;
    }

    private void AdoptHandle(SafeFileHandle h, string id, long offset, bool isLive)
    {
        _handle?.Dispose();
        _handle = h;
        _handleIsLive = isLive;
        FileId = id;
        Offset = offset;
        _readPos = offset;
        _pendingLen = 0;
        _pendingOversized = false;
        FileLastWriteUtc = SafeLastWrite(h);
    }

    /// <summary>Delivers every complete line available in the open file.</summary>
    private int ReadAvailable(LineHandler onLine)
    {
        var lines = 0;
        var readAny = false;
        try
        {
            while (true)
            {
                var n = RandomAccess.Read(_handle!, _chunk, _readPos);
                if (n <= 0) break;
                readAny = true;
                _readPos += n;
                var span = _chunk.AsSpan(0, n);
                while (true)
                {
                    var nl = span.IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        AppendPending(span);
                        break;
                    }
                    var consumed = _pendingLen + nl + 1;
                    if (_pendingOversized) Oversized();
                    else if (_pendingLen > 0)
                    {
                        AppendPending(span[..nl]);
                        if (!_pendingOversized && Deliver(_pending.AsSpan(0, _pendingLen), onLine)) lines++;
                        else if (_pendingOversized) Oversized();
                    }
                    else if (Deliver(span[..nl], onLine)) lines++;
                    Offset += consumed;
                    _pendingLen = 0;
                    _pendingOversized = false;
                    span = span[(nl + 1)..];
                }
            }
        }
        catch
        {
            // The handler (or a read) failed: Offset is the start of the line being delivered. Read from there next time,
            // so nothing after it is skipped and the saved offset stays exact (no mid-line resume after a restart).
            Rewind();
            throw;
        }
        finally
        {
            if (readAny && _handle is not null) FileLastWriteUtc = SafeLastWrite(_handle);
        }
        return lines;
    }

    private void Rewind()
    {
        _readPos = Offset;
        _pendingLen = 0;
        _pendingOversized = false;
    }

    private void Missed()
    {
        FilesMissed++;
        _onFileMissed?.Invoke();
    }

    private void Oversized()
    {
        _onOversized?.Invoke();
        OversizedLines++;
    }

    private void AppendPending(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (_pendingOversized || _pendingLen + bytes.Length > _maxLineBytes)
        {
            // Keep counting the bytes (for the offset) but not the content.
            _pendingOversized = true;
            _pendingLen += bytes.Length;
            return;
        }
        if (_pendingLen + bytes.Length > _pending.Length)
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLen + bytes.Length));
        bytes.CopyTo(_pending.AsSpan(_pendingLen));
        _pendingLen += bytes.Length;
    }

    private static bool Deliver(ReadOnlySpan<byte> line, LineHandler onLine)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IsEmpty) return false;
        onLine(line);
        return true;
    }

    /// <summary>The open file is finished (rotated away / drained backup): a trailing line without a break is complete.</summary>
    private int CompleteFile(LineHandler onLine)
    {
        var lines = 0;
        if (_pendingLen > 0)
        {
            try
            {
                if (_pendingOversized) Oversized();
                else if (Deliver(_pending.AsSpan(0, _pendingLen), onLine)) lines++;
            }
            catch
            {
                // Keep the file open: the next poll reads the last line again and completes the file then.
                Rewind();
                throw;
            }
            Offset += _pendingLen;
            _pendingLen = 0;
            _pendingOversized = false;
        }
        if (FileId is not null)
        {
            _completed.AddLast((FileId, Offset));
            if (_completed.Count > 64) _completed.RemoveFirst();
        }
        FilesCompleted++;
        _handle?.Dispose();
        _handle = null;
        return lines;
    }

    /// <summary>A backup file; Id null when it exists but could not be opened (a transient sharing/access problem).</summary>
    private readonly record struct Backup(string Path, string? Id, DateTime LastWriteUtc);

    private List<Backup> Backups() => Backups(out _);

    private List<Backup> Backups(out bool unopenable)
    {
        unopenable = false;
        var result = new List<Backup>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_dir, _backupPattern).ToList(); }
        catch (Exception ex) when (IsFileError(ex)) { return result; }
        foreach (var f in files)
        {
            if (string.Equals(Path.GetFullPath(f), _path, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var h = FileIdentity.OpenShared(f);
                result.Add(new Backup(f, FileIdentity.Of(h), SafeLastWrite(h)));
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                if (FileIdentity.IsMissing(ex)) continue; // deleted meanwhile
                // Exists but cannot be opened now: keep it (by path; its metadata is readable without opening it).
                unopenable = true;
                DateTime lastWrite;
                try { lastWrite = File.GetLastWriteTimeUtc(f); }
                catch (Exception ex2) when (IsFileError(ex2)) { continue; }
                result.Add(new Backup(f, null, lastWrite));
            }
        }
        return result;
    }

    /// <summary>
    /// Backups rotated after the file <paramref name="afterId"/> (last written at <paramref name="afterWrite"/>), oldest
    /// first. ≥ rather than &gt; so a coarse timestamp resolution cannot skip one; files already read are excluded by identity.
    /// A backup that cannot be opened now is included by path: when it is opened later and turns out to be a file already
    /// read, <see cref="OpenFile"/> continues at the offset where that file was finished (nothing is read twice).
    /// </summary>
    private IEnumerable<string> NewerBackups(string? afterId, DateTime afterWrite, string? liveId) =>
        Backups()
            .Where(b => b.Id is null || (b.Id != afterId && b.Id != liveId && !_completed.Any(c => c.Id == b.Id)))
            .Where(b => b.LastWriteUtc >= afterWrite)
            .OrderBy(b => b.LastWriteUtc).ThenBy(b => b.Path, StringComparer.Ordinal)
            .Select(b => b.Path);

    private static DateTime SafeLastWrite(SafeFileHandle h)
    {
        try { return File.GetLastWriteTimeUtc(h); }
        catch (Exception ex) when (IsFileError(ex)) { return DateTime.MinValue; }
    }

    private static bool IsFileError(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException;

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
