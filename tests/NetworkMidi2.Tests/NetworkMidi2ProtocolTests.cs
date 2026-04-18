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
}
