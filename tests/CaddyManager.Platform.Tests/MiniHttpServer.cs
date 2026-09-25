using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// Minimal HTTP/1.1 server on a loopback TCP socket (one request per connection, Content-Length bodies) that records the
/// request lines. Unlike HttpListener it needs no URL ACL, so it also works for a non-elevated user on Windows.
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, string, (int Status, byte[] Body)?> _respond;
    private readonly List<string> _requests = [];

    /// <param name="respond">(method, path) → response, or null for 404.</param>
    /// <param name="port">Loopback port; 0 picks a free one.</param>
    public MiniHttpServer(Func<string, string, (int Status, byte[] Body)?> respond, int port = 0)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Base => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>"METHOD /path" of every request so far.</summary>
    public List<string> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(buffer, _cts.Token) == 0) return;
                    head.Append((char)buffer[0]);
                }
                var lines = head.ToString().Split("\r\n");
                var parts = lines[0].Split(' ');
                var (method, target) = (parts[0], parts.Length > 1 ? parts[1] : "/");
                var path = target.Split('?')[0];
                var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
                var body = new byte[length];
                for (var read = 0; read < length;)
                {
                    var n = await stream.ReadAsync(body.AsMemory(read), _cts.Token);
                    if (n == 0) break;
                    read += n;
                }
                lock (_requests) _requests.Add($"{method} {path}");
                var (status, content) = _respond(method, path) ?? (404, []);
                var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                await stream.WriteAsync(content, _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
