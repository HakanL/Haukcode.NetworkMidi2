using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Haukcode.NetworkMidi2;

/// <summary>
/// Network MIDI 2.0 session (M2-124-UM v1.0).
///
/// Supports both client and host roles over a single UDP port:
///   - Client: call <see cref="ConnectAsync"/> with the host's endpoint.
///   - Host:   call <see cref="ListenAsync"/> to accept incoming connections.
///
/// UMP words are exposed as <see cref="UmpReceived"/> — each emission is one
/// complete UMP message group (the payload of one UMP Data Command).
/// </summary>
public sealed class NetworkMidi2Session : INetworkMidi2Session
{
    private const int DefaultPingIntervalMs = 10_000;
    private const int HandshakeTimeoutMs    = 5_000;

    private readonly string localName;

    /// <summary>
    /// Optional diagnostic hook. Set to a logger action to trace session events and
    /// packet exchanges.
    /// </summary>
    public static Action<string>? TraceHook;

    // --- Observables ---
    private readonly Subject<ReadOnlyMemory<uint>> umpSubject   = new();
    private readonly Subject<SessionState>          stateSubject = new();
    private SessionState _state = SessionState.Idle;

    // --- Socket and remote ---
    private UdpClient? socket;
    private IPEndPoint? remoteEndPoint;

    // --- Background loop cancellation ---
    private CancellationTokenSource? loopCts;
    private Task? receiveTask;
    private Task? pingTask;

    // --- Outbound sequence tracking (uint16, wraps at 0xFFFF) ---
    private ushort outboundSeqNum;

    // --- FEC send-side history ---
    private readonly UmpFec.SendHistory fecHistory = new();

    // --- Inbound gap detection ---
    private ushort? expectedSeqNum;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="localName">Name announced to peers (e.g. "My App").</param>
    public NetworkMidi2Session(string localName)
    {
        this.localName = localName;
    }

    // -------------------------------------------------------------------------
    // INetworkMidi2Session
    // -------------------------------------------------------------------------

    public IObservable<ReadOnlyMemory<uint>> UmpReceived => umpSubject.AsObservable();
    public IObservable<SessionState> StateChanges        => stateSubject.AsObservable();
    public string? RemoteName { get; private set; }
    public string? Pin { get; set; }

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            stateSubject.OnNext(value);
        }
    }

    // -------------------------------------------------------------------------
    // ConnectAsync (Client role)
    // -------------------------------------------------------------------------

    public async Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(umpSubject.IsDisposed, this);
        if (State != SessionState.Idle)
            throw new InvalidOperationException($"Session is not idle (current state: {State}).");

        remoteEndPoint = endPoint;
        outboundSeqNum = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        expectedSeqNum = null;

        socket = new UdpClient(0); // ephemeral local port
        socket.Connect(remoteEndPoint);

        if (TraceHook != null)
            TraceHook($"[{localName}] ConnectAsync target={endPoint}");

        State = SessionState.Connecting;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeoutMs);

        await HandshakeAsClientAsync(timeoutCts.Token);

        State = SessionState.Connected;
        StartLoops(ct);
    }

    // -------------------------------------------------------------------------
    // ListenAsync (Host role)
    // -------------------------------------------------------------------------

    public async Task ListenAsync(int port, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(umpSubject.IsDisposed, this);
        if (State != SessionState.Idle)
            throw new InvalidOperationException($"Session is not idle (current state: {State}).");

        outboundSeqNum = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        expectedSeqNum = null;

        socket = new UdpClient(port);

        if (TraceHook != null)
            TraceHook($"[{localName}] ListenAsync port={port}");

        State = SessionState.Connecting;

        remoteEndPoint = await AcceptAsHostAsync(ct);
        socket.Connect(remoteEndPoint);

        State = SessionState.Connected;
        StartLoops(ct);
    }

    // -------------------------------------------------------------------------
    // SendUmpAsync
    // -------------------------------------------------------------------------

    public async Task SendUmpAsync(ReadOnlyMemory<uint> umpWords, CancellationToken ct = default)
    {
        if (State != SessionState.Connected)
            throw new InvalidOperationException("Not connected.");

        ushort seqNum  = outboundSeqNum++;
        var history    = fecHistory.GetHistory();
        var datagram   = UmpFec.EncodeDatagram(seqNum, umpWords.Span, history);

        fecHistory.Record(seqNum, umpWords.ToArray());

        if (TraceHook != null)
            TraceHook($"[{localName}] TX UMP seq={seqNum} words={umpWords.Length} fec={history.Count}");

        await socket!.SendAsync(datagram, ct);
    }

    // -------------------------------------------------------------------------
    // DisconnectAsync
    // -------------------------------------------------------------------------

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Idle or SessionState.Disconnecting)
            return;

        var wasConnected = State == SessionState.Connected;
        State = SessionState.Disconnecting;

        if (wasConnected && socket != null)
        {
            try
            {
                var bye = NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.UserTerminated));
                if (TraceHook != null)
                    TraceHook($"[{localName}] TX Bye to {remoteEndPoint}");
                await socket.SendAsync(bye);
            }
            catch { /* best-effort */ }
        }

        await StopLoopsAsync();
        CloseAndNullSocket();

        State = SessionState.Idle;
    }

    // -------------------------------------------------------------------------
    // Reconnect loops
    // -------------------------------------------------------------------------

    public async Task ConnectWithReconnectAsync(IPEndPoint endPoint, TimeSpan reconnectDelay, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sessionEndedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = StateChanges
                .Where(s => s == SessionState.Idle)
                .Subscribe(_ => sessionEndedTcs.TrySetResult());

            try
            {
                await ConnectAsync(endPoint, ct);
                await sessionEndedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { await DisconnectAsync(); }

            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(reconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task ListenWithReconnectAsync(int port, TimeSpan reconnectDelay, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sessionEndedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = StateChanges
                .Where(s => s == SessionState.Idle)
                .Subscribe(_ => sessionEndedTcs.TrySetResult());

            try
            {
                await ListenAsync(port, ct);
                await sessionEndedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { await DisconnectAsync(); }

            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(reconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        umpSubject.OnCompleted();
        umpSubject.Dispose();
        stateSubject.OnCompleted();
        stateSubject.Dispose();
    }

    // -------------------------------------------------------------------------
    // Handshake: Client
    // -------------------------------------------------------------------------

    private async Task HandshakeAsClientAsync(CancellationToken ct)
    {
        var invite  = new InvitationPacket(localName);
        var encoded = NetworkMidi2Protocol.Encode(invite);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] TX Invitation to {remoteEndPoint} (attempt {attempt + 1}/3)");

            await socket!.SendAsync(encoded, ct);

            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            retryCts.CancelAfter(1_000);

            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(retryCts.Token);
                    if (!NetworkMidi2Protocol.TryParseAll(result.Buffer, out var cmds)) continue;

                    foreach (var cmd in cmds)
                    {
                        if (cmd is InvitationAcceptedPacket accepted)
                        {
                            RemoteName = accepted.EndpointName;
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX InvitationAccepted remote='{RemoteName}'");
                            return;
                        }

                        if (cmd is ByePacket bye)
                        {
                            throw new InvalidOperationException(
                                $"Remote refused invitation: {bye.Reason}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Inner 1 s retry expired — try again
            }
        }

        throw new TimeoutException($"Network MIDI 2.0 handshake timed out for {remoteEndPoint}.");
    }

    // -------------------------------------------------------------------------
    // Handshake: Host
    // -------------------------------------------------------------------------

    private async Task<IPEndPoint> AcceptAsHostAsync(CancellationToken ct)
    {
        while (true)
        {
            var result = await socket!.ReceiveAsync(ct);
            if (!NetworkMidi2Protocol.TryParseAll(result.Buffer, out var cmds)) continue;

            foreach (var cmd in cmds)
            {
                if (cmd is not InvitationPacket invite) continue;

                if (TraceHook != null)
                    TraceHook($"[{localName}] RX Invitation from {result.RemoteEndPoint} name='{invite.EndpointName}'");

                // Reject if PIN is required (auth not fully implemented yet)
                if (Pin != null)
                {
                    await socket.SendAsync(
                        NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.NoMatchingAuthMethod)),
                        result.RemoteEndPoint, ct);
                    continue;
                }

                RemoteName = invite.EndpointName;
                var accepted = new InvitationAcceptedPacket(localName);
                if (TraceHook != null)
                    TraceHook($"[{localName}] TX InvitationAccepted to {result.RemoteEndPoint}");
                await socket.SendAsync(
                    NetworkMidi2Protocol.Encode(accepted),
                    result.RemoteEndPoint, ct);

                return (IPEndPoint)result.RemoteEndPoint;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Receive loop
    // -------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket!.ReceiveAsync(ct);
                if (!NetworkMidi2Protocol.TryParseAll(result.Buffer, out var cmds)) continue;

                // Collect all UMP Data packets in datagram order (oldest FEC history first)
                var umpPackets = cmds.OfType<UmpDataPacket>().ToList();
                foreach (var pkt in umpPackets)
                    HandleUmpDataPacket(pkt);

                foreach (var cmd in cmds)
                {
                    switch (cmd)
                    {
                        case PingPacket ping:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX Ping id={ping.PingId:X8} — replying");
                            await socket.SendAsync(
                                NetworkMidi2Protocol.Encode(new PingReplyPacket(ping.PingId)), ct);
                            break;

                        case PingReplyPacket pingReply:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX PingReply id={pingReply.PingId:X8}");
                            break;

                        case ByePacket bye:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX Bye reason={bye.Reason} — sending ByeReply and disconnecting");
                            try
                            {
                                await socket.SendAsync(
                                    NetworkMidi2Protocol.Encode(new ByeReplyPacket()), ct);
                            }
                            catch { }
                            _ = DisconnectAsync();
                            break;

                        case ByeReplyPacket:
                            break; // expected after we send Bye
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (System.Net.Sockets.SocketException)
        {
            if (State == SessionState.Connected)
                _ = DisconnectAsync();
        }
    }

    private void HandleUmpDataPacket(UmpDataPacket pkt)
    {
        // Only emit if the sequence number is at or after what we expect
        // (handles both normal delivery and FEC-recovered historical packets)
        if (expectedSeqNum.HasValue && !IsSequenceAtOrAfter(pkt.SequenceNumber, expectedSeqNum.Value))
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] RX UMP seq={pkt.SequenceNumber} — duplicate/old, skipped");
            return;
        }

        expectedSeqNum = (ushort)(pkt.SequenceNumber + 1);

        if (pkt.UmpWords.Length > 0)
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] RX UMP seq={pkt.SequenceNumber} words={pkt.UmpWords.Length}");
            umpSubject.OnNext(pkt.UmpWords);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is at or after <paramref name="expected"/>
    /// in the uint16 sequence number space, using unsigned wraparound arithmetic.
    /// Assumes the distance is always less than half the range (32768).
    /// </summary>
    private static bool IsSequenceAtOrAfter(ushort candidate, ushort expected)
        => (ushort)(candidate - expected) < 0x8000;

    // -------------------------------------------------------------------------
    // Ping loop
    // -------------------------------------------------------------------------

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(DefaultPingIntervalMs, ct);
                if (State != SessionState.Connected) continue;

                var pingId = NetworkMidi2Protocol.GeneratePingId();
                if (TraceHook != null)
                    TraceHook($"[{localName}] TX Ping id={pingId:X8}");
                await socket!.SendAsync(
                    NetworkMidi2Protocol.Encode(new PingPacket(pingId)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // -------------------------------------------------------------------------
    // Loop lifecycle
    // -------------------------------------------------------------------------

    private void StartLoops(CancellationToken ct)
    {
        loopCts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiveTask = ReceiveLoopAsync(loopCts.Token);
        pingTask    = PingLoopAsync(loopCts.Token);
    }

    private async Task StopLoopsAsync()
    {
        loopCts?.Cancel();
        var tasks = new[] { receiveTask, pingTask }.Where(t => t != null).Select(t => t!);
        try { await Task.WhenAll(tasks); } catch { }
        loopCts?.Dispose();
        loopCts = null;
    }

    private void CloseAndNullSocket()
    {
        socket?.Close();
        socket = null;
    }
}
