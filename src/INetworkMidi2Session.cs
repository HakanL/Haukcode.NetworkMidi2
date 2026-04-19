namespace Haukcode.NetworkMidi2;

public interface INetworkMidi2Session : IAsyncDisposable
{
    /// <summary>
    /// Connect to a remote Network MIDI 2.0 host as a client.
    /// Returns once the invitation handshake is complete.
    /// </summary>
    Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default);

    /// <summary>
    /// Connect and automatically reconnect whenever the session drops.
    /// Loops until <paramref name="ct"/> is cancelled.
    /// </summary>
    Task ConnectWithReconnectAsync(IPEndPoint endPoint, TimeSpan reconnectDelay, CancellationToken ct = default);

    /// <summary>
    /// Listen for an incoming client connection on <paramref name="port"/>.
    /// Returns once the invitation handshake is complete.
    /// </summary>
    Task ListenAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// Listen and automatically re-listen after each session ends.
    /// Loops until <paramref name="ct"/> is cancelled.
    /// </summary>
    Task ListenWithReconnectAsync(int port, TimeSpan reconnectDelay, CancellationToken ct = default);

    /// <summary>Send a Bye command and close the session gracefully.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Send a SessionReset command to the remote peer and reset local sequence tracking.
    /// Both sides restart their sequence counters without closing the session.
    /// </summary>
    Task SendSessionResetAsync(CancellationToken ct = default);

    /// <summary>
    /// Send UMP words to the remote peer.
    /// Words must be in host byte order; the codec writes them big-endian on the wire.
    /// </summary>
    Task SendUmpAsync(ReadOnlyMemory<uint> umpWords, CancellationToken ct = default);

    /// <summary>
    /// Stream of UMP word payloads received from the remote peer.
    /// Words are in host byte order. Completes when the session is disposed.
    /// </summary>
    IObservable<ReadOnlyMemory<uint>> UmpReceived { get; }

    /// <summary>Stream of session state transitions. Completes when disposed.</summary>
    IObservable<SessionState> StateChanges { get; }

    SessionState State { get; }

    /// <summary>Name advertised by the remote peer, populated after connection.</summary>
    string? RemoteName { get; }

    /// <summary>
    /// Optional PIN for authentication.
    /// Null (default) = open session (no authentication).
    /// When set on a client, the SHA-256 hash is sent in the Invitation.
    /// When set on a host, incoming invitations must include the matching hash.
    /// </summary>
    string? Pin { get; set; }
}
