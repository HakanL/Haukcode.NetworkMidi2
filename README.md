# Haukcode.NetworkMidi2

Network MIDI 2.0 (M2-124-UM v1.0) implementation in modern C# with full session protocol support.

Enables bidirectional MIDI 2.0 (UMP) over IP — send and receive Universal MIDI Packets over any standard UDP network, compatible with macOS 26.4+ CoreMIDI, Windows MIDI Services, and any M2-124-UM conformant device or software.

[![NuGet](https://img.shields.io/nuget/v/Haukcode.NetworkMidi2.svg)](https://www.nuget.org/packages/Haukcode.NetworkMidi2)
[![Build](https://github.com/HakanL/Haukcode.NetworkMidi2/actions/workflows/main.yml/badge.svg)](https://github.com/HakanL/Haukcode.NetworkMidi2/actions)

---

## Features

- Full M2-124-UM v1.0 session protocol (Invitation / InvitationAccepted / Ping / Bye)
- Both **client** and **host** roles, with optional auto-reconnect
- **PIN authentication** — full challenge-response handshake (HMAC-SHA-256, `InvitationAuthenticate` / `InvitationAuthenticationRequired`)
- **User authentication** — username/password challenge-response handshake (HMAC-SHA-256, `InvitationUserAuthenticate` / `InvitationUserAuthenticationRequired`)
- **Invitation pending** — host can signal "try again later" (`InvitationPending`)
- **NAK** — generic negative acknowledgment with reason code
- **Retransmit / RetransmitError** — explicit missing-packet recovery request
- **Session reset** — mid-session sequence counter reset without disconnect (`SessionReset` / `SessionResetReply`)
- Universal MIDI Packet (UMP) encoding/decoding — all message types
- Forward Error Correction (FEC) — up to 2 historical UMP payloads piggy-backed on every outbound datagram
- `IObservable<T>` streams via **System.Reactive** for received UMP and state changes
- Cross-platform: Windows, Linux (including ARM64), macOS
- Zero platform-specific code — pure managed C#
- Optional mDNS discovery and advertising via the companion **Haukcode.NetworkMidi2.Mdns** package

## Compatible implementations

| Implementation | Platform | Notes |
|----------------|----------|-------|
| macOS 26.4+ CoreMIDI | macOS | Built-in, Audio MIDI Setup |
| Windows MIDI Services | Windows 11 24H2+ | [aka.ms/midi](https://aka.ms/midi) |
| Any M2-124-UM conformant device | Hardware/Software | Single UDP port |

---

## Installation

```
dotnet add package Haukcode.NetworkMidi2
```

For mDNS peer discovery and advertising:

```
dotnet add package Haukcode.NetworkMidi2.Mdns
```

---

## Quick Start

### Connect to a known peer (static IP)

```csharp
await using var session = new NetworkMidi2Session("My App");

// Subscribe before connecting
session.UmpReceived.Subscribe(umpWords =>
{
    // umpWords is ReadOnlyMemory<uint> in host byte order
    var hex = string.Join(" ", umpWords.ToArray().Select(w => $"{w:X8}"));
    Console.WriteLine($"UMP: {hex}");
});

session.StateChanges.Subscribe(state =>
    Console.WriteLine($"State: {state}"));

// Single UDP port (no separate control/data ports like RTP-MIDI)
await session.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 5004));

// Send a MIDI 2.0 Note On (message type 0x4, group 0, channel 0, pitch 60)
uint[] midi2NoteOn = [0x4090_3C00u, 0x8000_0000u];
await session.SendUmpAsync(midi2NoteOn);
```

### Listen for incoming connections (host role)

```csharp
await using var session = new NetworkMidi2Session("My App");

session.UmpReceived.Subscribe(umpWords => HandleUmp(umpWords));

await session.ListenAsync(port: 5004);
```

### Auto-reconnect

Both roles have reconnecting variants that loop until cancellation:

```csharp
using var cts = new CancellationTokenSource();

// Reconnects every 5 s if the session drops
await session.ConnectWithReconnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);

// Re-listens after each session ends
await session.ListenWithReconnectAsync(port: 5004, TimeSpan.FromMilliseconds(500), cts.Token);
```

### Discover peers via mDNS (requires Haukcode.NetworkMidi2.Mdns)

```csharp
// One-shot scan
var peers = await NetworkMidi2Discovery.ResolveAsync();
foreach (var peer in peers)
    Console.WriteLine($"{peer.Name} @ {peer.EndPoint}");

// Continuous monitoring
using var discovery = new NetworkMidi2Discovery();
discovery.PeersFound.Subscribe(peer => Console.WriteLine($"Found: {peer.Name}"));
discovery.PeersLost.Subscribe(peer  => Console.WriteLine($"Lost:  {peer.Name}"));
discovery.StartMonitoring();

// Advertise your own session
using var advertiser = new NetworkMidi2Advertiser("My App", port: 5004);
advertiser.Start();
```

---

## Architecture

```
Haukcode.NetworkMidi2          (core — System.Reactive only)
├── NetworkMidi2Session        Main session class (client + host roles)
├── INetworkMidi2Session       Public interface
├── NetworkMidi2Protocol       M2-124-UM packet codec (Invitation/Ping/Bye/UMP Data)
├── UmpFec                     Forward Error Correction — history packing/unpacking
├── UmpDataCommand             Low-level UMP Data command (0xFF) codec
├── UmpHelpers                 UMP word utilities (size, big-endian I/O, validation)
└── SessionState               Idle / Connecting / Connected / Disconnecting

Haukcode.NetworkMidi2.Mdns     (optional — adds Zeroconf)
├── NetworkMidi2Discovery      _midi2._udp mDNS browse (one-shot + continuous)
└── NetworkMidi2Advertiser     _midi2._udp mDNS advertisement
```

---

## Protocol notes

Network MIDI 2.0 uses a **single UDP port** per session (unlike RTP-MIDI which uses two).
The default port is **5004**.

### Session lifecycle

| Step | Initiator (client) | Responder (host) |
|------|--------------------|-----------------|
| 1 | Sends `Invitation` | — |
| 2a | — | Replies `InvitationAccepted` (open session, no PIN) |
| 2b | — | Replies `InvitationPending` (busy; client retries) |
| 2c | — | Replies `InvitationAuthenticationRequired` (PIN required, with nonce) |
| 2d | — | Replies `InvitationUserAuthenticationRequired` (username/password required, with nonce) |
| 3 | Sends `InvitationAuthenticate` (HMAC-SHA-256 digest) | — |
| 3u | Sends `InvitationUserAuthenticate` (username + HMAC-SHA-256 digest) | — |
| 4 | — | Replies `InvitationAccepted` (PIN/user verified) or `Bye` (`AuthFailed` / `UserNameNotFound`) |
| 5 | Session established — both sides exchange `UmpData` (0xFF) | ← same |
| 6 | Periodic `Ping` / `PingReply` to detect liveness | ← same |
| 7 | Either side sends `Bye` to end the session | — |
| 8 | — | Replies `ByeReply` |

### UMP Data and Forward Error Correction (FEC)

Every outbound UDP datagram carries the current UMP Data command **plus up to two historical UMP payloads** prepended in the same datagram (oldest first). A receiver that missed a packet can recover it from the FEC data in the next datagram without requiring retransmission.

Wire layout (single datagram):

```
[magic: "MIDI" 4 bytes]
[UmpData cmd N-2]  ← oldest FEC history
[UmpData cmd N-1]  ← newer FEC history
[UmpData cmd N  ]  ← current payload
```

### Known gaps vs. the M2-124-UM v1.0 specification

All session commands defined in the specification are now implemented.

---

## Platform support

| Platform | Tested |
|----------|--------|
| Windows 10/11 | Yes |
| Linux x64 | Yes |
| Linux ARM64 (Raspberry Pi) | Yes |
| macOS | Yes |

---

## Contributing

Bug reports and pull requests welcome at https://github.com/HakanL/Haukcode.NetworkMidi2.

When contributing protocol-level changes, please reference the relevant section of the
[M2-124-UM Network MIDI 2.0 specification](https://midi.org/specifications).

---

## Interoperability testing

A dedicated CLI tool lives in `tests/NetworkMidi2.InteropTest`.
It runs in two modes and is useful for validating changes against real implementations.

### Mode 1 — Client (connect to a known peer and run checks)

```
dotnet run --project tests/NetworkMidi2.InteropTest -- client --host <ip> --port 5004 [--name InteropTest] [--loopback]
```

Runs the following checks in order and prints PASS / FAIL / SKIP for each.
Exit code 0 means all non-skipped checks passed.

| # | Check | Notes |
|---|-------|-------|
| 1 | Session handshake (Invitation → InvitationAccepted) | Always runs |
| 2 | Ping / PingReply (liveness) | Always runs |
| 3 | UMP round-trip — MIDI 1.0 Note On/Off | Requires `--loopback` |
| 4 | UMP round-trip — MIDI 2.0 Note On/Off | Requires `--loopback` |
| 5 | FEC encoding (history in sent packets) | Always runs |
| 6 | Clean disconnection (Bye / ByeReply) | Always runs |

Pass `--loopback` when the peer is configured to echo all received UMP back
(e.g. server mode below, or a DAW in MIDI-thru mode).

A Wireshark display filter for the chosen port is printed at startup:

```
Wireshark filter: udp.port == 5004
```

### Mode 2 — Server (act as a reference peer for other implementations)

```
dotnet run --project tests/NetworkMidi2.InteropTest -- server [--port 5004] [--name InteropTest]
```

- Accepts incoming connections (Invitation → InvitationAccepted)
- **Echoes all received UMP back** to the sender (loopback mode)
- Reports each received packet to stdout with human-readable UMP decoding
- Advertises via mDNS (`_midi2._udp`) so devices find it automatically

### Setting up known-good peers

| Peer | Platform | Setup |
|------|----------|-------|
| macOS 26.4+ CoreMIDI | macOS | Open **Audio MIDI Setup** → **Network** → create a session on port 5004 |
| Windows MIDI Services | Windows 11 24H2+ | Install from [aka.ms/midi](https://aka.ms/midi), enable Network MIDI 2.0 |

### Example: full interop run against the built-in server

In one terminal start the server:

```
dotnet run --project tests/NetworkMidi2.InteropTest -- server --port 5004
```

In a second terminal run the client with loopback enabled:

```
dotnet run --project tests/NetworkMidi2.InteropTest -- client --host 127.0.0.1 --port 5004 --loopback
```

Expected output (all checks pass):

```
Wireshark filter: udp.port == 5004

Client mode → 127.0.0.1:5004  (name=InteropTest)

  [1/6] Session handshake (Invitation → InvitationAccepted)…   PASS  (connected to 'InteropTest')
  [2/6] Ping / PingReply (liveness)…                           PASS  (session alive 300 ms after connect)
  [3/6] UMP round-trip (MIDI 1.0 Note On/Off)…                 PASS  (MIDI 1.0 Note On + Note Off echoed back)
  [4/6] UMP round-trip (MIDI 2.0 Note On/Off)…                 PASS  (MIDI 2.0 Note On + Note Off echoed back)
  [5/6] FEC encoding (history in sent packets)…                PASS  (outbound datagrams carry FEC history entries)
  [6/6] Clean disconnection (Bye / ByeReply)…                  PASS  (DisconnectAsync completed without error)

─────────────────────────────────────────────────────────
  PASS  handshake                     connected to 'InteropTest'
  PASS  ping                          session alive 300 ms after connect
  PASS  ump-midi1-roundtrip           MIDI 1.0 Note On + Note Off echoed back
  PASS  ump-midi2-roundtrip           MIDI 2.0 Note On + Note Off echoed back
  PASS  fec                           outbound datagrams carry FEC history entries
  PASS  disconnect                    DisconnectAsync completed without error
─────────────────────────────────────────────────────────
  6 passed  /  0 failed  /  0 skipped

All checks passed. ✓
```
