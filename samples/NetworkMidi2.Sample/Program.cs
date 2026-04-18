using System.Net;
using System.Reactive.Linq;
using Haukcode.NetworkMidi2;
using Haukcode.NetworkMidi2.Mdns;

// ---------------------------------------------------------------------------
// Haukcode.NetworkMidi2 — sample console client
//
// Usage:
//   dotnet run                         Discover peers via mDNS, pick one interactively
//   dotnet run -- <host> <port>        Connect directly (e.g.  192.168.1.50 5004)
//   dotnet run -- --listen [port]      Listen for an incoming connection
//
// Once connected the sample:
//   • Prints every incoming UMP packet decoded to hex words
//   • Lets you type UMP words to send back
//   • Automatically reconnects if the session drops (Ctrl+C to quit)
// ---------------------------------------------------------------------------

const string LocalName = "NetworkMidi2-Sample";

if (args.Length >= 1 && args[0] == "--listen")
{
    int port = args.Length >= 2 ? int.Parse(args[1]) : 5004;
    await RunListenerAsync(port);
}
else if (args.Length >= 2)
{
    var host = args[0];
    int port = int.Parse(args[1]);
    await RunClientAsync(new IPEndPoint(IPAddress.Parse(host), port));
}
else
{
    await RunDiscoveryAndConnectAsync();
}

// ---------------------------------------------------------------------------
// Discovery → interactive peer selection → connect
// ---------------------------------------------------------------------------

static async Task RunDiscoveryAndConnectAsync()
{
    Console.WriteLine("Scanning for Network MIDI 2.0 peers (2 s)…");

    var peers = await NetworkMidi2Discovery.ResolveAsync();

    if (peers.Count == 0)
    {
        Console.WriteLine("No peers found. Try: dotnet run -- <host> <port>");
        return;
    }

    Console.WriteLine($"\nFound {peers.Count} peer(s):\n");
    for (int i = 0; i < peers.Count; i++)
        Console.WriteLine($"  [{i + 1}] {peers[i].Name}  ({peers[i].EndPoint})");

    Console.Write("\nSelect peer number (or 0 to exit): ");
    if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > peers.Count)
        return;

    await RunClientAsync(peers[choice - 1].EndPoint);
}

// ---------------------------------------------------------------------------
// Connect as client, with auto-reconnect
// ---------------------------------------------------------------------------

static async Task RunClientAsync(IPEndPoint ep)
{
    Console.WriteLine($"\nConnecting to {ep} (reconnects every 5 s on drop)…");

    await using var session = new NetworkMidi2Session(LocalName);
    SubscribeToSession(session);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var connectTask = session.ConnectWithReconnectAsync(ep, TimeSpan.FromSeconds(5), cts.Token);

    await RunCommandLoopAsync(session, cts.Token);
    await connectTask;
}

// ---------------------------------------------------------------------------
// Listen for incoming connection, with auto-reconnect
// ---------------------------------------------------------------------------

static async Task RunListenerAsync(int port)
{
    Console.WriteLine($"\nListening on port {port}…");
    Console.WriteLine("Will re-listen after each session ends. Press Ctrl+C to quit.\n");

    await using var session = new NetworkMidi2Session(LocalName);
    SubscribeToSession(session);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var listenTask = session.ListenWithReconnectAsync(port, TimeSpan.FromMilliseconds(500), cts.Token);

    await RunCommandLoopAsync(session, cts.Token);
    await listenTask;
}

// ---------------------------------------------------------------------------
// Shared: subscribe to observables
// ---------------------------------------------------------------------------

static void SubscribeToSession(INetworkMidi2Session session)
{
    session.StateChanges.Subscribe(state =>
    {
        var label = state switch
        {
            SessionState.Connected     => $"Connected to '{session.RemoteName}'",
            SessionState.Disconnecting => "Disconnecting…",
            SessionState.Idle          => "Disconnected — waiting to reconnect…",
            _                          => state.ToString(),
        };
        Console.WriteLine($"[state] {label}");
    });

    session.UmpReceived.Subscribe(words =>
    {
        var hex = string.Join(" ", words.ToArray().Select(w => $"{w:X8}"));
        Console.WriteLine($"[ump  ] {DecodeUmpSummary(words.Span)}  [{hex}]");
    });
}

// ---------------------------------------------------------------------------
// Interactive command loop
// ---------------------------------------------------------------------------

static async Task RunCommandLoopAsync(INetworkMidi2Session session, CancellationToken ct)
{
    PrintHelp();

    while (!ct.IsCancellationRequested)
    {
        Console.Write("> ");

        string? line;
        try { line = await Task.Run(() => Console.ReadLine(), ct); }
        catch (OperationCanceledException) { break; }

        if (string.IsNullOrWhiteSpace(line)) continue;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "ump":
                case "raw":
                    // ump <hex uint32 words, space-separated>
                    if (parts.Length < 2) { Console.WriteLine("Usage: ump <uint32 hex words...>"); break; }
                    var words = parts[1..].Select(h => Convert.ToUInt32(h, 16)).ToArray();
                    await session.SendUmpAsync(words, ct);
                    Console.WriteLine($"  → UMP [{string.Join(" ", words.Select(w => $"{w:X8}"))}]");
                    break;

                case "help":
                    PrintHelp();
                    break;

                case "quit":
                case "exit":
                    return;

                default:
                    Console.WriteLine($"Unknown command '{cmd}'. Type 'help' for list.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"  (not connected — {ex.Message})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    Console.WriteLine("\nDisconnecting…");
}

// ---------------------------------------------------------------------------
// UMP decoder — brief summary of common message types
// ---------------------------------------------------------------------------

static string DecodeUmpSummary(ReadOnlySpan<uint> words)
{
    if (words.IsEmpty) return "(empty)";

    byte msgType = (byte)(words[0] >> 28);
    byte group   = (byte)((words[0] >> 24) & 0x0F);

    return msgType switch
    {
        0x0 => "Utility",
        0x1 => "System",
        0x2 => DecodeMidi1(words[0], group),
        0x4 => DecodeMidi2(words, group),
        0x5 => "SysEx 8",
        0xD => "Flex Data",
        0xF => "UMP Stream",
        _   => $"Type 0x{msgType:X}",
    };
}

static string DecodeMidi1(uint word, byte group)
{
    byte status = (byte)((word >> 16) & 0xFF);
    byte d1     = (byte)((word >> 8)  & 0xFF);
    byte d2     = (byte)(word         & 0xFF);
    byte ch     = (byte)(status & 0x0F);

    return (status & 0xF0) switch
    {
        0x80 => $"Note Off  g={group} ch={ch} p={d1} v={d2}",
        0x90 => $"Note On   g={group} ch={ch} p={d1} v={d2}",
        0xB0 => $"CC        g={group} ch={ch} c={d1} v={d2}",
        0xC0 => $"Prog Chg  g={group} ch={ch} p={d1}",
        _    => $"MIDI1 0x{status:X2}",
    };
}

static string DecodeMidi2(ReadOnlySpan<uint> words, byte group)
{
    if (words.Length < 2) return "MIDI2 (truncated)";
    byte status = (byte)((words[0] >> 16) & 0xFF);
    byte ch     = (byte)(status & 0x0F);

    return (status & 0xF0) switch
    {
        0x80 => $"Note Off  g={group} ch={ch}",
        0x90 => $"Note On   g={group} ch={ch}",
        0xB0 => $"CC        g={group} ch={ch}",
        _    => $"MIDI2 0x{status:X2}",
    };
}

static void PrintHelp()
{
    Console.WriteLine("""

  Commands:
    ump <hex words...>    Send UMP packet (space-separated uint32 hex words)
    raw <hex words...>    Alias for ump
    help                  Show this list
    quit / exit           Disconnect and exit

  Incoming UMP packets are printed automatically as they arrive.
  Commands while disconnected are silently discarded (not connected exception).
""");
}
