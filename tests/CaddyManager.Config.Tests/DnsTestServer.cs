using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Minimal authoritative DNS server for a few test zones (UDP + TCP on 127.0.0.1), used as the rfc2136 target of Caddy's
/// DNS-01 solver and as the resolver of Pebble's validation authority. It answers SOA/NS at each apex, A for the name
/// server, TXT from its record store, static CNAMEs (NOERROR/NODATA with the SOA otherwise, REFUSED outside its zones),
/// and accepts RFC 2136 UPDATE messages only when they carry a valid RFC 8945 TSIG (hmac-sha256) for the configured key
/// and target a writable zone (read-only zones answer REFUSED, unknown zones NOTAUTH, names outside the zone NOTZONE);
/// responses to signed updates are signed too (the client, miekg/dns, verifies them).
///
/// CNAMEs (challenge delegation): a query of another type at a CNAME owner is answered with the CNAME plus the records of
/// the target when the target is in one of the zones, restarting at most 8 times (RFC 1034 §4.3.2 step 3a) — the way a
/// recursive resolver answers, which Pebble's Go resolver relies on (it takes the TXT records from the same answer).
/// A CNAME query returns only the CNAME.
///
/// Ways it could fail (and what guards against each):
/// - Name compression pointers in received messages (miekg compresses names in UPDATE sections) → ReadName follows
///   pointers, with a hop limit against loops.
/// - TSIG MAC computed over the wrong bytes → the MAC covers the received message up to the TSIG RR with ARCOUNT - 1
///   and the original ID, then the TSIG variables in canonical (lower-case, uncompressed) form, exactly as RFC 8945
///   §4.3.3 and miekg's tsigBuffer; a wrong implementation makes every UPDATE fail (NOTAUTH), so the test fails loudly.
/// - Signing the response wrongly → miekg rejects the answer ("dns: bad signature") and Caddy's solver reports it.
/// - Clock skew / replay → time signed must be within the fudge of now (BADTIME otherwise).
/// - TCP framing (2-byte length prefix, several messages per connection, partial reads) → ReadExactly per frame.
/// - Case differences in names (certmagic/Pebble may use any case) → names are compared lower-case.
/// - Truncation → answers are small (a few TXT records), far below 512 bytes.
/// - Concurrent UDP/TCP handlers mutating the record store → all store access under one lock.
/// - Port already taken → the listener binds port 0 for TCP first and reuses that port for UDP, retrying on conflicts.
/// - CNAME loops in the static data → the chase stops after 8 restarts.
/// - Overlapping zones (a.test inside test.) → the longest matching zone owns a name.
/// </summary>
public sealed class DnsTestServer : IAsyncDisposable
{
    /// <summary>One RR of an UPDATE. TsigValid: the signature verified; Rcode: the server's answer (0 = applied).</summary>
    public sealed record UpdateSeen(string Zone, string Operation, string Name, string Type, string? Data, bool TsigValid, string? KeyName, string? Error, int Rcode = 0);
    public sealed record QuerySeen(string Transport, string Name, string Type, int Rcode, int Answers);

    private const ushort TypeA = 1, TypeNs = 2, TypeCname = 5, TypeSoa = 6, TypeTxt = 16, TypeAny = 255, TypeTsig = 250;
    private const ushort ClassIn = 1, ClassNone = 254, ClassAny = 255;

    private readonly Dictionary<string, bool> _zones = new(StringComparer.Ordinal); // "dns01.test." → writable
    private readonly Dictionary<string, string> _cnames = new(StringComparer.Ordinal); // owner → target, both FQDN lower case
    private readonly string _keyName;     // "cpm-e2e." lower case
    private readonly byte[] _key;
    private readonly UdpClient _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    private readonly Dictionary<string, List<string>> _txt = new(StringComparer.Ordinal);
    private readonly List<UpdateSeen> _updates = new();
    private readonly List<QuerySeen> _queries = new();

    public int Port { get; }
    public string Endpoint => $"127.0.0.1:{Port}";
    /// <summary>The first zone.</summary>
    public string Zone => _zones.Keys.First().TrimEnd('.');

    /// <summary>One writable zone.</summary>
    public DnsTestServer(string zone, string keyName, byte[] key) : this([(zone, true)], keyName, key) { }

    /// <summary>Several zones; only the writable ones accept UPDATEs.</summary>
    public DnsTestServer(IEnumerable<(string Zone, bool Writable)> zones, string keyName, byte[] key)
    {
        foreach (var (zone, writable) in zones) _zones[Fqdn(zone)] = writable;
        _keyName = Fqdn(keyName);
        _key = key;
        for (var attempt = 0; ; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                _tcp = tcp;
                Port = port;
                break;
            }
            catch (SocketException) when (attempt < 20)
            {
                tcp.Stop();
            }
        }
        _loops.Add(Task.Run(UdpLoopAsync));
        _loops.Add(Task.Run(TcpLoopAsync));
    }

    public IReadOnlyList<UpdateSeen> Updates { get { lock (_txt) return _updates.ToList(); } }
    public IReadOnlyList<QuerySeen> Queries { get { lock (_txt) return _queries.ToList(); } }

    /// <summary>TXT values currently stored for a name.</summary>
    public IReadOnlyList<string> Txt(string name) { lock (_txt) return _txt.TryGetValue(Fqdn(name), out var l) ? l.ToList() : []; }

    /// <summary>Adds a static CNAME (the owner must be inside one of the zones).</summary>
    public void AddCname(string owner, string target) { lock (_txt) _cnames[Fqdn(owner)] = Fqdn(target); }

    /// <summary>Adds a static TXT record (e.g. a leftover manual challenge record).</summary>
    public void AddTxt(string name, string value)
    {
        lock (_txt)
        {
            if (!_txt.TryGetValue(Fqdn(name), out var list)) _txt[Fqdn(name)] = list = new();
            list.Add(value);
        }
    }

    private static string Fqdn(string name) => (name.EndsWith('.') ? name : name + ".").ToLowerInvariant();

    /// <summary>The zone a name belongs to (longest match), or null.</summary>
    private string? ZoneOf(string fqdn) =>
        _zones.Keys.Where(z => fqdn == z || fqdn.EndsWith("." + z, StringComparison.Ordinal)).OrderByDescending(z => z.Length).FirstOrDefault();

    // ------------------------------------------------------------------ transports

    private async Task UdpLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable reported on Windows
            byte[]? answer;
            try { answer = Handle(r.Buffer, "udp"); }
            catch (Exception) { answer = null; }
            if (answer is null) continue;
            try { await _udp.SendAsync(answer, r.RemoteEndPoint, _cts.Token); } catch (Exception) when (!_cts.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TcpLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var len = new byte[2];
                        while (!_cts.IsCancellationRequested)
                        {
                            await stream.ReadExactlyAsync(len, _cts.Token);
                            var msg = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                            await stream.ReadExactlyAsync(msg, _cts.Token);
                            var answer = Handle(msg, "tcp");
                            if (answer is null) return;
                            var framed = new byte[answer.Length + 2];
                            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)answer.Length);
                            answer.CopyTo(framed, 2);
                            await stream.WriteAsync(framed, _cts.Token);
                        }
                    }
                    catch (Exception)
                    {
                        // client closed the connection (EndOfStream) or the server is stopping
                    }
                }
            });
        }
    }

    // ------------------------------------------------------------------ message handling

    private sealed record Rr(string Name, ushort Type, ushort Class, uint Ttl, byte[] RData, int RDataOffset, int Start);

    private byte[]? Handle(byte[] msg, string transport)
    {
        if (msg.Length < 12) return null;
        var id = BinaryPrimitives.ReadUInt16BigEndian(msg);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(2));
        if ((flags & 0x8000) != 0) return null; // a response
        var opcode = (flags >> 11) & 0xF;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        var an = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
        var ns = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8));
        var ar = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(10));
        var off = 12;
        var questions = new List<(string Name, ushort Type, ushort Class)>();
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(msg, ref off);
            questions.Add((name, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)), BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off + 2))));
            off += 4;
        }
        var sections = new List<Rr>[3] { new(), new(), new() };
        var counts = new[] { an, ns, ar };
        for (var s = 0; s < 3; s++)
            for (var i = 0; i < counts[s]; i++) sections[s].Add(ReadRr(msg, ref off));

        if (opcode == 5) return HandleUpdate(msg, id, flags, questions, sections[1], sections[2]);
        if (opcode != 0 || questions.Count != 1) return Header(id, flags, rcode: 4 /* NOTIMP */, questions, [], []);

        var (qname, qtype, _) = questions[0];
        var lname = qname.ToLowerInvariant();
        var answers = new List<byte[]>();
        var authority = new List<byte[]>();
        int rcode;
        if (ZoneOf(lname) is not { } zone)
        {
            rcode = 5; // REFUSED: not authoritative
        }
        else
        {
            rcode = 0;
            lock (_txt)
            {
                var name = lname;
                for (var restarts = 0; ; restarts++)
                {
                    if (_cnames.TryGetValue(name, out var target))
                    {
                        answers.Add(RrBytes(name, TypeCname, 60, NameBytes(target)));
                        // Chase within our zones unless the CNAME itself was asked for (RFC 1034 §4.3.2 step 3a).
                        if (qtype == TypeCname || restarts >= 8 || ZoneOf(target) is not { } targetZone) break;
                        name = target;
                        zone = targetZone;
                        continue;
                    }
                    if (qtype is TypeSoa or TypeAny && name == zone) answers.Add(SoaRr(zone));
                    if (qtype is TypeNs or TypeAny && name == zone) answers.Add(RrBytes(zone, TypeNs, 60, NameBytes("ns1." + zone)));
                    if (qtype is TypeA or TypeAny && name == "ns1." + zone) answers.Add(RrBytes(name, TypeA, 60, [127, 0, 0, 1]));
                    if (qtype is TypeTxt or TypeAny && _txt.TryGetValue(name, out var values))
                        foreach (var v in values) answers.Add(RrBytes(name, TypeTxt, 1, TxtRData(v)));
                    break;
                }
            }
            if (answers.Count == 0) authority.Add(SoaRr(zone));
        }
        lock (_txt) _queries.Add(new QuerySeen(transport, lname, TypeName(qtype), rcode, answers.Count));
        return Header(id, flags, rcode, questions, answers, authority);
    }

    private byte[] HandleUpdate(byte[] msg, ushort id, ushort flags, List<(string Name, ushort Type, ushort Class)> zoneSection, List<Rr> updates, List<Rr> additional)
    {
        var zone = zoneSection.Count == 1 ? zoneSection[0].Name.ToLowerInvariant() : "";
        var tsig = additional.Count > 0 && additional[^1].Type == TypeTsig ? additional[^1] : null;
        string? error = null;
        byte[]? requestMac = null;
        var rcode = 0;
        if (tsig is null) error = "no TSIG";
        else error = VerifyTsig(msg, tsig, out requestMac);
        var tsigValid = tsig is not null && error is null;
        if (error is not null) rcode = 9; // NOTAUTH for a missing/invalid signature
        else if (!_zones.TryGetValue(zone, out var writable)) (rcode, error) = (9, $"not authoritative for zone '{zone}' (NOTAUTH)");
        else if (!writable) (rcode, error) = (5, $"zone '{zone}' is read-only (REFUSED)");
        else if (updates.Any(rr => !(rr.Name.ToLowerInvariant() == zone || rr.Name.ToLowerInvariant().EndsWith("." + zone, StringComparison.Ordinal))))
            (rcode, error) = (10, $"a record name is outside zone '{zone}' (NOTZONE)");

        var ops = new List<UpdateSeen>();
        foreach (var rr in updates)
        {
            var name = rr.Name.ToLowerInvariant();
            string op;
            string? data = rr.Type == TypeTxt && rr.RData.Length > 0 ? ParseTxt(rr.RData) : null;
            if (rr.Class == ClassIn) op = "add";
            else if (rr.Class == ClassNone) op = "delete";
            else if (rr.Class == ClassAny) op = rr.Type == TypeAny ? "delete-name" : "delete-rrset";
            else op = "class-" + rr.Class;
            ops.Add(new UpdateSeen(zone, op, name, TypeName(rr.Type), data, tsigValid, tsig?.Name.ToLowerInvariant(), error, rcode));
            if (rcode != 0) continue; // RFC 2136 §3.7: an update is applied entirely or not at all
            lock (_txt)
            {
                switch (op)
                {
                    case "add" when rr.Type == TypeTxt && data is not null:
                        if (!_txt.TryGetValue(name, out var list)) _txt[name] = list = new();
                        if (!list.Contains(data)) list.Add(data);
                        break;
                    case "delete" when rr.Type == TypeTxt && data is not null:
                        if (_txt.TryGetValue(name, out var l2)) l2.Remove(data);
                        break;
                    case "delete-rrset" when rr.Type == TypeTxt:
                    case "delete-name":
                        _txt.Remove(name);
                        break;
                }
            }
        }
        if (ops.Count == 0) ops.Add(new UpdateSeen(zone, "none", "", "", null, tsigValid, tsig?.Name, error, rcode));
        lock (_txt) _updates.AddRange(ops);

        // Responses to validly signed requests are signed, refusals included.
        var response = Header(id, flags, rcode, zoneSection, [], []);
        return tsig is not null && requestMac is not null ? Sign(response, requestMac) : response;
    }

    // ------------------------------------------------------------------ TSIG (RFC 8945, hmac-sha256)

    private string? VerifyTsig(byte[] msg, Rr tsig, out byte[]? requestMac)
    {
        requestMac = null;
        var off = tsig.RDataOffset;
        var alg = ReadName(msg, ref off).ToLowerInvariant();
        var timeSigned = ReadUInt48(msg, off); off += 6;
        var fudge = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)); off += 2;
        var macSize = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)); off += 2;
        var mac = msg.AsSpan(off, macSize).ToArray(); off += macSize;
        var origId = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)); off += 2;
        var tsigError = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)); off += 2;
        var otherLen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off)); off += 2;
        var other = msg.AsSpan(off, otherLen).ToArray();

        if (tsig.Name.ToLowerInvariant() != _keyName) return $"unknown TSIG key '{tsig.Name}'";
        if (alg != "hmac-sha256.") return $"unsupported TSIG algorithm '{alg}'";

        // The signed message: everything before the TSIG RR, ARCOUNT - 1, the original ID.
        var signed = msg.AsSpan(0, tsig.Start).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(signed, origId);
        BinaryPrimitives.WriteUInt16BigEndian(signed.AsSpan(10), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(signed.AsSpan(10)) - 1));
        var expected = HMACSHA256.HashData(_key, Concat(signed, Variables(_keyName, alg, timeSigned, fudge, tsigError, other)));
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) return "TSIG MAC mismatch (BADSIG)";
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now > timeSigned + fudge || timeSigned > now + fudge) return "TSIG time outside the fudge window (BADTIME)";
        requestMac = mac;
        return null;
    }

    /// <summary>Appends a TSIG RR to a response: MAC over (request MAC with its length, response, TSIG variables).</summary>
    private byte[] Sign(byte[] response, byte[] requestMac)
    {
        const string alg = "hmac-sha256.";
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const ushort fudge = 300;
        var prefix = new byte[2 + requestMac.Length];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)requestMac.Length);
        requestMac.CopyTo(prefix, 2);
        var mac = HMACSHA256.HashData(_key, Concat(prefix, response, Variables(_keyName, alg, now, fudge, 0, [])));

        var rdata = new List<byte>(NameBytes(alg));
        var t = new byte[6];
        WriteUInt48(t, now);
        rdata.AddRange(t);
        rdata.AddRange(U16(fudge));
        rdata.AddRange(U16((ushort)mac.Length));
        rdata.AddRange(mac);
        rdata.AddRange(U16(BinaryPrimitives.ReadUInt16BigEndian(response)));
        rdata.AddRange(U16(0));
        rdata.AddRange(U16(0));
        var rr = Concat(NameBytes(_keyName), U16(TypeTsig), U16(ClassAny), U32(0), U16((ushort)rdata.Count), rdata.ToArray());
        var signed = Concat(response, rr);
        BinaryPrimitives.WriteUInt16BigEndian(signed.AsSpan(10), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(signed.AsSpan(10)) + 1));
        return signed;
    }

    private static byte[] Variables(string keyName, string alg, ulong timeSigned, ushort fudge, ushort error, byte[] other)
    {
        var t = new byte[6];
        WriteUInt48(t, timeSigned);
        return Concat(NameBytes(keyName), U16(ClassAny), U32(0), NameBytes(alg), t, U16(fudge), U16(error), U16((ushort)other.Length), other);
    }

    // ------------------------------------------------------------------ wire helpers

    private static byte[] SoaRr(string zone)
    {
        var rdata = Concat(NameBytes("ns1." + zone), NameBytes("hostmaster." + zone), U32(2026092501), U32(3600), U32(600), U32(86400), U32(60));
        return RrBytes(zone, TypeSoa, 60, rdata);
    }

    private static byte[] Header(ushort id, ushort requestFlags, int rcode, List<(string Name, ushort Type, ushort Class)> questions,
        List<byte[]> answers, List<byte[]> authority)
    {
        // QR, opcode copied, AA, RD copied, RA, rcode.
        var flags = (ushort)(0x8000 | (requestFlags & 0x7800) | 0x0400 | (requestFlags & 0x0100) | 0x0080 | (rcode & 0xF));
        var parts = new List<byte[]> { U16(id), U16(flags), U16((ushort)questions.Count), U16((ushort)answers.Count), U16((ushort)authority.Count), U16(0) };
        foreach (var q in questions) parts.Add(Concat(NameBytes(q.Name), U16(q.Type), U16(q.Class)));
        parts.AddRange(answers);
        parts.AddRange(authority);
        return Concat(parts.ToArray());
    }

    private static byte[] RrBytes(string name, ushort type, uint ttl, byte[] rdata) =>
        Concat(NameBytes(name), U16(type), U16(ClassIn), U32(ttl), U16((ushort)rdata.Length), rdata);

    private static byte[] TxtRData(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var list = new List<byte>();
        for (var i = 0; i < bytes.Length || i == 0; i += 255)
        {
            var chunk = bytes.AsSpan(i, Math.Min(255, bytes.Length - i));
            list.Add((byte)chunk.Length);
            list.AddRange(chunk.ToArray());
            if (bytes.Length == 0) break;
        }
        return list.ToArray();
    }

    private static string ParseTxt(byte[] rdata)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < rdata.Length;)
        {
            var len = rdata[i++];
            sb.Append(Encoding.ASCII.GetString(rdata, i, Math.Min(len, rdata.Length - i)));
            i += len;
        }
        return sb.ToString();
    }

    /// <summary>Uncompressed, lower-case wire name (canonical form, RFC 4034 §6.2).</summary>
    private static byte[] NameBytes(string name)
    {
        var list = new List<byte>();
        foreach (var label in name.TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = Encoding.ASCII.GetBytes(label);
            list.Add((byte)b.Length);
            list.AddRange(b);
        }
        list.Add(0);
        return list.ToArray();
    }

    private static string ReadName(byte[] msg, ref int off)
    {
        var labels = new List<string>();
        var pos = off;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var len = msg[pos];
            if (len == 0)
            {
                if (!jumped) off = pos + 1;
                return string.Join('.', labels) + ".";
            }
            if ((len & 0xC0) == 0xC0)
            {
                var ptr = ((len & 0x3F) << 8) | msg[pos + 1];
                if (!jumped) off = pos + 2;
                jumped = true;
                pos = ptr;
                continue;
            }
            labels.Add(Encoding.ASCII.GetString(msg, pos + 1, len));
            pos += len + 1;
        }
        throw new InvalidDataException("DNS name compression loop");
    }

    private static Rr ReadRr(byte[] msg, ref int off)
    {
        var start = off;
        var name = ReadName(msg, ref off);
        var type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off));
        var cls = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off + 2));
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(off + 4));
        var len = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(off + 8));
        off += 10;
        var rr = new Rr(name, type, cls, ttl, msg.AsSpan(off, len).ToArray(), off, start);
        off += len;
        return rr;
    }

    private static ulong ReadUInt48(byte[] b, int off) =>
        ((ulong)BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(off)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 2));

    private static void WriteUInt48(byte[] b, ulong v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(v >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), (uint)v);
    }

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static string TypeName(ushort t) => t switch
    {
        TypeA => "A", TypeNs => "NS", TypeCname => "CNAME", TypeSoa => "SOA", TypeTxt => "TXT", TypeAny => "ANY", TypeTsig => "TSIG", 28 => "AAAA", 257 => "CAA", 41 => "OPT",
        _ => "TYPE" + t,
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _cts.Dispose();
    }
}
