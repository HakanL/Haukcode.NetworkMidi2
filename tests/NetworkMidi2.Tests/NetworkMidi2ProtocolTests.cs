using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

public class NetworkMidi2ProtocolTests
{
    // -------------------------------------------------------------------------
    // Invitation
    // -------------------------------------------------------------------------

    [Fact]
    public void Invitation_RoundTrip_NoPin()
    {
        var original = new InvitationPacket(0xDEADBEEF, "Test Session", PinHash: null);
        var encoded = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationPacket>(cmds[0]);

        Assert.Equal(0xDEADBEEFu, parsed.InitiatorToken);
        Assert.Equal("Test Session", parsed.LocalName);
        Assert.Null(parsed.PinHash);
    }

    [Fact]
    public void Invitation_RoundTrip_WithPin()
    {
        var pinHash  = NetworkMidi2Protocol.ComputePinHash("secret");
        var original = new InvitationPacket(0x12345678, "Secure Session", pinHash);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationPacket>(cmds[0]);

        Assert.Equal("Secure Session", parsed.LocalName);
        Assert.NotNull(parsed.PinHash);
        Assert.Equal(32, parsed.PinHash.Length);
        Assert.True(NetworkMidi2Protocol.PinHashesEqual(parsed.PinHash, pinHash));
    }

    // -------------------------------------------------------------------------
    // InvitationAccepted
    // -------------------------------------------------------------------------

    [Fact]
    public void InvitationAccepted_RoundTrip()
    {
        var original = new InvitationAcceptedPacket(0xABCD1234, "Bridge");
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationAcceptedPacket>(cmds[0]);

        Assert.Equal(0xABCD1234u, parsed.InitiatorToken);
        Assert.Equal("Bridge", parsed.RemoteName);
    }

    // -------------------------------------------------------------------------
    // InvitationRefused
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(InvitationRefusedReason.Unspecified)]
    [InlineData(InvitationRefusedReason.AuthRequired)]
    [InlineData(InvitationRefusedReason.AuthFailed)]
    [InlineData(InvitationRefusedReason.SessionBusy)]
    public void InvitationRefused_RoundTrip_AllReasons(InvitationRefusedReason reason)
    {
        var original = new InvitationRefusedPacket(0x99887766, reason);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<InvitationRefusedPacket>(cmds[0]);

        Assert.Equal(0x99887766u, parsed.InitiatorToken);
        Assert.Equal(reason, parsed.Reason);
    }

    // -------------------------------------------------------------------------
    // Bye / ByeReply
    // -------------------------------------------------------------------------

    [Fact]
    public void Bye_RoundTrip()
    {
        var original = new ByePacket(0x11223344);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<ByePacket>(cmds[0]);
        Assert.Equal(0x11223344u, parsed.InitiatorToken);
    }

    [Fact]
    public void ByeReply_RoundTrip()
    {
        var original = new ByeReplyPacket(0xAABBCCDD);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<ByeReplyPacket>(cmds[0]);
        Assert.Equal(0xAABBCCDDu, parsed.InitiatorToken);
    }

    // -------------------------------------------------------------------------
    // Ping / PingReply
    // -------------------------------------------------------------------------

    [Fact]
    public void Ping_RoundTrip()
    {
        var original = new PingPacket(0xFEDCBA98);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<PingPacket>(cmds[0]);
        Assert.Equal(0xFEDCBA98u, parsed.PingId);
    }

    [Fact]
    public void PingReply_RoundTrip()
    {
        var original = new PingReplyPacket(0x00112233);
        var encoded  = NetworkMidi2Protocol.Encode(original);

        Assert.True(NetworkMidi2Protocol.TryParseAll(encoded, out var cmds));
        var parsed = Assert.IsType<PingReplyPacket>(cmds[0]);
        Assert.Equal(0x00112233u, parsed.PingId);
    }

    // -------------------------------------------------------------------------
    // Multiple commands in one datagram
    // -------------------------------------------------------------------------

    [Fact]
    public void MultipleCommandsInOneDatagram_AllParsed()
    {
        var ping    = NetworkMidi2Protocol.Encode(new PingPacket(0x11111111));
        var bye     = NetworkMidi2Protocol.Encode(new ByePacket(0x22222222));
        var pingRep = NetworkMidi2Protocol.Encode(new PingReplyPacket(0x33333333));

        var combined = ping.Concat(bye).Concat(pingRep).ToArray();

        Assert.True(NetworkMidi2Protocol.TryParseAll(combined, out var cmds));
        Assert.Equal(3, cmds.Count);

        Assert.IsType<PingPacket>(cmds[0]);
        Assert.IsType<ByePacket>(cmds[1]);
        Assert.IsType<PingReplyPacket>(cmds[2]);
    }

    // -------------------------------------------------------------------------
    // Parse rejection
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsWrongMagic()
    {
        var buf = new byte[12];
        buf[0] = 0x00; // not 'M'
        buf[1] = 0x49;
        buf[2] = 0x44;
        buf[3] = 0x49;
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    [Fact]
    public void Parse_RejectsTooShort()
    {
        var buf = new byte[] { 0x4D, 0x49, 0x44 }; // "MID" — truncated magic
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    [Fact]
    public void Parse_TruncatedPayload_ReturnsFalseForThatCommand()
    {
        // Valid header for a Ping (payload should be 4 bytes) but only 2 payload bytes provided
        var buf = new byte[]
        {
            0x4D, 0x49, 0x44, 0x49,  // "MIDI"
            0x00, 0x06,               // Ping command
            0x00, 0x04,               // declares 4-byte payload
            0x00, 0x01,               // only 2 bytes of payload
        };
        Assert.False(NetworkMidi2Protocol.TryParseAll(buf, out _));
    }

    // -------------------------------------------------------------------------
    // PIN hash
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
