using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CaddyManager.Telemetry.Traffic;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// Isolated tests of the rotation state machine (LogTailer) with real files rotated the way Caddy's timberjack writer does
/// it: the active file is renamed to "requests-&lt;time&gt;-size.log" and a new "requests.log" is created.
///
/// Ways the tailer could fail (written before the code):
///  1. Lines written to the old file between our last read and the rename are lost.
///  2. Lines are read twice: a saved offset applied to a different (new) file, or a backup re-read after it was finished.
///  3. Several rotations between two polls: an intermediate backup is skipped, or backups are read out of order.
///  4. A partial last line (no newline yet) is delivered early (split entry) or dropped when the rest arrives.
///  5. The partial last line of a file that has been rotated away is never delivered.
///  6. Restart: the saved position is not honoured (same file); a saved file that became a backup while the manager was
///     down is not found (lost tail) or is read from 0 (duplicates); a saved file that was deleted blocks progress.
///  7. A file truncated in place leaves the tailer stuck past EOF.
///  8. The log file does not exist yet, or is missing between rename and re-create → exception.
///  9. CRLF endings leave '\r' in lines; empty lines are delivered; an over-long line exhausts memory.
/// 10. Older backups (rotated before the saved file) are read again after a restart.
/// 11. The log path exists but cannot be opened for a moment (a third-party tool's sharing mode, access denied): taken as a
///     rotation, the live file is read again from offset 0 (double counting); at a restart, the saved file is declared
///     lost and reading starts over (TEL-3).
/// 12. The line handler throws: the rest of the 64 KB chunk is skipped and the offset no longer matches what was delivered,
///     so a restart resumes mid-line or re-delivers lines (TEL-7).
/// </summary>
public sealed class LogTailerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cpm-tailer-tests", Guid.NewGuid().ToString("N")[..10]);
    private readonly string _path;
    private readonly List<string> _got = new();
    private int _rotation;

    public LogTailerTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "requests.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Append(string text)
    {
        using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    private void Lines(int from, int to)
    {
        var sb = new StringBuilder();
        for (var i = from; i < to; i++) sb.Append("line-").Append(i).Append('\n');
        Append(sb.ToString());
    }

    /// <summary>timberjack-style rotation: rename the active file to a timestamped backup, create a new empty one.</summary>
    private string Rotate(bool createNew = true)
    {
        var backup = Path.Combine(_dir, $"requests-2026-09-25T10-00-{_rotation++:00}.000-size.log");
        File.Move(_path, backup);
        if (createNew) File.WriteAllBytes(_path, []);
        return backup;
    }

    private int Poll(LogTailer t) => t.Poll(l => _got.Add(Encoding.UTF8.GetString(l)));

    private static TrafficCursorDoc Cursor(LogTailer t) => new() { FileId = t.FileId, Offset = t.Offset, FileLastWriteUtc = t.FileLastWriteUtc };

    private void AssertSequence(int count) => Assert.Equal(Enumerable.Range(0, count).Select(i => $"line-{i}"), _got);

    [Fact]
    public void MissingFileIsNotAnError_ThenReadsFromStart()
    {
        using var t = new LogTailer(_path, null);
        Assert.Equal(0, Poll(t));
        Lines(0, 3);
        Assert.Equal(3, Poll(t));
        AssertSequence(3);
        // On Windows the native identity (volume + file index) is in use, not the creation-time fallback.
        Assert.StartsWith(OperatingSystem.IsWindows() ? "win:" : "ct:", t.FileId);
    }

    [Fact]
    public void PartialLineWaitsForItsNewline()
    {
        using var t = new LogTailer(_path, null);
        Append("line-0\nline-");
        Poll(t);
        Assert.Equal(["line-0"], _got);
        Assert.Equal(7, t.Offset); // after "line-0\n" only
        Append("1\r\n\n\nline-2\n");
        Poll(t);
        AssertSequence(3); // CRLF stripped, empty lines skipped
    }

    [Fact]
    public void WritesBeforeRenameAreDrained_ThenNewFileFromZero()
    {
        using var t = new LogTailer(_path, null);
        Lines(0, 10);
        Poll(t);
        Lines(10, 20);          // written after our last read …
        Append("line-20");      // … including a final line without newline
        Rotate();
        Lines(21, 25);
        Poll(t);
        AssertSequence(25);
        Assert.Equal(1, t.FilesCompleted);
    }

    [Fact]
    public void SeveralRotationsBetweenPolls_ReadInOrder()
    {
        using var t = new LogTailer(_path, null);
        Lines(0, 5);
        Poll(t);
        Lines(5, 10);
        Rotate();
        Lines(10, 15);
        Rotate();
        Lines(15, 20);
        Rotate();
        Lines(20, 25);
        Poll(t);
        AssertSequence(25);
        Assert.Equal(3, t.FilesCompleted);
        Poll(t);
        AssertSequence(25);
    }

    [Fact]
    public void RotationWithoutNewFileYet_WaitsThenContinues()
    {
        using var t = new LogTailer(_path, null);
        Lines(0, 5);
        Poll(t);
        Lines(5, 8);
        Rotate(createNew: false);
        Poll(t);
        AssertSequence(8);
        Lines(8, 12); // creates requests.log
        Poll(t);
        AssertSequence(12);
    }

    [Fact]
    public void ResumeInSameFileAtSavedOffset()
    {
        TrafficCursorDoc saved;
        using (var t = new LogTailer(_path, null))
        {
            Lines(0, 10);
            Poll(t);
            saved = Cursor(t);
        }
        Lines(10, 15);
        using var t2 = new LogTailer(_path, saved);
        Poll(t2);
        AssertSequence(15);
    }

    [Fact]
    public void ResumeWhenSavedFileWasRotatedWhileStopped_NoLossNoDuplicates()
    {
        // An older backup (rotated before the saved file) must not be read again.
        Lines(-5, 0);
        _got.Clear();
        Rotate();
        TrafficCursorDoc saved;
        using (var t = new LogTailer(_path, null))
        {
            Lines(0, 10);
            Poll(t);
            _got.Clear();
            _got.AddRange(Enumerable.Range(0, 10).Select(i => $"line-{i}"));
            saved = Cursor(t);
        }
        // While stopped: more lines into the saved file, two rotations, more lines.
        Lines(10, 20);
        Rotate();
        Lines(20, 30);
        Rotate();
        Lines(30, 35);
        using var t2 = new LogTailer(_path, saved);
        Poll(t2);
        AssertSequence(35);
        Assert.Equal(0, t2.FilesMissed);
    }

    [Fact]
    public void ResumeWhenSavedFileWasDeleted_CountsMissedAndContinuesWithNewer()
    {
        TrafficCursorDoc saved;
        using (var t = new LogTailer(_path, null))
        {
            Lines(0, 10);
            Poll(t);
            saved = Cursor(t);
        }
        Lines(10, 20);
        var gone = Rotate();
        File.Delete(gone); // roll_keep removed it before we got to it
        Lines(20, 30);
        Rotate();
        Lines(30, 35);
        using var t2 = new LogTailer(_path, saved);
        Poll(t2);
        Assert.Equal(1, t2.FilesMissed);
        Assert.Equal(Enumerable.Range(0, 10).Concat(Enumerable.Range(20, 15)).Select(i => $"line-{i}"), _got);
    }

    [Fact]
    public void SavedOffsetBeyondEndOfSameFile_StartsOver()
    {
        using (var t = new LogTailer(_path, null))
        {
            Lines(0, 10);
            Poll(t);
            var saved = Cursor(t);
            // Truncated in place (not how Caddy rotates, but must not wedge the tailer).
            File.WriteAllBytes(_path, []);
            _got.Clear();
            Lines(0, 3);
            Poll(t);
            AssertSequence(3);
            _got.Clear();
            using var t2 = new LogTailer(_path, saved);
            Poll(t2);
            AssertSequence(3); // saved offset (after 10 lines) is past EOF of the 3-line file → from 0
        }
    }

    [Fact]
    public void OverlongLineIsSkippedAndCounted()
    {
        using var t = new LogTailer(_path, null, maxLineBytes: 100);
        Append("line-0\n" + new string('x', 150_000) + "\nline-1\n");
        Poll(t);
        AssertSequence(2);
        Assert.Equal(1, t.OversizedLines);
        Assert.Equal(new FileInfo(_path).Length, t.Offset);
    }

    /// <summary>
    /// Makes the file exist but not be openable for reading — as a tool holding it without read sharing, or an ACL, would.
    /// Windows: a deny-read ACE for the current user; elsewhere: mode 000. Handles opened before keep working.
    /// </summary>
    private static void Unopenable(string path, bool on)
    {
        if (OperatingSystem.IsWindows()) DenyRead(path, on);
        else File.SetUnixFileMode(path, on ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (on) Assert.ThrowsAny<UnauthorizedAccessException>(() => FileIdentity.OpenShared(path).Dispose());
    }

    [SupportedOSPlatform("windows")]
    private static void DenyRead(string path, bool deny)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        if (deny) acl.AddAccessRule(rule);
        else acl.RemoveAccessRule(rule);
        file.SetAccessControl(acl);
    }

    [Fact]
    public void PathTemporarilyUnopenable_IsNotARotation_NothingReadTwice()
    {
        using var t = new LogTailer(_path, null);
        Lines(0, 10);
        Poll(t);
        Lines(10, 15);
        Unopenable(_path, true);
        try
        {
            Poll(t); // the open handle still delivers the new lines; the path is not "gone"
            Poll(t);
            AssertSequence(15);
            Assert.Equal(0, t.FilesCompleted);
        }
        finally { Unopenable(_path, false); }
        Lines(15, 20);
        Poll(t);
        AssertSequence(20);
        Assert.Equal(new FileInfo(_path).Length, t.Offset);
    }

    [Fact]
    public void ResumeWhileTheFileIsUnopenable_WaitsInsteadOfDeclaringItLost()
    {
        TrafficCursorDoc saved;
        using (var t = new LogTailer(_path, null))
        {
            Lines(0, 10);
            Poll(t);
            saved = Cursor(t);
        }
        Lines(10, 15);
        using var t2 = new LogTailer(_path, saved);
        Unopenable(_path, true);
        try
        {
            Assert.Equal(0, Poll(t2));
            Assert.Equal(0, t2.FilesMissed);
        }
        finally { Unopenable(_path, false); }
        Poll(t2);
        AssertSequence(15);
        Assert.Equal(0, t2.FilesMissed);
    }

    [Fact]
    public void HandlerFailure_RewindsToThatLine_NoLineSkippedOrRepeated()
    {
        const int total = 20_000; // ~190 KB: several 64 KB chunks
        Lines(0, total);
        var failures = 1;
        using var t = new LogTailer(_path, null);
        LineHandler handler = l =>
        {
            var s = Encoding.UTF8.GetString(l);
            if (s == "line-7000" && failures-- > 0) throw new InvalidOperationException("handler failed");
            _got.Add(s);
        };
        Assert.Throws<InvalidOperationException>(() => t.Poll(handler));
        AssertSequence(7000);
        // The position is exactly the start of the failed line: a restart from it continues with that line.
        var saved = Cursor(t);
        using (var restarted = new LogTailer(_path, saved))
        {
            var after = new List<string>();
            restarted.Poll(l => after.Add(Encoding.UTF8.GetString(l)));
            Assert.Equal(Enumerable.Range(7000, total - 7000).Select(i => $"line-{i}"), after);
        }
        // The same tailer retries the line and continues.
        t.Poll(handler);
        AssertSequence(total);
        Assert.Equal(new FileInfo(_path).Length, t.Offset);
    }
}
