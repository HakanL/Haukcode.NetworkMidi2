using System.Net;
using Haukcode.Mdns;

namespace Haukcode.NetworkMidi2.Mdns;

/// <summary>
/// Advertises a Network MIDI 2.0 session on the local network via mDNS (_midi2._udp)
/// so that other devices can discover it without requiring manual IP entry.
///
/// Usage:
///   1. Create a <see cref="NetworkMidi2Advertiser"/> with your session name and port.
///   2. Call <see cref="Start"/> — the session becomes visible on the network.
///   3. Call <see cref="Dispose"/> to send goodbye packets and remove the advertisement.
/// </summary>
public sealed class NetworkMidi2Advertiser : IDisposable
{
    private readonly MdnsAdvertiser advertiser;
    private bool disposed;

    /// <param name="sessionName">
    /// Human-readable name for the session, visible in macOS Audio MIDI Setup and other clients.
    /// </param>
    /// <param name="port">
    /// The UDP port to advertise. Default is 5004 (IANA-assigned port for Network MIDI 2.0).
    /// </param>
    /// <param name="localAddress">
    /// Local IPv4 address for the A record. If null, the best available address is selected.
    /// </param>
    /// <param name="properties">Optional TXT record properties.</param>
    public NetworkMidi2Advertiser(
        string sessionName,
        ushort port = 5004,
        IPAddress? localAddress = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var profile = new ServiceProfile(sessionName, "_midi2._udp", port, properties);
        advertiser = new MdnsAdvertiser(profile, localAddress);
    }

    /// <summary>Start advertising the session on the local network.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        advertiser.Start();
    }

    /// <summary>
    /// Stop advertising — sends mDNS goodbye packets so other devices remove this
    /// session from their lists promptly.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        advertiser.Dispose();
    }
}
