# Haukcode.NetworkMidi2

Network MIDI 2.0 (M2-124-UM v1.0) for .NET — bidirectional UMP over IP with full session protocol support.

## Key Features

- M2-124-UM v1.0 session protocol (Invitation / InvitationAccepted / Ping / Bye) — both client and host roles
- Universal MIDI Packet (UMP) encoding/decoding — all message types (MIDI 1.0, MIDI 2.0, SysEx 7/8, Flex Data, UMP Stream)
- Forward Error Correction (FEC) — up to 2 historical UMP payloads piggy-backed on every outbound datagram
- `IObservable<T>` streams for received UMP and state changes (System.Reactive)
- Cross-platform: Windows, Linux ARM64, macOS — pure managed C#

## Installation

```
dotnet add package Haukcode.NetworkMidi2
```

For mDNS peer discovery and advertising:

```
dotnet add package Haukcode.NetworkMidi2.Mdns
```

## Quick Start

```csharp
await using var session = new NetworkMidi2Session("My App");

session.UmpReceived.Subscribe(umpWords =>
{
    var hex = string.Join(" ", umpWords.ToArray().Select(w => $"{w:X8}"));
    Console.WriteLine($"UMP: {hex}");
});

// Single UDP port — no separate control/data ports
await session.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 5004));

// Send a MIDI 2.0 Note On (type 0x4, group 0, channel 0, pitch 60, velocity 0x8000)
await session.SendUmpAsync(new uint[] { 0x4090_3C00u, 0x8000_0000u });
```

## Compatible Implementations

- **macOS 26.4+** — Built-in CoreMIDI (Audio MIDI Setup → Network MIDI)
- **Windows 11 24H2+** — Windows MIDI Services ([aka.ms/midi](https://aka.ms/midi))
- Any M2-124-UM conformant hardware or software on a single UDP port

## Links

- [GitHub](https://github.com/HakanL/Haukcode.NetworkMidi2)
- [M2-124-UM Specification](https://midi.org/specifications)
