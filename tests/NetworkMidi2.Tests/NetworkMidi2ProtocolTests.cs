using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

public class NetworkMidi2ProtocolTests
{
    // -------------------------------------------------------------------------
    // Wire format constants
    // -------------------------------------------------------------------------

    private static ReadOnlySpan<byte> MidiMagic => [0x4D, 0x49, 0x44, 0x49]; // "MIDI"

    // -------------------------------------------------------------------------
    // Invitation (0x01)
    // -------------------------------------------------------------------------

    [Fact]
    public void Invitation_RoundTrip_BasicName()
    {
        var original = new InvitationPacket("Test Session");
        var encoded  = NetworkMidi2Protocol.Encode(original);

        // Verify magic at position 0
        Assert.Equal(MidiMagic.ToArray(), encoded[..4]);
        // Verify command code byte
        Assert.Equal(0x01, encoded[4]);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationPacket>(cmds[0]);

        Assert.Equal("Test Session", parsed.EndpointName);
        Assert.Equal("", parsed.ProductInstanceId);
    }

    [Fact]
    public void Invitation_RoundTrip_WithProductId()
    {
        var original = new InvitationPacket("My Device", "SN-12345");
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationPacket>(cmds[0]);

        Assert.Equal("My Device", parsed.EndpointName);
        Assert.Equal("SN-12345", parsed.ProductInstanceId);
    }

    [Fact]
    public void Invitation_NameWordCount_InCSD1()
    {
        // "ABCD" = 4 bytes = 1 word → CSD1 = 1
        var encoded = NetworkMidi2Protocol.Encode(new InvitationPacket("ABCD"));
        Assert.Equal(1, encoded[6]); // CSD1

        // "ABCDE" = 5 bytes → ceil(5/4) = 2 words → CSD1 = 2
        var encoded2 = NetworkMidi2Protocol.Encode(new InvitationPacket("ABCDE"));
        Assert.Equal(2, encoded2[6]);
    }

    [Fact]
    public void Invitation_PayloadLength_IsWordCount()
    {
        var encoded = NetworkMidi2Protocol.Encode(new InvitationPacket("Hi"));
        // PayloadLengthWords is at encoded[5]
        int payloadWords = encoded[5];
        // actual payload = (total - magic:4 - header:4)
        int actualPayloadBytes = encoded.Length - 8;
        Assert.Equal(payloadWords * 4, actualPayloadBytes);
    }

    // -------------------------------------------------------------------------
    // InvitationAccepted (0x10)
    // -------------------------------------------------------------------------

    [Fact]
    public void InvitationAccepted_RoundTrip()
    {
        var original = new InvitationAcceptedPacket("Bridge");
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x10, encoded[4]); // command code

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationAcceptedPacket>(cmds[0]);

        Assert.Equal("Bridge", parsed.EndpointName);
    }

    // -------------------------------------------------------------------------
    // Ping (0x20) / PingReply (0x21)
    // -------------------------------------------------------------------------

    [Fact]
    public void Ping_RoundTrip()
    {
        var original = new PingPacket(0xFEDCBA98);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x20, encoded[4]);  // code
        Assert.Equal(1,    encoded[5]);  // payloadWordLen = 1
        Assert.Equal(0,    encoded[6]);  // CSD1 = 0
        Assert.Equal(0,    encoded[7]);  // CSD2 = 0

        // PingId at bytes 8-11 big-endian
        Assert.Equal(0xFE, encoded[8]);
        Assert.Equal(0xDC, encoded[9]);
        Assert.Equal(0xBA, encoded[10]);
        Assert.Equal(0x98, encoded[11]);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<PingPacket>(cmds[0]);
        Assert.Equal(0xFEDCBA98u, parsed.PingId);
    }

    [Fact]
    public void PingReply_RoundTrip()
    {
        var original = new PingReplyPacket(0x00112233);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x21, encoded[4]);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<PingReplyPacket>(cmds[0]);
        Assert.Equal(0x00112233u, parsed.PingId);
    }

    // -------------------------------------------------------------------------
    // Bye (0xF0) / ByeReply (0xF1)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ByeReason.UserTerminated)]
    [InlineData(ByeReason.PowerDown)]
    [InlineData(ByeReason.Timeout)]
    [InlineData(ByeReason.TooManyOpenSessions)]
    [InlineData(ByeReason.AuthFailed)]
    public void Bye_RoundTrip_AllReasons(ByeReason reason)
    {
        var original = new ByePacket(reason);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0xF0, encoded[4]);     // code
        Assert.Equal(0,    encoded[5]);     // payloadWordLen = 0 (no payload)
        Assert.Equal((byte)reason, encoded[6]); // CSD1 = reason

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<ByePacket>(cmds[0]);
        Assert.Equal(reason, parsed.Reason);
    }

    [Fact]
    public void ByeReply_RoundTrip()
    {
        var original = new ByeReplyPacket();
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0xF1, encoded[4]);
        Assert.Equal(0,    encoded[5]); // no payload

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        Assert.IsType<ByeReplyPacket>(cmds[0]);
    }

    // -------------------------------------------------------------------------
    // Multiple commands in one datagram
    // -------------------------------------------------------------------------

    [Fact]
    public void MultipleCommandsInOneDatagram_AllParsed()
    {
        // CombineDatagram packs multiple command bodies under one "MIDI" magic
        var combined = NetworkMidi2Protocol.CombineDatagram(
            NetworkMidi2Protocol.Encode(new PingPacket(0x11111111)),
            NetworkMidi2Protocol.Encode(new ByePacket(ByeReason.UserTerminated)),
            NetworkMidi2Protocol.Encode(new PingReplyPacket(0x33333333)));

        Assert.True(NetworkMidi2Protocol.TryParseAll(combined, out var cmds));
        Assert.Equal(3, cmds.Count);

        Assert.IsType<PingPacket>(cmds[0]);
        Assert.IsType<ByePacket>(cmds[1]);
        Assert.IsType<PingReplyPacket>(cmds[2]);
    }

    // -------------------------------------------------------------------------
    // UMP Data (0xFF) — parsed by TryParseAll
    // -------------------------------------------------------------------------

    [Fact]
    public void UmpData_ParsedByTryParseAll()
    {
        // Build a minimal UMP Data datagram manually:
        // [magic:4][0xFF][wordCount=1][seqHi=0x00][seqLo=0x05][ump_word:4]
        byte[] datagram =
        [
            0x4D, 0x49, 0x44, 0x49,  // "MIDI" magic
            0xFF,                     // code = UMP Data
            0x01,                     // payloadWordLen = 1
            0x00,                     // CSD1 = seqNum high byte = 0
            0x05,                     // CSD2 = seqNum low byte  = 5  → seqNum = 5
            0x20, 0x90, 0x60, 0x40,  // UMP word (MIDI1 Note-On)
        ];

        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram, out var cmds));
        // UmpDataPacket is internal; cast via pattern matching
        Assert.Single(cmds);
        // The object type name validates it decoded correctly
        Assert.Equal("UmpDataPacket", cmds[0].GetType().Name);
    }

    [Fact]
    public void UmpData_SequenceNumber_ReadFromCSD()
    {
        // seqNum = 0x1234 → CSD1=0x12, CSD2=0x34
        byte[] datagram =
        [
            0x4D, 0x49, 0x44, 0x49,
            0xFF,
            0x01,    // 1 word payload
            0x12,    // CSD1 = high byte
            0x34,    // CSD2 = low byte  → seqNum = 0x1234
            0x20, 0x00, 0x00, 0x00,
        ];

        // Parse via UmpDataCommand directly
        Assert.True(UmpDataCommand.TryDecode(datagram[4..], out ushort seqNum, out _, out _));
        Assert.Equal((ushort)0x1234, seqNum);
    }

    // -------------------------------------------------------------------------
    // Parse rejection
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsWrongMagic()
    {
        byte[] buf = [0x00, 0x49, 0x44, 0x49, 0x20, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    [Fact]
    public void Parse_RejectsTooShort()
    {
        byte[] buf = [0x4D, 0x49, 0x44]; // truncated magic
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    [Fact]
    public void Parse_TruncatedPayload_ReturnsFalse()
    {
        // Declares 1-word (4-byte) payload but only provides 2 bytes
        byte[] buf =
        [
            0x4D, 0x49, 0x44, 0x49,  // magic
            0x20,                     // Ping
            0x01,                     // payloadWordLen = 1 (expects 4 bytes)
            0x00, 0x00,               // CSD1, CSD2
            0x00, 0x01,               // only 2 bytes of payload (truncated)
        ];
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    // -------------------------------------------------------------------------
    // PIN hash helpers
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputePinHash_IsSha256_32Bytes()
    {
        var hash = NetworkMidi2Protocol.ComputePinHash("1234");
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputePinHash_SamePin_SameHash()
    {
        var h1 = NetworkMidi2Protocol.ComputePinHash("abc");
        var h2 = NetworkMidi2Protocol.ComputePinHash("abc");
        Assert.True(NetworkMidi2Protocol.PinHashesEqual(h1, h2));
    }

    [Fact]
    public void ComputePinHash_DifferentPin_DifferentHash()
    {
        var h1 = NetworkMidi2Protocol.ComputePinHash("abc");
        var h2 = NetworkMidi2Protocol.ComputePinHash("xyz");
        Assert.False(NetworkMidi2Protocol.PinHashesEqual(h1, h2));
    }

    // -------------------------------------------------------------------------
    // InvitationPending (0x11)
    // -------------------------------------------------------------------------

    [Fact]
    public void InvitationPending_RoundTrip()
    {
        var encoded = NetworkMidi2Protocol.Encode(new InvitationPendingPacket());

        Assert.Equal(0x11, encoded[4]); // command code
        Assert.Equal(0,    encoded[5]); // no payload

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        Assert.IsType<InvitationPendingPacket>(cmds[0]);
    }

    // -------------------------------------------------------------------------
    // InvitationAuthenticationRequired (0x12)
    // -------------------------------------------------------------------------

    [Fact]
    public void InvitationAuthenticationRequired_RoundTrip()
    {
        var nonce    = new byte[16];
        for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 1);

        var original = new InvitationAuthenticationRequiredPacket(nonce, "Host", "ID-001", AuthState: 0);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x12, encoded[4]); // command code
        Assert.Equal(0,    encoded[7]); // CSD2 = auth state 0

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationAuthenticationRequiredPacket>(cmds[0]);

        Assert.Equal(nonce, parsed.Nonce);
        Assert.Equal("Host", parsed.EndpointName);
        Assert.Equal("ID-001", parsed.ProductInstanceId);
        Assert.Equal(0, parsed.AuthState);
    }

    [Fact]
    public void InvitationAuthenticationRequired_AuthState1_RoundTrip()
    {
        var nonce   = new byte[16];
        var original = new InvitationAuthenticationRequiredPacket(nonce, "Host", "", AuthState: 1);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(1, encoded[7]); // CSD2 = auth state 1

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationAuthenticationRequiredPacket>(cmds[0]);
        Assert.Equal(1, parsed.AuthState);
    }

    // -------------------------------------------------------------------------
    // InvitationAuthenticate (0x02)
    // -------------------------------------------------------------------------

    [Fact]
    public void InvitationAuthenticate_RoundTrip()
    {
        var digest   = new byte[32];
        for (int i = 0; i < 32; i++) digest[i] = (byte)(i + 0xA0);

        var original = new InvitationAuthenticatePacket(digest, "Client", "SN-99");
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x02, encoded[4]); // command code

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationAuthenticatePacket>(cmds[0]);

        Assert.Equal(digest, parsed.AuthDigest);
        Assert.Equal("Client", parsed.EndpointName);
        Assert.Equal("SN-99", parsed.ProductInstanceId);
    }

    // -------------------------------------------------------------------------
    // PIN authentication helpers — ComputeAuthDigest
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeAuthDigest_Returns32Bytes()
    {
        var nonce = new byte[16];
        var digest = NetworkMidi2Protocol.ComputeAuthDigest("secret", nonce);
        Assert.Equal(32, digest.Length);
    }

    [Fact]
    public void ComputeAuthDigest_SameInputs_SameOutput()
    {
        var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var d1 = NetworkMidi2Protocol.ComputeAuthDigest("pin1234", nonce);
        var d2 = NetworkMidi2Protocol.ComputeAuthDigest("pin1234", nonce);
        Assert.Equal(d1, d2);
    }

    [Fact]
    public void ComputeAuthDigest_DifferentNonce_DifferentOutput()
    {
        var nonce1 = new byte[16];
        var nonce2 = new byte[16];
        nonce2[0] = 1;
        var d1 = NetworkMidi2Protocol.ComputeAuthDigest("pin", nonce1);
        var d2 = NetworkMidi2Protocol.ComputeAuthDigest("pin", nonce2);
        Assert.False(d1.SequenceEqual(d2));
    }

    // -------------------------------------------------------------------------
    // Nak (0x8F)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NakReason.CommandNotSupported)]
    [InlineData(NakReason.CommandNotExpected)]
    [InlineData(NakReason.CommandMalformed)]
    [InlineData(NakReason.BadPingReply)]
    public void Nak_RoundTrip_AllReasons(NakReason reason)
    {
        var encoded = NetworkMidi2Protocol.Encode(new NakPacket(reason));

        Assert.Equal(0x8F, encoded[4]);           // command code
        Assert.Equal(0,    encoded[5]);           // payloadWordLen = 0
        Assert.Equal((byte)reason, encoded[6]);   // CSD1 = reason

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<NakPacket>(cmds[0]);
        Assert.Equal(reason, parsed.Reason);
    }

    // -------------------------------------------------------------------------
    // Retransmit (0x80) / RetransmitError (0x81)
    // -------------------------------------------------------------------------

    [Fact]
    public void Retransmit_RoundTrip_TwoSequenceNumbers()
    {
        var original = new RetransmitPacket([0x0005, 0x0006]);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x80, encoded[4]); // command code

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<RetransmitPacket>(cmds[0]);
        Assert.Equal<ushort>([0x0005, 0x0006], parsed.SequenceNumbers);
    }

    [Fact]
    public void Retransmit_RoundTrip_SingleSequenceNumber()
    {
        var original = new RetransmitPacket([0x1234]);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<RetransmitPacket>(cmds[0]);
        // One seq packed in one word → two 16-bit slots; first = 0x1234, second = 0x0000
        Assert.True(parsed.SequenceNumbers.Length >= 1);
        Assert.Equal((ushort)0x1234, parsed.SequenceNumbers[0]);
    }

    [Fact]
    public void RetransmitError_RoundTrip()
    {
        var original = new RetransmitErrorPacket([0xABCD, 0x1234]);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.Equal(0x81, encoded[4]); // command code

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<RetransmitErrorPacket>(cmds[0]);
        Assert.Equal<ushort>([0xABCD, 0x1234], parsed.SequenceNumbers);
    }

    // -------------------------------------------------------------------------
    // SessionReset (0x82) / SessionResetReply (0x83)
    // -------------------------------------------------------------------------

    [Fact]
    public void SessionReset_RoundTrip()
    {
        var encoded = NetworkMidi2Protocol.Encode(new SessionResetPacket());

        Assert.Equal(0x82, encoded[4]);
        Assert.Equal(0,    encoded[5]); // no payload

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        Assert.IsType<SessionResetPacket>(cmds[0]);
    }

    [Fact]
    public void SessionResetReply_RoundTrip()
    {
        var encoded = NetworkMidi2Protocol.Encode(new SessionResetReplyPacket());

        Assert.Equal(0x83, encoded[4]);
        Assert.Equal(0,    encoded[5]); // no payload

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        Assert.IsType<SessionResetReplyPacket>(cmds[0]);
    }
}
