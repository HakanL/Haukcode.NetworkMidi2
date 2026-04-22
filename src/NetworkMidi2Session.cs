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

    // Serialises SendUmpAsync so concurrent callers can't race the
    // outboundSeqNum increment + GetHistory + Record sequence (which would
    // otherwise reuse a sequence number or attach the wrong FEC history).
    private readonly SemaphoreSlim sendGate = new(1, 1);

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
    public string? Username { get; set; }
    public string? Password { get; set; }

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

        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ushort seqNum  = outboundSeqNum++;
            var history    = fecHistory.GetHistory();
            var datagram   = UmpFec.EncodeDatagram(seqNum, umpWords.Span, history);

            fecHistory.Record(seqNum, umpWords.ToArray());

            if (TraceHook != null)
                TraceHook($"[{localName}] TX UMP seq={seqNum} words={umpWords.Length} fec={history.Count}");

            await socket!.SendAsync(datagram, ct);
        }
        finally
        {
            sendGate.Release();
        }
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
    // SendSessionResetAsync
    // -------------------------------------------------------------------------

    public async Task SendSessionResetAsync(CancellationToken ct = default)
    {
        if (State != SessionState.Connected)
            throw new InvalidOperationException("Not connected.");

        if (TraceHook != null)
            TraceHook($"[{localName}] TX SessionReset to {remoteEndPoint}");

        await socket!.SendAsync(
            NetworkMidi2Protocol.Encode(new SessionResetPacket()), ct);

        ResetSequenceTracking();
        outboundSeqNum = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        fecHistory.Clear();
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

                        if (cmd is InvitationPendingPacket)
                        {
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX InvitationPending — host is busy, will retry");
                            // Break inner loop to trigger outer retry with delay
                            await Task.Delay(500, ct);
                            goto nextAttempt;
                        }

                        if (cmd is InvitationAuthenticationRequiredPacket authRequired)
                        {
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX InvitationAuthenticationRequired authState={authRequired.AuthState}");

                            if (Pin == null)
                            {
                                throw new InvalidOperationException(
                                    "Host requires PIN authentication but no Pin is set.");
                            }

                            var digest     = NetworkMidi2Protocol.ComputeAuthDigest(Pin, authRequired.Nonce);
                            var authPacket = new InvitationAuthenticatePacket(digest, localName, "");
                            if (TraceHook != null)
                                TraceHook($"[{localName}] TX InvitationAuthenticate");
                            await socket.SendAsync(
                                NetworkMidi2Protocol.Encode(authPacket), ct);
                            // Continue waiting for InvitationAccepted or Bye
                            continue;
                        }

                        if (cmd is InvitationUserAuthenticationRequiredPacket userAuthRequired)
                        {
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX InvitationUserAuthenticationRequired authState={userAuthRequired.AuthState}");

                            if (Username == null || Password == null)
                            {
                                throw new InvalidOperationException(
                                    "Host requires user authentication but no Username/Password is set.");
                            }

                            var digest     = NetworkMidi2Protocol.ComputeUserAuthDigest(Password, userAuthRequired.Nonce);
                            var authPacket = new InvitationUserAuthenticatePacket(digest, localName, "", Username);
                            if (TraceHook != null)
                                TraceHook($"[{localName}] TX InvitationUserAuthenticate user='{Username}'");
                            await socket.SendAsync(
                                NetworkMidi2Protocol.Encode(authPacket), ct);
                            // Continue waiting for InvitationAccepted or Bye
                            continue;
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
            nextAttempt:;
        }

        throw new TimeoutException($"Network MIDI 2.0 handshake timed out for {remoteEndPoint}.");
    }

    // -------------------------------------------------------------------------
    // Handshake: Host
    // -------------------------------------------------------------------------

    private async Task<IPEndPoint> AcceptAsHostAsync(CancellationToken ct)
    {
        // Tracks pending PIN auth: maps client endpoint → nonce we sent
        var pendingAuth   = new Dictionary<string, byte[]>();
        var pendingRetry  = new HashSet<string>();

        // Tracks pending user auth: maps client endpoint → nonce we sent
        var pendingUserAuth  = new Dictionary<string, byte[]>();
        var pendingUserRetry = new HashSet<string>();

        while (true)
        {
            var result = await socket!.ReceiveAsync(ct);
            if (!NetworkMidi2Protocol.TryParseAll(result.Buffer, out var cmds)) continue;

            var clientKey = result.RemoteEndPoint.ToString();

            foreach (var cmd in cmds)
            {
                if (cmd is InvitationPacket invite)
                {
                    if (TraceHook != null)
                        TraceHook($"[{localName}] RX Invitation from {result.RemoteEndPoint} name='{invite.EndpointName}'");

                    if (Username != null && Password != null)
                    {
                        // Generate a nonce and challenge the client with user auth
                        var nonce = RandomNumberGenerator.GetBytes(16);
                        pendingUserAuth[clientKey] = nonce;
                        pendingUserRetry.Remove(clientKey);

                        var challenge = new InvitationUserAuthenticationRequiredPacket(
                            nonce, localName, "", AuthState: 0);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationUserAuthenticationRequired to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(challenge),
                            result.RemoteEndPoint, ct);
                        continue;
                    }

                    if (Pin != null)
                    {
                        // Generate a nonce and challenge the client
                        var nonce = RandomNumberGenerator.GetBytes(16);
                        pendingAuth[clientKey] = nonce;
                        pendingRetry.Remove(clientKey);

                        var challenge = new InvitationAuthenticationRequiredPacket(
                            nonce, localName, "", AuthState: 0);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationAuthenticationRequired to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(challenge),
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

                if (cmd is InvitationAuthenticatePacket authResponse && Pin != null)
                {
                    if (TraceHook != null)
                        TraceHook($"[{localName}] RX InvitationAuthenticate from {result.RemoteEndPoint}");

                    if (!pendingAuth.TryGetValue(clientKey, out var storedNonce))
                    {
                        // No pending auth for this client — ignore
                        continue;
                    }

                    var expectedDigest = NetworkMidi2Protocol.ComputeAuthDigest(Pin, storedNonce);
                    if (NetworkMidi2Protocol.PinHashesEqual(authResponse.AuthDigest, expectedDigest))
                    {
                        pendingAuth.Remove(clientKey);
                        pendingRetry.Remove(clientKey);
                        RemoteName = authResponse.EndpointName;
                        var accepted = new InvitationAcceptedPacket(localName);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationAccepted (PIN OK) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(accepted),
                            result.RemoteEndPoint, ct);

                        return (IPEndPoint)result.RemoteEndPoint;
                    }
                    else if (!pendingRetry.Contains(clientKey))
                    {
                        // Wrong PIN — allow one retry with a new nonce
                        var nonce = RandomNumberGenerator.GetBytes(16);
                        pendingAuth[clientKey] = nonce;
                        pendingRetry.Add(clientKey);

                        var challenge = new InvitationAuthenticationRequiredPacket(
                            nonce, localName, "", AuthState: 1);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationAuthenticationRequired (retry) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(challenge),
                            result.RemoteEndPoint, ct);
                    }
                    else
                    {
                        // Second failure — reject
                        pendingAuth.Remove(clientKey);
                        pendingRetry.Remove(clientKey);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX Bye (AuthFailed) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.AuthFailed)),
                            result.RemoteEndPoint, ct);
                    }
                }

                if (cmd is InvitationUserAuthenticatePacket userAuthResponse && Username != null && Password != null)
                {
                    if (TraceHook != null)
                        TraceHook($"[{localName}] RX InvitationUserAuthenticate from {result.RemoteEndPoint} user='{userAuthResponse.UserName}'");

                    if (!pendingUserAuth.TryGetValue(clientKey, out var storedNonce))
                    {
                        // No pending user auth for this client — ignore
                        continue;
                    }

                    if (userAuthResponse.UserName != Username)
                    {
                        // Unknown username — reject immediately
                        pendingUserAuth.Remove(clientKey);
                        pendingUserRetry.Remove(clientKey);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX Bye (UserNameNotFound) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.UserNameNotFound)),
                            result.RemoteEndPoint, ct);
                        continue;
                    }

                    var expectedDigest = NetworkMidi2Protocol.ComputeUserAuthDigest(Password, storedNonce);
                    if (NetworkMidi2Protocol.PinHashesEqual(userAuthResponse.AuthDigest, expectedDigest))
                    {
                        pendingUserAuth.Remove(clientKey);
                        pendingUserRetry.Remove(clientKey);
                        RemoteName = userAuthResponse.EndpointName;
                        var accepted = new InvitationAcceptedPacket(localName);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationAccepted (user auth OK) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(accepted),
                            result.RemoteEndPoint, ct);

                        return (IPEndPoint)result.RemoteEndPoint;
                    }
                    else if (!pendingUserRetry.Contains(clientKey))
                    {
                        // Wrong password — allow one retry with a new nonce
                        var nonce = RandomNumberGenerator.GetBytes(16);
                        pendingUserAuth[clientKey] = nonce;
                        pendingUserRetry.Add(clientKey);

                        var challenge = new InvitationUserAuthenticationRequiredPacket(
                            nonce, localName, "", AuthState: 1);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX InvitationUserAuthenticationRequired (retry) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(challenge),
                            result.RemoteEndPoint, ct);
                    }
                    else
                    {
                        // Second failure — reject
                        pendingUserAuth.Remove(clientKey);
                        pendingUserRetry.Remove(clientKey);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX Bye (AuthFailed) to {result.RemoteEndPoint}");
                        await socket.SendAsync(
                            NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.AuthFailed)),
                            result.RemoteEndPoint, ct);
                    }
                }
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
                var umpPackets    = cmds.OfType<UmpDataPacket>().ToList();
                var expectedBefore = expectedSeqNum;
                foreach (var pkt in umpPackets)
                    HandleUmpDataPacket(pkt);

                // After FEC has had a chance to fill the gap, request retransmit
                // for any sequence numbers that are still missing. Bounded to avoid
                // runaway requests after a long pause or large burst loss.
                if (expectedBefore.HasValue && umpPackets.Count > 0)
                    await RequestRetransmitIfGapAsync(expectedBefore.Value, umpPackets, ct);

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

                        case NakPacket nak:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX Nak reason={nak.Reason}");
                            break;

                        case RetransmitPacket retransmit:
                            await HandleRetransmitAsync(retransmit, ct);
                            break;

                        case RetransmitErrorPacket retransmitError:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX RetransmitError seqs=[{string.Join(",", retransmitError.SequenceNumbers)}]");
                            break;

                        case SessionResetPacket:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX SessionReset — resetting sequence tracking");
                            ResetSequenceTracking();
                            await socket.SendAsync(
                                NetworkMidi2Protocol.Encode(new SessionResetReplyPacket()), ct);
                            break;

                        case SessionResetReplyPacket:
                            if (TraceHook != null)
                                TraceHook($"[{localName}] RX SessionResetReply");
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

    private const int MaxRetransmitRequestCount = 64;

    private async Task RequestRetransmitIfGapAsync(
        ushort expectedBefore,
        List<UmpDataPacket> umpPackets,
        CancellationToken ct)
    {
        // Find the lowest "new" sequence (>= expectedBefore under wraparound).
        ushort? lowestNew = null;
        foreach (var pkt in umpPackets)
        {
            if (!IsSequenceAtOrAfter(pkt.SequenceNumber, expectedBefore))
                continue;
            if (lowestNew is null || IsSequenceAtOrAfter(lowestNew.Value, pkt.SequenceNumber))
                lowestNew = pkt.SequenceNumber;
        }

        if (lowestNew is null || lowestNew.Value == expectedBefore)
            return;

        var missing = new List<ushort>();
        ushort s = expectedBefore;
        while (s != lowestNew.Value && missing.Count < MaxRetransmitRequestCount)
        {
            missing.Add(s);
            s++;
        }

        if (missing.Count == 0)
            return;

        if (TraceHook != null)
            TraceHook($"[{localName}] TX Retransmit request seqs=[{string.Join(",", missing)}]");
        await socket!.SendAsync(
            NetworkMidi2Protocol.Encode(new RetransmitPacket([.. missing])), ct);
    }

    private async Task HandleRetransmitAsync(RetransmitPacket retransmit, CancellationToken ct)
    {
        if (TraceHook != null)
            TraceHook($"[{localName}] RX Retransmit seqs=[{string.Join(",", retransmit.SequenceNumbers)}]");

        var unavailable = new List<ushort>();

        foreach (var seqNum in retransmit.SequenceNumbers)
        {
            if (fecHistory.TryGet(seqNum, out var umpWords))
            {
                // Resend the requested sequence as a plain UMP datagram (no extra FEC)
                var datagram = UmpFec.EncodeDatagram(seqNum, umpWords, []);
                if (TraceHook != null)
                    TraceHook($"[{localName}] TX Retransmit seq={seqNum}");
                await socket!.SendAsync(datagram, ct);
            }
            else
            {
                unavailable.Add(seqNum);
            }
        }

        if (unavailable.Count > 0)
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] TX RetransmitError seqs=[{string.Join(",", unavailable)}]");
            await socket!.SendAsync(
                NetworkMidi2Protocol.Encode(new RetransmitErrorPacket([.. unavailable])), ct);
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

    private void ResetSequenceTracking()
    {
        expectedSeqNum = null;
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
