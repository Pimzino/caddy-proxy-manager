using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>Runs only when a Python with aioquic is configured (CPM_AIOQUIC_PYTHON) — .NET cannot send QUIC 0-RTT.</summary>
public sealed class ZeroRttFactAttribute : FactAttribute
{
    public ZeroRttFactAttribute()
    {
        if (CaddyBinary.Path is null) Skip = "Caddy binary not found (.dev/bin/caddy or CPM_TEST_CADDY).";
        else if (TlsE2ETests.AioquicPython is null)
            Skip = "Set CPM_AIOQUIC_PYTHON to a Python with aioquic (pip install aioquic) to run the HTTP/3 0-RTT test; .NET's QUIC client cannot send 0-RTT.";
    }
}

/// <summary>End-to-end checks of certificate selection and TLS behaviour with the real Caddy binary.</summary>
public sealed class TlsE2ETests
{
    internal static string? AioquicPython =>
        Environment.GetEnvironmentVariable("CPM_AIOQUIC_PYTHON") is { Length: > 0 } p && File.Exists(p) ? p : null;

    /// <summary>
    /// Every host gets the certificate of ITS OWN TLS mode even when a wildcard host of another mode covers it
    /// (research #4, #5, #77; Caddy v2.10+ prefers managed wildcards for subdomains, and loaded custom certificates
    /// suppress automation). ACME uses a directory on a closed loopback port, so no public CA is ever contacted.
    /// Ways it could fail: (1) an Internal exact host under an ACME wildcard gets no certificate of its own (Caddy
    /// waits for the wildcard, which can never be issued); (2) an Internal exact host under an uploaded custom wildcard
    /// is served the custom certificate (loaded certificates skip automation, and the wildcard SNI policy wins);
    /// (3) names that only the custom wildcard covers stop getting the custom certificate; (4) an ACME exact host
    /// under an ACME wildcard that cannot be issued (no DNS challenge) is never attempted on its own; (5) responses
    /// come from the wrong host; (6) the control (config without the fixes) shows the misbehaviour.
    /// </summary>
    [CaddyFact]
    public async Task Each_host_gets_a_certificate_of_its_own_tls_mode_under_wildcards_of_another_mode()
    {
        var report = E2EArtifacts.Report(nameof(Each_host_gets_a_certificate_of_its_own_tls_mode_under_wildcards_of_another_mode));
        var closed = Net.FreeTcpPort();
        using var c = new LiveCaddy(s =>
        {
            s.AcmeCa = AcmeCa.Custom;
            s.CustomAcmeDirectory = $"https://127.0.0.1:{closed}/acme/directory";
            s.AcmeEmail = "e2e@example.com";
        });
        SiteHost R(string domain, TlsMode tls, string body) => new() { Kind = HostKind.Response, Domains = [domain], Tls = tls, ResponseStatus = 200, ResponseBody = body, Compression = false };
        c.Add(R("*.w.test", TlsMode.Acme, "wild-acme"));
        c.Add(R("x.w.test", TlsMode.Internal, "x-internal"));
        c.Add(R("a2.w.test", TlsMode.Acme, "a2-acme"));
        using var wildcard = TestCerts.SelfSigned(["*.c.test"]);
        var row = c.AddUploadedCertificate("cwild", wildcard);
        var customHost = R("*.c.test", TlsMode.Custom, "c-custom");
        customHost.CertificateId = row.Id;
        c.Add(customHost);
        c.Add(R("y.c.test", TlsMode.Internal, "y-internal"));
        await c.StartAsync();

        var generated = c.S.Config.Generate();
        var fixedJson = JsonNode.Parse(generated.ToJson())!;

        // (6) CONTROL first, on a fresh Caddy: the config without automate / ignore_loaded_certificates / the
        // managed-under-custom SNI policy (what the generator produced before).
        var control = fixedJson.DeepClone();
        (control["apps"]!["tls"]!["certificates"] as JsonObject)?.Remove("automate");
        var srv0 = control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!.AsObject();
        srv0["automatic_https"]!.AsObject().Remove("ignore_loaded_certificates");
        var policies = srv0["tls_connection_policies"]!.AsArray();
        foreach (var p in policies.Where(p => p!["match"] is not null && p["certificate_selection"] is null).ToList()) policies.Remove(p);
        var logBefore = c.ProcessLog().Length;
        await c.LoadRawAsync(control);
        await Task.Delay(TimeSpan.FromSeconds(6)); // time enough for Caddy to issue internal certificates it manages
        var cx = await c.HttpsAsync("x.w.test");
        var cy = await c.HttpsAsync("y.c.test");
        var controlLog = c.ProcessLog()[logBefore..];
        report["control"] = new JsonObject
        {
            ["x.w.test"] = cx.ToJson(), ["y.c.test"] = cy.ToJson(),
            ["a2.w.test managed"] = Managed(controlLog, "a2.w.test"),
        };
        Assert.False(Managed(controlLog, "a2.w.test")); // (4) reproduced
        Assert.DoesNotContain("x.w.test", cx.DnsNames);    // (1) reproduced
        Assert.Contains("*.c.test", cy.DnsNames);          // (2) reproduced

        // The fixed config through the service.
        var logAfterControl = c.ProcessLog().Length;
        await c.ApplyAsync();
        var x = await c.HttpsUntilAsync("x.w.test", r => r.DnsNames.Contains("x.w.test"), TimeSpan.FromSeconds(20));
        var y = await c.HttpsUntilAsync("y.c.test", r => r.DnsNames.Contains("y.c.test"), TimeSpan.FromSeconds(20));
        var z = await c.HttpsAsync("z.c.test");
        var a2Attempted = await Wait.For(() => Task.FromResult(Managed(c.ProcessLog()[logAfterControl..], "a2.w.test")), TimeSpan.FromSeconds(15));
        report["fixed"] = new JsonObject { ["x.w.test"] = x.ToJson(), ["y.c.test"] = y.ToJson(), ["z.c.test"] = z.ToJson(), ["a2.w.test attempted"] = a2Attempted };
        report["automate"] = fixedJson["apps"]!["tls"]!["certificates"]?["automate"]?.DeepClone();

        Assert.Contains("x.w.test", x.DnsNames);                       // (1)
        Assert.Contains("Caddy Local Authority", x.Issuer);
        Assert.Equal("x-internal", x.Body);                            // (5)
        Assert.Contains("y.c.test", y.DnsNames);                       // (2)
        Assert.Contains("Caddy Local Authority", y.Issuer);
        Assert.Equal("y-internal", y.Body);
        Assert.Contains("*.c.test", z.DnsNames);                       // (3)
        Assert.Equal("c-custom", z.Body);
        Assert.True(a2Attempted, "a2.w.test (ACME, under an ACME wildcard without DNS challenge) was not managed on its own"); // (4)

        E2EArtifacts.Write("tls-wildcard-modes.json", report);
    }

    /// <summary>
    /// True when CertMagic tried to obtain a certificate for exactly this name (logger "tls.obtain"). The earlier
    /// "enabling automatic TLS certificate management" line lists every name, including those Caddy then skips.
    /// </summary>
    private static bool Managed(string log, string name) =>
        log.Split('\n').Any(l => l.Contains($"\"{name}\"", StringComparison.Ordinal) && l.Contains("\"logger\":\"tls.obtain\"", StringComparison.Ordinal));

    /// <summary>
    /// Caddyfile mode must never install a root certificate into the OS trust stores (research #10): Caddy installs
    /// every CA's root unless install_trust is explicitly false, and the service runs headless as LocalSystem.
    /// Safety: the completed config is checked BEFORE it is loaded, so a regression fails the test without Caddy ever
    /// trying to modify this machine's trust store.
    /// Ways it could fail: (1) the implicit "local" CA (tls internal) keeps install_trust unset; (2) a named CA from the
    /// Caddyfile's pki block (used by `issuer internal { ca corp }`) keeps it unset; (3) an explicit user choice is
    /// overwritten; (4) Caddy logs an install attempt ("installing root", "failed to install root", "already trusted");
    /// (5) the CAs stop issuing (sites break); (6) the control: the adapted Caddyfile really leaves install_trust unset.
    /// </summary>
    [CaddyFact]
    public async Task Caddyfile_mode_disables_trust_store_installation_for_every_ca()
    {
        var report = E2EArtifacts.Report(nameof(Caddyfile_mode_disables_trust_store_installation_for_every_ca));
        using var c = new LiveCaddy();
        var caddyfile = $$"""
            {
            	admin 127.0.0.1:{{c.AdminPort}}
            	http_port {{c.HttpPort}}
            	https_port {{c.HttpsPort}}
            	pki {
            		ca corp {
            			name "CPM E2E Corp CA"
            		}
            	}
            }
            https://local.test:{{c.HttpsPort}} {
            	tls internal
            	respond "local"
            }
            https://corp.test:{{c.HttpsPort}} {
            	tls {
            		issuer internal {
            			ca corp
            		}
            	}
            	respond "corp"
            }
            """;
        c.UpdateSettings(s => { s.Mode = ConfigMode.Caddyfile; s.RawCaddyfile = caddyfile; });
        await c.StartAsync();

        // (6) CONTROL (static): what `caddy adapt` produces on its own.
        var adapted = (await c.S.Config.AdaptCaddyfileAsync(caddyfile))!.Value.Json;
        var rawCas = JsonNode.Parse(adapted)!["apps"]?["pki"]?["certificate_authorities"] as JsonObject;
        report["adaptedCertificateAuthorities"] = rawCas?.DeepClone();
        Assert.NotNull(rawCas);
        Assert.Null(rawCas!["corp"]?["install_trust"]);
        Assert.Null(rawCas["local"]?["install_trust"]);

        // Safety gate: the completed config must disable trust installation for every CA before Caddy sees it.
        var completed = JsonNode.Parse(CaddyConfigGenerator.CompleteAdaptedConfig(adapted, c.S.Store.GetSettings<CaddySettings>(), c.S.Paths, []))!;
        var cas = completed["apps"]!["pki"]!["certificate_authorities"]!.AsObject();
        report["completedCertificateAuthorities"] = cas.DeepClone();
        Assert.Equal(["corp", "local"], cas.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(cas, kv => Assert.False(kv.Value!["install_trust"]!.GetValue<bool>())); // (1)(2)

        // (3) an explicit choice in the config is kept
        var explicitTrue = JsonNode.Parse(adapted)!;
        explicitTrue["apps"]!["pki"]!["certificate_authorities"]!["corp"]!["install_trust"] = true;
        var kept = JsonNode.Parse(CaddyConfigGenerator.CompleteAdaptedConfig(explicitTrue.ToJsonString(), c.S.Store.GetSettings<CaddySettings>(), c.S.Paths, []))!;
        Assert.True(kept["apps"]!["pki"]!["certificate_authorities"]!["corp"]!["install_trust"]!.GetValue<bool>());

        await c.ApplyAsync("caddyfile trust");
        var running = await c.RunningConfigAsync();
        Assert.All(running["apps"]!["pki"]!["certificate_authorities"]!.AsObject(), kv => Assert.False(kv.Value!["install_trust"]!.GetValue<bool>()));

        var local = await c.HttpsUntilAsync("local.test", r => r.Status == 200, TimeSpan.FromSeconds(20));
        var corp = await c.HttpsUntilAsync("corp.test", r => r.Status == 200, TimeSpan.FromSeconds(20));
        report["local.test"] = local.ToJson();
        report["corp.test"] = corp.ToJson();
        Assert.Equal("local", local.Body);                              // (5)
        Assert.Contains("Caddy Local Authority", local.Issuer);
        Assert.Equal("corp", corp.Body);
        Assert.Contains("CPM E2E Corp CA", corp.Issuer);

        var log = c.ProcessLog();
        var disabled = log.Split('\n').Where(l => l.Contains("trust store installation disabled")).ToList();
        report["trustLogLines"] = new JsonArray(disabled.Select(l => (JsonNode)l.Trim()).ToArray());
        Assert.Contains(disabled, l => l.Contains("pki.ca.local"));
        Assert.Contains(disabled, l => l.Contains("pki.ca.corp"));
        Assert.DoesNotContain("installing root", log, StringComparison.OrdinalIgnoreCase); // (4)
        Assert.DoesNotContain("failed to install root", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("already trusted", log, StringComparison.OrdinalIgnoreCase);

        E2EArtifacts.Write("caddyfile-install-trust.json", report);
    }

    /// <summary>
    /// HTTP/3 0-RTT with IP access lists (research #72). Caddy answers 425 Too Early to requests that arrive in QUIC
    /// 0-RTT early data when a remote_ip/client_ip matcher runs, and some clients never retry. The client is aioquic
    /// (Python): connection 1 fetches a session ticket, connection 2 resumes and — if the ticket allows it — sends the
    /// request as 0-RTT early data.
    /// Ways it could fail: (1) the resumed request gets 425 (early data accepted by the server); (2) resumption itself
    /// breaks (no session ticket, full handshakes only — that would be acceptable but must be visible);
    /// (3) the IP rule stops being applied (a denied address gets through); (4) HTTP/3 does not answer at all;
    /// (5) the control (allow_0rtt removed) does not reproduce the 425, i.e. the client never really sent 0-RTT;
    /// (6) a Caddy that is already running a 0-RTT QUIC listener (a config from an older version, HTTP/3 on) keeps
    /// accepting 0-RTT after the manager applies allow_0rtt=false, because Caddy shares the QUIC listener across
    /// reloads and keeps the Allow0RTT it was created with (listeners.go ListenQUIC, LoadOrNew).
    /// </summary>
    [ZeroRttFact]
    public async Task Http3_resumed_requests_on_ip_restricted_hosts_never_get_425()
    {
        var report = E2EArtifacts.Report(nameof(Http3_resumed_requests_on_ip_restricted_hosts_never_get_425));
        using var c = new LiveCaddy(s => s.EnableHttp3 = true);
        var allowLocal = new AccessList { Id = "local", Name = "Local only", Rules = [new IpRule { Action = IpRuleAction.Allow, Cidr = "127.0.0.0/8" }] };
        var denyLocal = new AccessList { Id = "deny", Name = "Deny local", Rules = [new IpRule { Action = IpRuleAction.Deny, Cidr = "127.0.0.0/8" }] };
        c.S.Store.Col<AccessList>().Insert(allowLocal);
        c.S.Store.Col<AccessList>().Insert(denyLocal);
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["zr.test"], Tls = TlsMode.Internal, ResponseStatus = 200, ResponseBody = "ok", AccessListId = "local", Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["zd.test"], Tls = TlsMode.Internal, ResponseStatus = 200, ResponseBody = "ok", AccessListId = "deny", Compression = false });
        await c.StartAsync();
        await c.ApplyAsync();
        Assert.Equal(200, (await c.HttpsUntilAsync("zr.test", r => r.Status == 200, TimeSpan.FromSeconds(20))).Status);
        Assert.Equal(403, (await c.HttpsUntilAsync("zd.test", r => r.Status == 403, TimeSpan.FromSeconds(20))).Status);

        var fixedRun = await RunProbe(c.HttpsPort, "zr.test");
        report["fixed"] = fixedRun;
        Assert.Equal(2, fixedRun.Count);                                                   // (4)
        Assert.All(fixedRun, a => Assert.Equal(200, a!["status"]!.GetValue<int>()));      // (1)
        Assert.True(fixedRun[1]!["resumed"]!.GetValue<bool>(), "session resumption did not happen"); // (2)
        Assert.False(fixedRun[1]!["earlyDataAccepted"]!.GetValue<bool>());
        var denied = await RunProbe(c.HttpsPort, "zd.test");
        report["denied"] = denied;
        Assert.All(denied, a => Assert.Equal(403, a!["status"]!.GetValue<int>()));        // (3)

        // (5) CONTROL: Caddy's default (0-RTT allowed). The QUIC listener is released first (HTTP/3 off), otherwise
        // the reload would keep the existing listener and its Allow0RTT=false.
        var control = await c.RunningConfigAsync();
        var srv0 = control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!.AsObject();
        srv0.Remove("allow_0rtt");
        var withoutH3 = control.DeepClone();
        withoutH3["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["protocols"] = new JsonArray("h1", "h2");
        await c.LoadRawAsync(withoutH3);
        await c.LoadRawAsync(control);
        var controlRun = await RunProbe(c.HttpsPort, "zr.test");
        report["control"] = controlRun;
        Assert.True(controlRun[1]!["earlyDataAccepted"]!.GetValue<bool>(), "control: the client never sent 0-RTT data");
        Assert.Equal(425, controlRun[1]!["status"]!.GetValue<int>());

        // (6) the manager applies its config to that Caddy (0-RTT listener running): no 425 afterwards.
        await c.ApplyAsync("upgrade from a 0-RTT listener");
        var upgraded = await RunProbe(c.HttpsPort, "zr.test");
        report["afterApplyOnZeroRttListener"] = upgraded;
        Assert.All(upgraded, a => Assert.Equal(200, a!["status"]!.GetValue<int>()));
        Assert.False(upgraded[1]!["earlyDataAccepted"]!.GetValue<bool>(), "the running QUIC listener still accepts 0-RTT");

        E2EArtifacts.Write("http3-0rtt-ip-access-list.json", report);
    }

    /// <summary>
    /// Releasing a 0-RTT QUIC listener when the same apply also moves the admin API (review round 2, research #72).
    /// The release loads an interim config (HTTP/3 off) before the real one. When that interim copy already carried
    /// the NEW admin address, Caddy moved its admin API there, the real load went to the old address and failed, the
    /// manager reported "Caddy is not running" and the interim config (no HTTP/3) stayed live.
    /// Runs without aioquic (the release is decided from the running config); with CPM_AIOQUIC_PYTHON it also proves
    /// that the new listener refuses 0-RTT.
    /// Ways it could fail: (1) the apply fails or ends "written only" (Caddy considered not running); (2) Caddy's admin
    /// API is not on the new address afterwards, or still on the old one; (3) the running config is the interim one
    /// (no h3) or lacks allow_0rtt=false; (4) HTTPS stops answering; (5) with aioquic: the listener still accepts 0-RTT;
    /// (6) the control (interim load with the new admin address, then the real load on the old address — the previous
    /// sequence) does not fail, i.e. the scenario would not catch the bug.
    /// </summary>
    [CaddyFact]
    public async Task Zero_rtt_listener_release_follows_an_admin_address_change_in_the_same_apply()
    {
        var report = E2EArtifacts.Report(nameof(Zero_rtt_listener_release_follows_an_admin_address_change_in_the_same_apply));
        using var c = new LiveCaddy(s => s.EnableHttp3 = true);
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["zr.test"], Tls = TlsMode.Internal, ResponseStatus = 200, ResponseBody = "ok", Compression = false });
        await c.StartAsync();
        await c.ApplyAsync();
        Assert.Equal(200, (await c.HttpsUntilAsync("zr.test", r => r.Status == 200, TimeSpan.FromSeconds(20))).Status);

        // A Caddy running an older config: HTTP/3 with 0-RTT allowed (listener created fresh, so it really allows it).
        async Task RunZeroRttConfig()
        {
            var old = await c.RunningConfigAsync();
            old["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!.AsObject().Remove("allow_0rtt");
            var noH3 = old.DeepClone();
            noH3["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["protocols"] = new JsonArray("h1", "h2");
            await c.LoadRawAsync(noH3);
            await c.LoadRawAsync(old);
        }
        await RunZeroRttConfig();
        var oldAdmin = c.Admin.BaseUrl;

        // The apply under test: new admin address + the 0-RTT release.
        var newAdminPort = Net.FreeTcpPort();
        c.UpdateSettings(s => s.AdminListen = $"127.0.0.1:{newAdminPort}");
        var result = await c.S.Config.ApplyAsync("admin move + 0-RTT release");
        report["apply"] = new JsonObject { ["success"] = result.Success, ["writtenOnly"] = result.WrittenOnly, ["error"] = result.Error, ["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode)w).ToArray()) };
        Assert.True(result.Success, result.Error);
        Assert.False(result.WrittenOnly, "apply ended written-only: " + string.Join(" | ", result.Warnings)); // (1)
        Assert.EndsWith(":" + newAdminPort, c.Admin.BaseUrl);
        Assert.True(await c.Admin.IsReachableAsync(), "admin API not on the new address");                 // (2)
        Assert.False(await c.Admin.ForAddress(oldAdmin.Replace("http://", "")).IsReachableAsync(), "admin API still on the old address");
        var running = await c.RunningConfigAsync();
        var srv0 = running["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!;
        report["runningSrv0"] = new JsonObject { ["protocols"] = srv0["protocols"]!.DeepClone(), ["allow_0rtt"] = srv0["allow_0rtt"]?.DeepClone() };
        Assert.Contains("h3", srv0["protocols"]!.AsArray().Select(x => x!.GetValue<string>()));            // (3)
        Assert.False(srv0["allow_0rtt"]!.GetValue<bool>());
        Assert.Equal(200, (await c.HttpsUntilAsync("zr.test", r => r.Status == 200, TimeSpan.FromSeconds(10))).Status); // (4)
        if (AioquicPython is not null)
        {
            var probe = await RunProbe(c.HttpsPort, "zr.test");
            report["aioquicProbe"] = probe;
            Assert.False(probe[1]!["earlyDataAccepted"]!.GetValue<bool>(), "the new QUIC listener accepts 0-RTT"); // (5)
        }
        else report["aioquicProbe"] = "skipped (CPM_AIOQUIC_PYTHON not set)";

        // (6) CONTROL: the previous sequence — interim copy with the NEW admin address, real load on the old one.
        await RunZeroRttConfig();
        var next = await c.RunningConfigAsync();
        next["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["allow_0rtt"] = false;
        var movedPort = Net.FreeTcpPort();
        next["admin"]!["listen"] = $"127.0.0.1:{movedPort}";
        var interim = next.DeepClone();
        interim["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["protocols"] = new JsonArray("h1", "h2");
        var before = c.Admin;
        await before.LoadAsync(interim.ToJsonString());
        var failed = await Record.ExceptionAsync(() => before.LoadAsync(next.ToJsonString()));
        report["controlSecondLoadError"] = failed?.GetType().Name + ": " + failed?.Message;
        Assert.IsType<HttpRequestException>(failed);
        // Leave Caddy reachable where LiveCaddy expects it for disposal (it is killed either way).
        await c.Admin.ForAddress($"127.0.0.1:{movedPort}").LoadAsync(next.ToJsonString().Replace($"127.0.0.1:{movedPort}", $"127.0.0.1:{newAdminPort}"));

        E2EArtifacts.Write("zero-rtt-release-admin-move.json", report);
    }

    private static async Task<JsonArray> RunProbe(int port, string sni)
    {
        var script = Path.Combine(Path.GetTempPath(), "cpm-zrtt-" + Guid.NewGuid().ToString("N")[..8] + ".py");
        await File.WriteAllTextAsync(script, ZeroRttProbe);
        try
        {
            var psi = new ProcessStartInfo(AioquicPython!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { script, "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture), sni, "/zrtt" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await p.WaitForExitAsync(cts.Token);
            var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(p.ExitCode == 0, $"aioquic probe failed ({p.ExitCode}): {await stderr}\n{await stdout}");
            return new JsonArray(lines.Select(l => JsonNode.Parse(l)).ToArray());
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>aioquic 1.x client: connection 1 obtains a session ticket, connection 2 resumes with 0-RTT when allowed.</summary>
    private const string ZeroRttProbe = """
        import asyncio, json, ssl, sys
        from aioquic.asyncio import connect
        from aioquic.asyncio.protocol import QuicConnectionProtocol
        from aioquic.h3.connection import H3_ALPN, H3Connection
        from aioquic.h3.events import HeadersReceived, DataReceived
        from aioquic.quic.configuration import QuicConfiguration

        class C(QuicConnectionProtocol):
            def __init__(self, *a, **k):
                super().__init__(*a, **k); self.h3 = H3Connection(self._quic); self.w = {}
            def quic_event_received(self, e):
                for ev in self.h3.handle_event(e):
                    s = self.w.get(getattr(ev, "stream_id", None))
                    if not s: continue
                    if isinstance(ev, HeadersReceived): s["h"] += ev.headers
                    if isinstance(ev, DataReceived): s["b"] += ev.data
                    if getattr(ev, "stream_ended", False) and not s["f"].done(): s["f"].set_result(True)
            async def get(self, authority, path):
                sid = self._quic.get_next_available_stream_id()
                st = {"h": [], "b": b"", "f": asyncio.get_event_loop().create_future()}; self.w[sid] = st
                self.h3.send_headers(sid, [(b":method", b"GET"), (b":scheme", b"https"), (b":authority", authority.encode()), (b":path", path.encode())], end_stream=True)
                self.transmit(); await asyncio.wait_for(st["f"], 15); return st

        async def main(ip, port, sni, path):
            tickets = []
            for attempt in (1, 2):
                cfg = QuicConfiguration(is_client=True, alpn_protocols=H3_ALPN, server_name=sni, verify_mode=ssl.CERT_NONE)
                ticket = tickets[-1] if tickets else None
                if ticket is not None: cfg.session_ticket = ticket
                offered = ticket is not None and ticket.max_early_data_size is not None
                async with connect(ip, port, configuration=cfg, create_protocol=C, session_ticket_handler=tickets.append, wait_connected=not offered) as c:
                    r = await c.get(sni, path)
                    await asyncio.sleep(0.3)
                    h = dict(r["h"])
                    print(json.dumps({"attempt": attempt, "status": int(h.get(b":status", b"0")), "earlyDataOffered": offered,
                        "earlyDataAccepted": bool(c._quic.tls.early_data_accepted), "resumed": bool(c._quic.tls.session_resumed),
                        "ticketAllowsEarlyData": bool(tickets and tickets[-1].max_early_data_size is not None)}), flush=True)

        asyncio.run(asyncio.wait_for(main(sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]), 60))
        """;
}
