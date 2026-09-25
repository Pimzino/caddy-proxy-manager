using System.Collections.Concurrent;
using System.Text;

namespace CaddyManager;

/// <summary>Minimal daily-rolling file logger (logs/manager/manager-yyyyMMdd.log, 14 days kept).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _writer = new Thread(Write) { IsBackground = true, Name = "file-logger" };
        _writer.Start();
        Prune();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var file = Path.Combine(_dir, $"manager-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch { /* never throw from logging */ }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_dir).GetFiles("manager-*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14)))
                f.Delete();
        }
        catch { }
    }

    public void Dispose() => _queue.CompleteAdding();

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
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
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
