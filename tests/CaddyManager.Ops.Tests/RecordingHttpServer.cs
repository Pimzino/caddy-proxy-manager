using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// A real HTTP/1.1 server on a TCP socket that records every request. Used as an origin (origin-form "POST /hook") and
/// as a forward proxy (absolute-form "POST http://host/path", RFC 9112 §3.2.2): it answers proxied requests itself, so
/// the destination host never has to exist. With <see cref="ProxyCredentials"/> set it demands Basic proxy
/// authentication (407 + Proxy-Authenticate) like a corporate proxy. Handles Content-Length and chunked bodies.
/// </summary>
public sealed class RecordingHttpServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Target, bool AbsoluteForm, string? Host, Dictionary<string, string> Headers, string Body);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<Request, (int Status, string ContentType, string Body)> _respond;

    public ConcurrentQueue<Request> Requests { get; } = new();
    /// <summary>"user:password" required in Proxy-Authorization (Basic); null = no proxy authentication.</summary>
    public string? ProxyCredentials { get; init; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public RecordingHttpServer(Func<Request, (int Status, string ContentType, string Body)> respond, IPAddress? address = null)
    {
        _respond = respond;
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
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
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var requestLine = await ReadLineAsync(stream);
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await ReadLineAsync(stream) is { Length: > 0 } line)
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var body = await ReadBodyAsync(stream, headers);
            var absolute = parts[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var host = absolute ? new Uri(parts[1]).Host : headers.GetValueOrDefault("Host");
            var request = new Request(parts[0], parts[1], absolute, host, headers, body);
            Requests.Enqueue(request);

            if (ProxyCredentials is not null && absolute)
            {
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ProxyCredentials));
                if (headers.GetValueOrDefault("Proxy-Authorization") != expected)
                {
                    await WriteAsync(stream, 407, "text/plain", "proxy authentication required", "Proxy-Authenticate: Basic realm=\"cpm-e2e\"\r\n");
                    return;
                }
            }
            var (status, type, text) = _respond(request);
            await WriteAsync(stream, status, type, text, "");
        }
        catch (IOException) { /* client went away */ }
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, string body, string extraHeaders)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(HttpStatusCode)status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n{extraHeaders}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, Dictionary<string, string> headers)
    {
        var buffer = new MemoryStream();
        if (headers.TryGetValue("Content-Length", out var len) && int.TryParse(len, out var n))
        {
            await ReadExactAsync(stream, buffer, n);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream) ?? "0";
                var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0) { await ReadLineAsync(stream); break; }
                await ReadExactAsync(stream, buffer, size);
                await ReadLineAsync(stream);
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task ReadExactAsync(NetworkStream stream, MemoryStream into, int count)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var r = await stream.ReadAsync(buf.AsMemory(read, count - read));
            if (r == 0) break;
            read += r;
        }
        into.Write(buf, 0, read);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var r = await stream.ReadAsync(one);
            if (r == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* stopped */ }
    }
}
