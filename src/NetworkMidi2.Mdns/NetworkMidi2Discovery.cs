using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Haukcode.Mdns;

namespace Haukcode.NetworkMidi2.Mdns;

/// <summary>
/// Discovers Network MIDI 2.0 peers on the local network via mDNS (_midi2._udp).
///
/// Compatible with macOS 26.4+ (CoreMIDI), Windows MIDI Services, and any
/// M2-124-UM conformant implementation that registers via mDNS.
/// </summary>
public sealed class NetworkMidi2Discovery : IDisposable
{
    private const string ServiceType = "_midi2._udp";

    private readonly Subject<NetworkMidi2Peer> foundSubject = new();
    private readonly Subject<NetworkMidi2Peer> lostSubject  = new();
    private readonly MdnsBrowser browser;
    private bool disposed;

    /// <summary>Emits each peer the moment it is first discovered.</summary>
    public IObservable<NetworkMidi2Peer> PeersFound => foundSubject.AsObservable();

    /// <summary>Emits a peer when its PTR TTL expires and it is not refreshed.</summary>
    public IObservable<NetworkMidi2Peer> PeersLost => lostSubject.AsObservable();

    public NetworkMidi2Discovery()
    {
        browser = new MdnsBrowser(ServiceType);
        browser.ServiceFound += OnServiceFound;
        browser.ServiceLost  += OnServiceLost;
    }

    // -------------------------------------------------------------------------
    // One-shot resolve
    // -------------------------------------------------------------------------

    /// <summary>Perform a one-shot mDNS scan and return all currently-advertising peers.</summary>
    public static async Task<IReadOnlyList<NetworkMidi2Peer>> ResolveAsync(
        TimeSpan? scanTime = null,
        CancellationToken ct = default)
    {
        using var browser = new MdnsBrowser(ServiceType);
        var found = new List<NetworkMidi2Peer>();

        browser.ServiceFound += svc =>
        {
            lock (found)
                found.Add(ToPeer(svc));
        };

        browser.Start();
        await Task.Delay(scanTime ?? TimeSpan.FromSeconds(2), ct);

        return found.ToList();
    }

    // -------------------------------------------------------------------------
    // Continuous monitoring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start continuous mDNS monitoring. <see cref="PeersFound"/> emits as new
    /// devices appear; <see cref="PeersLost"/> emits when their TTL expires.
    /// Call <see cref="Dispose"/> to stop.
    /// </summary>
    public void StartMonitoring()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        browser.Start();
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private void OnServiceFound(ServiceProfile profile)
        => foundSubject.OnNext(ToPeer(profile));

    private void OnServiceLost(ServiceProfile profile)
        => lostSubject.OnNext(ToPeer(profile));

    private static NetworkMidi2Peer ToPeer(ServiceProfile profile)
    {
        var address = profile.Address ?? IPAddress.Loopback;
        return new NetworkMidi2Peer(profile.InstanceName, new IPEndPoint(address, profile.Port));
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        browser.ServiceFound -= OnServiceFound;
        browser.ServiceLost  -= OnServiceLost;
        browser.Dispose();

        foundSubject.OnCompleted();
        foundSubject.Dispose();
        lostSubject.OnCompleted();
        lostSubject.Dispose();
    }
}
