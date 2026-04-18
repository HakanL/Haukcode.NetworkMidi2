namespace Haukcode.NetworkMidi2;

/// <summary>
/// A Network MIDI 2.0 peer discovered via mDNS (_midi2._udp).
/// All session traffic uses the single <see cref="EndPoint"/> — there is no separate data port.
/// </summary>
public record NetworkMidi2Peer(string Name, IPEndPoint EndPoint);
