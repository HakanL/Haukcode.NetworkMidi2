using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;

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
    private readonly uint   localInitiatorToken;

    /// <summary>
    /// Optional diagnostic hook. Set to a logger action to trace session events and
    /// packet exchanges. Guard call sites with <c>if (TraceHook != null)</c>.
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

    // --- Outbound sequence tracking ---
    private uint outboundSeqNum;

    // --- FEC send-side history ---
    private readonly UmpFec.SendHistory fecHistory = new();

    // --- Inbound gap detection ---
    private uint? expectedSeqNum;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="localName">Name announced to peers (e.g. "My App").</param>
    public NetworkMidi2Session(string localName)
    {
        this.localName = localName;
        localInitiatorToken = NetworkMidi2Protocol.GenerateInitiatorToken();
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
        outboundSeqNum = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
        expectedSeqNum = null;

        socket = new UdpClient(0); // ephemeral local port
        socket.Connect(remoteEndPoint);

        if (TraceHook != null)
            TraceHook($"[{localName}] ConnectAsync target={endPoint} token={localInitiatorToken:X8}");

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

        outboundSeqNum = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
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

        uint seqNum  = outboundSeqNum++;
        var history  = fecHistory.GetHistory();
        var fecBlock = UmpFec.Encode(umpWords.Span, history);
        var payload  = UmpDataCommand.EncodePayload(seqNum, fecBlock);
        var datagram = NetworkMidi2Protocol.WrapWithHeader(NetworkMidi2Command.UmpData, payload);

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
                var bye = NetworkMidi2Protocol.Encode(new ByePacket(localInitiatorToken));
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
        byte[]? pinHash = Pin != null ? NetworkMidi2Protocol.ComputePinHash(Pin) : null;
        var invite  = new InvitationPacket(localInitiatorToken, localName, pinHash);
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
                        if (cmd is InvitationAcceptedPacket accepted
                            && accepted.InitiatorToken == localInitiatorToken)
                        {
                            RemoteName = accepted.RemoteName;
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX InvitationAccepted remote='{RemoteName}'");
                            return;
                        }
                        if (cmd is InvitationRefusedPacket refused
                            && refused.InitiatorToken == localInitiatorToken)
                        {
                            throw new InvalidOperationException(
                                $"Remote refused invitation: {refused.Reason}");
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
                    TraceHook($"[{localName}] RX Invitation from {result.RemoteEndPoint} name='{invite.LocalName}'");

                // PIN check
                if (Pin != null)
                {
                    var expectedHash = NetworkMidi2Protocol.ComputePinHash(Pin);
                    if (invite.PinHash == null
                        || !NetworkMidi2Protocol.PinHashesEqual(invite.PinHash, expectedHash))
                    {
                        var reason = invite.PinHash == null
                            ? InvitationRefusedReason.AuthRequired
                            : InvitationRefusedReason.AuthFailed;
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(new InvitationRefusedPacket(invite.InitiatorToken, reason)),
                            result.RemoteEndPoint, ct);
                        continue;
                    }
                }

                RemoteName = invite.LocalName;
                var accepted = new InvitationAcceptedPacket(invite.InitiatorToken, localName);
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

                foreach (var cmd in cmds)
                {
                    switch (cmd)
                    {
                        case UmpDataRawPayload raw:
                            HandleUmpDataPayload(raw.Bytes);
                            break;

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
                                TraceHook($"[{localName}] RX Bye — sending ByeReply and disconnecting");
                            try
                            {
                                await socket.SendAsync(
                                    NetworkMidi2Protocol.Encode(new ByeReplyPacket(bye.InitiatorToken)), ct);
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
        catch (SocketException)
        {
            if (State == SessionState.Connected)
                _ = DisconnectAsync();
        }
    }

    private void HandleUmpDataPayload(byte[] payloadBytes)
    {
        if (!UmpDataCommand.TryParsePayload(payloadBytes, out var seqNum, out var fecBlock))
            return;

        if (!UmpFec.TryDecode(fecBlock, out var primaryWords, out var historical))
            return;

        // FEC gap recovery
        if (expectedSeqNum.HasValue && seqNum != expectedSeqNum.Value)
        {
            foreach (var h in historical)
            {
                if (IsInGap(h.SequenceNumber, expectedSeqNum.Value, seqNum))
                {
                    if (TraceHook != null)
                        TraceHook($"[{localName}] FEC recovered seq={h.SequenceNumber} words={h.UmpWords.Length}");
                    umpSubject.OnNext(h.UmpWords);
                }
            }
        }

        expectedSeqNum = seqNum + 1;

        if (primaryWords is { Length: > 0 })
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] RX UMP seq={seqNum} words={primaryWords.Length}");
            umpSubject.OnNext(primaryWords);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="checkpointSeq"/> falls within the gap
    /// [<paramref name="gapStart"/>, <paramref name="receivedSeq"/> - 1] using
    /// unsigned 32-bit wraparound arithmetic.
    /// </summary>
    private static bool IsInGap(uint checkpointSeq, uint gapStart, uint receivedSeq)
        => (checkpointSeq - gapStart) < (receivedSeq - gapStart);

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
