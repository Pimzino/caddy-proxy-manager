using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// Minimal plain-text SMTP server (EHLO/MAIL/RCPT/DATA/QUIT) capturing received messages. With
/// <c>oauth: true</c> it advertises AUTH XOAUTH2 and records the decoded SASL initial responses.
/// </summary>
public sealed class SmtpTestServer : IAsyncDisposable
{
    public sealed record Received(string From, List<string> To, string Data);

    private readonly bool _oauth;
    /// <summary>Decoded XOAUTH2 initial responses ("user=...^Aauth=Bearer ...^A^A").</summary>
    public ConcurrentQueue<string> AuthAttempts { get; } = new();
    /// <summary>When set, only this bearer token is accepted.</summary>
    public string? AcceptToken { get; set; }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public ConcurrentQueue<Received> Messages { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public SmtpTestServer(bool oauth = false)
    {
        _oauth = oauth;
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 localhost test SMTP");
        string from = "";
        var to = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) return;
            var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
            switch (cmd)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-localhost");
                    if (_oauth) await writer.WriteLineAsync("250-AUTH XOAUTH2");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "AUTH":
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var decoded = parts.Length >= 3 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])) : "";
                    AuthAttempts.Enqueue(decoded);
                    if (AcceptToken is null || decoded.Contains("auth=Bearer " + AcceptToken + "\x01", StringComparison.Ordinal))
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    else
                        await writer.WriteLineAsync("535 5.7.3 Authentication unsuccessful");
                    break;
                case "HELO":
                    await writer.WriteLineAsync("250 localhost");
                    break;
                case "MAIL":
                    from = Address(line);
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "RCPT":
                    to.Add(Address(line));
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var sb = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        sb.AppendLine(dataLine.StartsWith("..") ? dataLine[1..] : dataLine);
                    Messages.Enqueue(new Received(from, to.ToList(), sb.ToString()));
                    to.Clear();
                    await writer.WriteLineAsync("250 OK queued");
                    break;
                case "RSET":
                case "NOOP":
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    return;
                default:
                    await writer.WriteLineAsync("502 Command not implemented");
                    break;
            }
        }
    }

    private static string Address(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>', start + 1);
        return start >= 0 && end > start ? line[(start + 1)..end] : line;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* stopped */ }
    }
}
