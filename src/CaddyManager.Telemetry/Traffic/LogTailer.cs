using Microsoft.Win32.SafeHandles;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>Receives one complete log line (without the line break).</summary>
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
    private readonly LinkedList<string> _completed = new();

    private TrafficCursorDoc? _resumeFrom;
    private SafeFileHandle? _handle;
    private bool _handleIsLive;
    private long _readPos;
    private byte[] _pending = new byte[4096];
    private int _pendingLen;
    private bool _pendingOversized;

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

    /// <summary>Reads everything available. Returns the number of lines delivered.</summary>
    public int Poll(LineHandler onLine)
    {
        var lines = 0;
        if (_resumeFrom is not null)
        {
            Resume(_resumeFrom);
            _resumeFrom = null;
        }
        // Bounded: each iteration either returns or finishes one file.
        for (var guard = 0; guard < 1000; guard++)
        {
            if (_handle is null)
            {
                if (_chain.Count > 0)
                {
                    OpenFile(_chain.Dequeue(), 0, live: false);
                    continue;
                }
                if (!OpenFile(_path, 0, live: true)) return lines;
            }

            lines += ReadAvailable(onLine);
            if (!_handleIsLive)
            {
                lines += CompleteFile(onLine);
                continue;
            }

            var pathId = FileIdentity.OfPath(_path);
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

    private void Resume(TrafficCursorDoc saved)
    {
        // Prefer the live file: open it first so its identity and the handle we read from are the same file.
        SafeFileHandle? live = null;
        try { live = FileIdentity.OpenShared(_path); } catch (Exception ex) when (IsFileError(ex)) { }
        var liveId = live is null ? null : FileIdentity.Of(live);
        if (live is not null && liveId == saved.FileId)
        {
            var length = RandomAccess.GetLength(live);
            AdoptHandle(live, liveId!, saved.Offset <= length ? saved.Offset : 0, isLive: true);
            return;
        }
        live?.Dispose();

        // The saved file was rotated while we were not running: find it among the backups.
        var backups = Backups();
        var match = backups.FirstOrDefault(b => b.Id == saved.FileId);
        if (match.Path is not null)
        {
            SafeFileHandle? h = null;
            try { h = FileIdentity.OpenShared(match.Path); } catch (Exception ex) when (IsFileError(ex)) { }
            if (h is not null && FileIdentity.Of(h) == saved.FileId)
            {
                var length = RandomAccess.GetLength(h);
                AdoptHandle(h, saved.FileId!, Math.Min(saved.Offset, length), isLive: false);
                foreach (var b in NewerBackups(saved.FileId, match.LastWriteUtc, liveId)) _chain.Enqueue(b);
                return;
            }
            h?.Dispose();
        }

        // Gone (deleted by roll_keep): what followed it is still readable.
        FilesMissed++;
        foreach (var b in NewerBackups(saved.FileId, saved.FileLastWriteUtc, liveId)) _chain.Enqueue(b);
        FileId = null;
        Offset = 0;
    }

    private bool OpenFile(string path, long offset, bool live)
    {
        SafeFileHandle h;
        try { h = FileIdentity.OpenShared(path); }
        catch (Exception ex) when (IsFileError(ex))
        {
            // A queued backup deleted by roll_keep before we got to it.
            if (!live) FilesMissed++;
            return false;
        }
        AdoptHandle(h, FileIdentity.Of(h), offset, live);
        return true;
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
                if (_pendingOversized) OversizedLines++;
                else if (_pendingLen > 0)
                {
                    AppendPending(span[..nl]);
                    if (!_pendingOversized && Deliver(_pending.AsSpan(0, _pendingLen), onLine)) lines++;
                    else if (_pendingOversized) OversizedLines++;
                }
                else if (Deliver(span[..nl], onLine)) lines++;
                Offset += consumed;
                _pendingLen = 0;
                _pendingOversized = false;
                span = span[(nl + 1)..];
            }
        }
        if (readAny) FileLastWriteUtc = SafeLastWrite(_handle!);
        return lines;
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
            if (_pendingOversized) OversizedLines++;
            else if (Deliver(_pending.AsSpan(0, _pendingLen), onLine)) lines++;
            Offset += _pendingLen;
            _pendingLen = 0;
            _pendingOversized = false;
        }
        if (FileId is not null)
        {
            _completed.AddLast(FileId);
            if (_completed.Count > 64) _completed.RemoveFirst();
        }
        FilesCompleted++;
        _handle?.Dispose();
        _handle = null;
        return lines;
    }

    private readonly record struct Backup(string Path, string Id, DateTime LastWriteUtc);

    private List<Backup> Backups()
    {
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
            catch (Exception ex) when (IsFileError(ex)) { }
        }
        return result;
    }

    /// <summary>
    /// Backups rotated after the file <paramref name="afterId"/> (last written at <paramref name="afterWrite"/>), oldest
    /// first. ≥ rather than &gt; so a coarse timestamp resolution cannot skip one; files already read are excluded by identity.
    /// </summary>
    private IEnumerable<string> NewerBackups(string? afterId, DateTime afterWrite, string? liveId) =>
        Backups()
            .Where(b => b.Id != afterId && b.Id != liveId && !_completed.Contains(b.Id) && b.LastWriteUtc >= afterWrite)
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
