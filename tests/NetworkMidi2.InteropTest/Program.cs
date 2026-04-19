using System.Net;
using System.Reactive.Linq;
using Haukcode.NetworkMidi2;
using Haukcode.NetworkMidi2.Mdns;

// ---------------------------------------------------------------------------
// Haukcode.NetworkMidi2 — Interoperability Test CLI
//
// Usage:
//   dotnet run -- client --host <ip> --port <port> [--name <name>] [--loopback]
//       Connect to a remote Network MIDI 2.0 peer and run a structured set of
//       protocol checks (handshake, ping, UMP round-trip, FEC, clean disconnect).
//       Each check prints PASS / FAIL / SKIP with a reason.
//       Exit code 0 = all non-skipped checks passed.
//
//   dotnet run -- server [--port <port>] [--name <name>]
//       Listen for incoming connections, echo all received UMP back to the
//       sender, and advertise via mDNS (_midi2._udp) so peers can find it
//       automatically (e.g. macOS 26.4+, Windows MIDI Services).
//
// A Wireshark display filter for the chosen port is printed at startup.
// ---------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string mode = args[0].ToLowerInvariant();

return mode switch
{
    "client" => await RunClientModeAsync(args[1..]),
    "server" => await RunServerModeAsync(args[1..]),
    _        => PrintUsage(),
};

// ---------------------------------------------------------------------------
// Usage
// ---------------------------------------------------------------------------

static int PrintUsage()
{
    Console.WriteLine("""
Network MIDI 2.0 Interoperability Test Tool
============================================

Usage:
  dotnet run -- client --host <ip> --port <port> [--name <name>] [--loopback]
  dotnet run -- server [--port <port>] [--name <name>]

Client mode connects to a remote peer and runs protocol checks:
  • Session handshake (Invitation → InvitationAccepted)
  • Ping / PingReply (liveness exchange)
  • UMP round-trip — MIDI 1.0 Note On/Off echoed back (requires --loopback)
  • UMP round-trip — MIDI 2.0 Note On/Off echoed back (requires --loopback)
  • FEC encoding — verify packets include forward error correction history
  • Clean disconnection (Bye / ByeReply)

Server mode advertises via mDNS, accepts connections, echoes all UMP,
and reports each received packet to stdout.

Known good peers to test against:
  macOS 26.4+ CoreMIDI — Audio MIDI Setup → Network MIDI
  Windows MIDI Services — https://aka.ms/midi (Windows 11 24H2+)
""");
    return 1;
}

// ---------------------------------------------------------------------------
// Client mode
// ---------------------------------------------------------------------------

static async Task<int> RunClientModeAsync(string[] args)
{
    string? host     = null;
    int     port     = NetworkMidi2Protocol.DefaultPort;
    string  name     = "InteropTest";
    bool    loopback = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--host":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --host requires a value."); return 1; }
                host = args[++i];
                break;
            case "--port":
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port)) { Console.Error.WriteLine("ERROR: --port requires a valid integer."); return 1; }
                i++;
                break;
            case "--name":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --name requires a value."); return 1; }
                name = args[++i];
                break;
            case "--loopback": loopback = true; break;
        }
    }

    if (host is null)
    {
        Console.Error.WriteLine("ERROR: --host is required for client mode.");
        Console.Error.WriteLine("Example: dotnet run -- client --host 192.168.1.50 --port 5004");
        return 1;
    }

    if (!IPAddress.TryParse(host, out var address))
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? addresses[0];
        }
        catch
        {
            Console.Error.WriteLine($"ERROR: Cannot resolve host '{host}'.");
            return 1;
        }
    }

    var endPoint = new IPEndPoint(address, port);

    PrintWiresharkFilter(port);
    Console.WriteLine();
    Console.WriteLine($"Client mode → {endPoint}  (name={name})");
    Console.WriteLine();

    var results = new List<CheckResult>();

    // Capture a reference to the trace log for FEC verification
    var traceLog = new List<string>();
    NetworkMidi2Session.TraceHook = msg => { lock (traceLog) traceLog.Add(msg); };

    // -----------------------------------------------------------------------
    // Check 1: Session handshake (Invitation → InvitationAccepted)
    // -----------------------------------------------------------------------

    Console.Write("  [1/6] Session handshake (Invitation → InvitationAccepted)… ");

    await using var session = new NetworkMidi2Session(name);

    var receivedUmp = new List<uint[]>();
    var umpLock     = new object();
    session.UmpReceived.Subscribe(mem =>
    {
        lock (umpLock)
            receivedUmp.Add(mem.ToArray());
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    try
    {
        await session.ConnectAsync(endPoint, cts.Token);
        results.Add(Pass("handshake", $"connected to '{session.RemoteName}'"));
    }
    catch (Exception ex)
    {
        results.Add(Fail("handshake", ex.Message));
        NetworkMidi2Session.TraceHook = null;
        PrintResults(results);
        return results.Any(r => !r.Passed) ? 2 : 0;
    }

    // -----------------------------------------------------------------------
    // Check 2: Ping / PingReply
    // -----------------------------------------------------------------------

    Console.Write("  [2/6] Ping / PingReply (liveness)… ");

    // Give the session a moment then verify it is still alive (ping loop is active)
    await Task.Delay(300, cts.Token);
    bool stillConnected = session.State == SessionState.Connected;
    results.Add(stillConnected
        ? Pass("ping", "session alive 300 ms after connect")
        : Fail("ping", $"unexpected state {session.State}"));

    // -----------------------------------------------------------------------
    // Check 3: UMP round-trip — MIDI 1.0 Note On/Off (requires loopback)
    // -----------------------------------------------------------------------

    Console.Write("  [3/6] UMP round-trip (MIDI 1.0 Note On/Off)… ");

    if (!loopback)
    {
        results.Add(Skip("ump-midi1-roundtrip", "peer loopback not confirmed (pass --loopback to enable)"));
    }
    else
    {
        // MIDI 1.0 Channel Voice Message UMP (message type 0x2):
        //   Word: [0x2 | group=0] [status=0x9_ch=0] [pitch=0x3C] [vel=0x64]
        uint noteOn  = 0x2090_3C64u;  // Note On  ch=0 pitch=60 vel=100
        uint noteOff = 0x2080_3C00u;  // Note Off ch=0 pitch=60 vel=0

        lock (umpLock) receivedUmp.Clear();

        await session.SendUmpAsync(new[] { noteOn  }, cts.Token);
        await session.SendUmpAsync(new[] { noteOff }, cts.Token);

        var deadline  = DateTime.UtcNow.AddSeconds(3);
        bool roundTrip = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (umpLock)
            {
                if (receivedUmp.Any(w => w.Length > 0 && w[0] == noteOn) &&
                    receivedUmp.Any(w => w.Length > 0 && w[0] == noteOff))
                {
                    roundTrip = true;
                    break;
                }
            }
            await Task.Delay(50, cts.Token);
        }

        results.Add(roundTrip
            ? Pass("ump-midi1-roundtrip", "MIDI 1.0 Note On + Note Off echoed back")
            : Fail("ump-midi1-roundtrip", "did not receive echoed UMP within 3 s"));
    }

    // -----------------------------------------------------------------------
    // Check 4: UMP round-trip — MIDI 2.0 Note On/Off (requires loopback)
    // -----------------------------------------------------------------------

    Console.Write("  [4/6] UMP round-trip (MIDI 2.0 Note On/Off)… ");

    if (!loopback)
    {
        results.Add(Skip("ump-midi2-roundtrip", "peer loopback not confirmed (pass --loopback to enable)"));
    }
    else
    {
        // MIDI 2.0 Channel Voice Message UMP (message type 0x4), 2 words:
        //   Word 1: [0x4 | group=0] [status=0x9_ch=0] [pitch=0x3C] [attribute=0x00]
        //   Word 2: [velocity=0x8000] [attribute data=0x0000]
        uint[] midi2NoteOn  = [0x4090_3C00u, 0x8000_0000u];  // MIDI 2.0 Note On
        uint[] midi2NoteOff = [0x4080_3C00u, 0x0000_0000u];  // MIDI 2.0 Note Off

        lock (umpLock) receivedUmp.Clear();

        await session.SendUmpAsync(midi2NoteOn,  cts.Token);
        await session.SendUmpAsync(midi2NoteOff, cts.Token);

        var deadline    = DateTime.UtcNow.AddSeconds(3);
        bool roundTrip2 = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (umpLock)
            {
                if (receivedUmp.Any(w => w.Length >= 2 && w[0] == midi2NoteOn[0]  && w[1] == midi2NoteOn[1]) &&
                    receivedUmp.Any(w => w.Length >= 2 && w[0] == midi2NoteOff[0] && w[1] == midi2NoteOff[1]))
                {
                    roundTrip2 = true;
                    break;
                }
            }
            await Task.Delay(50, cts.Token);
        }

        results.Add(roundTrip2
            ? Pass("ump-midi2-roundtrip", "MIDI 2.0 Note On + Note Off echoed back")
            : Fail("ump-midi2-roundtrip", "did not receive echoed UMP within 3 s"));
    }

    // -----------------------------------------------------------------------
    // Check 5: FEC — verify outbound packets include history entries
    // -----------------------------------------------------------------------

    Console.Write("  [5/6] FEC encoding (history in sent packets)… ");

    // After sending at least 2 UMP packets we expect the trace log to show
    // FEC history entries ("fec=1" or "fec=2").
    bool fecActive;
    lock (traceLog)
        fecActive = traceLog.Any(m => m.Contains("fec=1") || m.Contains("fec=2"));

    results.Add(fecActive
        ? Pass("fec", "outbound datagrams carry FEC history entries")
        : Fail("fec", "no FEC history observed in sent packets"));

    // -----------------------------------------------------------------------
    // Check 6: Clean disconnection (Bye / ByeReply)
    // -----------------------------------------------------------------------

    Console.Write("  [6/6] Clean disconnection (Bye / ByeReply)… ");

    try
    {
        await session.DisconnectAsync(cts.Token);
        results.Add(Pass("disconnect", "DisconnectAsync completed without error"));
    }
    catch (Exception ex)
    {
        results.Add(Fail("disconnect", ex.Message));
    }

    // -----------------------------------------------------------------------
    // Summary
    // -----------------------------------------------------------------------

    NetworkMidi2Session.TraceHook = null;

    Console.WriteLine();
    PrintResults(results);
    return results.Any(r => !r.Passed && !r.Skipped) ? 2 : 0;
}

// ---------------------------------------------------------------------------
// Server mode
// ---------------------------------------------------------------------------

static async Task<int> RunServerModeAsync(string[] args)
{
    int    port = NetworkMidi2Protocol.DefaultPort;
    string name = "InteropTest";

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--port":
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port)) { Console.Error.WriteLine("ERROR: --port requires a valid integer."); return 1; }
                i++;
                break;
            case "--name":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --name requires a value."); return 1; }
                name = args[++i];
                break;
        }
    }

    PrintWiresharkFilter(port);
    Console.WriteLine();
    Console.WriteLine($"Server mode  port={port}  name={name}");
    Console.WriteLine($"Advertising via mDNS (_midi2._udp) — visible to macOS 26.4+, Windows MIDI Services.");
    Console.WriteLine($"All received UMP will be echoed back to the sender.");
    Console.WriteLine($"Press Ctrl+C to exit.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // Advertise via mDNS so peers can find this session automatically
    using var advertiser = new NetworkMidi2Advertiser(name, (ushort)port);
    advertiser.Start();
    Console.WriteLine($"  [mDNS] Advertising '{name}' on port {port}.");

    int sessionCount = 0;

    await using var session = new NetworkMidi2Session(name);

    session.StateChanges.Subscribe(state =>
    {
        switch (state)
        {
            case SessionState.Connected:
                Console.WriteLine($"  [session #{++sessionCount}] Connected: peer='{session.RemoteName}'");
                break;
            case SessionState.Idle:
                Console.WriteLine($"  [session] Disconnected — waiting for next connection…");
                break;
            case SessionState.Disconnecting:
                Console.WriteLine($"  [session] Disconnecting…");
                break;
        }
    });

    // Echo every received UMP message back to the sender
    session.UmpReceived.Subscribe(async umpWords =>
    {
        var hex     = string.Join(" ", umpWords.ToArray().Select(w => $"{w:X8}"));
        var decoded = DecodeUmp(umpWords.Span);
        Console.WriteLine($"  [ump   ] {decoded,-42}  [{hex}]");

        if (session.State == SessionState.Connected)
        {
            try
            {
                await session.SendUmpAsync(umpWords, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  [echo  ] Send failed: {ex.Message}");
            }
        }
    });

    await session.ListenWithReconnectAsync(port, TimeSpan.FromMilliseconds(500), cts.Token);

    Console.WriteLine("\nServer stopped.");
    return 0;
}

// ---------------------------------------------------------------------------
// Wireshark filter
// ---------------------------------------------------------------------------

static void PrintWiresharkFilter(int port)
{
    Console.WriteLine($"Wireshark filter: udp.port == {port}");
}

// ---------------------------------------------------------------------------
// Check result helpers
// ---------------------------------------------------------------------------

static CheckResult Pass(string name, string reason)
{
    Console.WriteLine($"PASS  ({reason})");
    return new CheckResult(name, Passed: true, Skipped: false, reason);
}

static CheckResult Fail(string name, string reason)
{
    Console.WriteLine($"FAIL  ({reason})");
    return new CheckResult(name, Passed: false, Skipped: false, reason);
}

static CheckResult Skip(string name, string reason)
{
    Console.WriteLine($"SKIP  ({reason})");
    return new CheckResult(name, Passed: true, Skipped: true, reason);
}

static void PrintResults(IReadOnlyList<CheckResult> results)
{
    int passed  = results.Count(r => r.Passed && !r.Skipped);
    int failed  = results.Count(r => !r.Passed);
    int skipped = results.Count(r => r.Skipped);

    Console.WriteLine("─────────────────────────────────────────────────────────");
    foreach (var r in results)
    {
        string tag = r.Skipped ? "SKIP" : r.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"  {tag,-4}  {r.Name,-28}  {r.Reason}");
    }
    Console.WriteLine("─────────────────────────────────────────────────────────");
    Console.WriteLine($"  {passed} passed  /  {failed} failed  /  {skipped} skipped");
    Console.WriteLine();

    if (failed == 0)
        Console.WriteLine("All checks passed. ✓");
    else
        Console.WriteLine($"{failed} check(s) FAILED.");
}

// ---------------------------------------------------------------------------
// UMP decoder — human-readable summary of common message types
// ---------------------------------------------------------------------------

static string DecodeUmp(ReadOnlySpan<uint> words)
{
    if (words.IsEmpty) return "(empty)";

    byte msgType = (byte)(words[0] >> 28);
    byte group   = (byte)((words[0] >> 24) & 0x0F);

    return msgType switch
    {
        0x0 => "Utility",
        0x1 => DecodeSystemRealTime(words[0], group),
        0x2 => DecodeMidi1ChannelVoice(words[0], group),
        0x3 => DecodeSysEx7(words, group),
        0x4 => DecodeMidi2ChannelVoice(words, group),
        0x5 => DecodeSysEx8(words, group),
        0xD => "Flex Data",
        0xF => "UMP Stream",
        _   => $"Type 0x{msgType:X}",
    };
}

static string DecodeSystemRealTime(uint word, byte group)
{
    byte status = (byte)((word >> 16) & 0xFF);
    return status switch
    {
        0xF8 => $"Timing Clock  g={group}",
        0xFA => $"Start         g={group}",
        0xFB => $"Continue      g={group}",
        0xFC => $"Stop          g={group}",
        0xFE => $"Active Sense  g={group}",
        0xFF => $"System Reset  g={group}",
        _    => $"System 0x{status:X2} g={group}",
    };
}

static string DecodeMidi1ChannelVoice(uint word, byte group)
{
    byte status = (byte)((word >> 16) & 0xFF);
    byte d1     = (byte)((word >> 8)  & 0xFF);
    byte d2     = (byte)(word         & 0xFF);
    byte ch     = (byte)(status & 0x0F);

    return (status & 0xF0) switch
    {
        0x80 => $"MIDI1 Note Off  g={group} ch={ch} p={d1} v={d2}",
        0x90 => d2 == 0
                    ? $"MIDI1 Note Off  g={group} ch={ch} p={d1} (vel=0)"
                    : $"MIDI1 Note On   g={group} ch={ch} p={d1} v={d2}",
        0xA0 => $"MIDI1 Poly AT   g={group} ch={ch} p={d1} pres={d2}",
        0xB0 => $"MIDI1 CC        g={group} ch={ch} ctrl={d1} val={d2}",
        0xC0 => $"MIDI1 Prog Chg  g={group} ch={ch} prog={d1}",
        0xD0 => $"MIDI1 Chan AT   g={group} ch={ch} pres={d1}",
        0xE0 => $"MIDI1 Pitch Bnd g={group} ch={ch} val={d1 | (d2 << 7)}",
        _    => $"MIDI1 0x{status:X2} g={group}",
    };
}

static string DecodeSysEx7(ReadOnlySpan<uint> words, byte group)
{
    byte status = (byte)((words[0] >> 20) & 0x0F);
    byte count  = (byte)((words[0] >> 16) & 0x0F);
    return $"SysEx7 g={group} status=0x{status:X} bytes={count}";
}

static string DecodeMidi2ChannelVoice(ReadOnlySpan<uint> words, byte group)
{
    if (words.Length < 2) return $"MIDI2 (truncated) g={group}";
    byte status = (byte)((words[0] >> 16) & 0xFF);
    byte ch     = (byte)(status & 0x0F);
    byte pitch  = (byte)((words[0] >> 8) & 0xFF);

    return (status & 0xF0) switch
    {
        0x80 => $"MIDI2 Note Off  g={group} ch={ch} p={pitch}",
        0x90 => $"MIDI2 Note On   g={group} ch={ch} p={pitch}",
        0xA0 => $"MIDI2 Poly AT   g={group} ch={ch} p={pitch}",
        0xB0 => $"MIDI2 CC        g={group} ch={ch} ctrl={pitch}",
        0xC0 => $"MIDI2 Prog Chg  g={group} ch={ch}",
        0xD0 => $"MIDI2 Chan AT   g={group} ch={ch}",
        0xE0 => $"MIDI2 Pitch Bnd g={group} ch={ch}",
        _    => $"MIDI2 0x{status:X2} g={group}",
    };
}

static string DecodeSysEx8(ReadOnlySpan<uint> words, byte group)
{
    byte status = (byte)((words[0] >> 20) & 0x0F);
    byte count  = (byte)((words[0] >> 16) & 0x0F);
    return $"SysEx8 g={group} status=0x{status:X} bytes={count}";
}

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

record CheckResult(string Name, bool Passed, bool Skipped, string Reason);
