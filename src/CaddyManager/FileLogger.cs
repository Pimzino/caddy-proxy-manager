using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace CaddyManager;

/// <summary>
/// Minimal daily-rolling file logger: logs/manager/manager-yyyyMMdd.log (UTF-8 without BOM, so the files concatenate and
/// grep cleanly), <see cref="RetentionDays"/> days kept — pruned at start-up and whenever the day rolls over.
/// A single background thread owns the open file; the file is shared for reading (log viewer) and deletion.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Prune();
        _writer = new Thread(Write) { IsBackground = true, Name = "file-logger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void Write()
    {
        StreamWriter? writer = null;
        string? day = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    if (writer is null || today != day)
                    {
                        var rolled = day is not null && today != day;
                        writer?.Dispose();
                        writer = null;
                        day = today;
                        var stream = new FileStream(Path.Combine(_dir, $"manager-{today}.log"), FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
                        if (rolled) Prune();
                    }
                    writer.Write(line);
                    if (_queue.Count == 0) writer.Flush();
                }
                catch
                {
                    // Never throw from logging; reopen on the next line (e.g. the file was deleted or the disk was full).
                    try { writer?.Dispose(); } catch { /* ignore */ }
                    writer = null;
                }
            }
        }
        finally
        {
            try { writer?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Deletes manager-yyyyMMdd.log files older than <see cref="RetentionDays"/> days (by the date in the name).</summary>
    private void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("manager-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(f.Name)["manager-".Length..];
                var date = DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d : f.LastWriteTime.Date;
                if (date < cutoff)
                {
                    try { f.Delete(); } catch { /* in use or no permission: try again next roll */ }
                }
            }
        }
        catch { /* never throw from logging */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // Let the writer drain and flush what is queued (shutdown messages).
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "DBG",
            };
            var shortCat = category[(category.LastIndexOf('.') + 1)..];
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
                .Append(level).Append(' ').Append(shortCat).Append(": ").Append(Sanitize(formatter(state, exception))).AppendLine();
            if (exception is not null) sb.AppendLine(exception.ToString());
            provider.Enqueue(sb.ToString());
        }

        /// <summary>
        /// Messages embed client-supplied values (e-mail addresses, paths, host names). Continuation lines are
        /// indented and other control characters escaped, so a crafted value cannot forge a new log entry.
        /// </summary>
        internal static string Sanitize(string message)
        {
            if (message.AsSpan().IndexOfAnyInRange('\0', '\u001f') < 0 && !message.Contains('\u007f')) return message;
            var sb = new StringBuilder(message.Length + 16);
            for (var i = 0; i < message.Length; i++)
            {
                var c = message[i];
                if (c == '\r')
                {
                    if (i + 1 < message.Length && message[i + 1] == '\n') continue;
                    sb.Append(Environment.NewLine).Append("    ");
                }
                else if (c == '\n') sb.Append(Environment.NewLine).Append("    ");
                else if (c == '\t') sb.Append(c);
                else if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
