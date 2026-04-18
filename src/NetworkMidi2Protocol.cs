using System.Security.Cryptography;

namespace Haukcode.NetworkMidi2;

/// <summary>
/// Network MIDI 2.0 session command types (M2-124-UM v1.0).
/// </summary>
public enum NetworkMidi2Command : ushort
{
    Invitation          = 0x0001,
    InvitationAccepted  = 0x0002,
    InvitationRefused   = 0x0003,
    Bye                 = 0x0004,
    ByeReply            = 0x0005,
    Ping                = 0x0006,
    PingReply           = 0x0007,
    UmpData             = 0x0008,
}

/// <summary>Reason codes returned in an InvitationRefused response.</summary>
public enum InvitationRefusedReason : byte
{
    Unspecified  = 0x00,
    AuthRequired = 0x01,
    AuthFailed   = 0x02,
    SessionBusy  = 0x03,
}

// ---------------------------------------------------------------------------
// Packet records
// ---------------------------------------------------------------------------

/// <summary>Invitation (Client → Host): start a session, optionally with PIN auth.</summary>
public record InvitationPacket(uint InitiatorToken, string LocalName, byte[]? PinHash);

/// <summary>InvitationAccepted (Host → Client): session established.</summary>
public record InvitationAcceptedPacket(uint InitiatorToken, string RemoteName);

/// <summary>InvitationRefused (Host → Client): session rejected.</summary>
public record InvitationRefusedPacket(uint InitiatorToken, InvitationRefusedReason Reason);

/// <summary>Bye (either direction): ends the session.</summary>
public record ByePacket(uint InitiatorToken);

/// <summary>ByeReply: acknowledgment of Bye.</summary>
public record ByeReplyPacket(uint InitiatorToken);

/// <summary>Ping (liveness check, either direction).</summary>
public record PingPacket(uint PingId);

/// <summary>PingReply: acknowledgment of Ping.</summary>
public record PingReplyPacket(uint PingId);

// ---------------------------------------------------------------------------
// Codec
// ---------------------------------------------------------------------------

/// <summary>
/// Codec for the Network MIDI 2.0 session protocol (M2-124-UM v1.0).
/// All methods are static and I/O-free.
///
/// Wire format per datagram:
///   4 bytes  Magic: "MIDI" (0x4D 0x49 0x44 0x49)
///   2 bytes  Command type (big-endian ushort)
///   2 bytes  Payload length in bytes (big-endian ushort)
///   N bytes  Payload
///
/// Multiple commands may be packed back-to-back into a single UDP datagram.
/// </summary>
public static class NetworkMidi2Protocol
{
    public const int DefaultPort = 5004;

    private static ReadOnlySpan<byte> Magic => "MIDI"u8;
    private const int HeaderSize = 8; // magic(4) + cmd(2) + len(2)

    // Auth type byte values in Invitation payload
    private const byte AuthTypeNone = 0x00;
    private const byte AuthTypePin  = 0x01;

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    public static byte[] Encode(InvitationPacket packet)
    {
        var nameBytes = Encoding.UTF8.GetBytes(packet.LocalName);
        bool hasPin = packet.PinHash is { Length: 32 };

        // Payload: token(4) + authType(1) + name(N) + NUL(1) [+ pinHash(32)]
        int payloadLen = 4 + 1 + nameBytes.Length + 1 + (hasPin ? 32 : 0);
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.Invitation, payloadLen);

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.InitiatorToken); pos += 4;
        buf[pos++] = hasPin ? AuthTypePin : AuthTypeNone;
        nameBytes.CopyTo(buf, pos); pos += nameBytes.Length;
        buf[pos++] = 0; // NUL terminator
        if (hasPin)
            packet.PinHash!.CopyTo(buf, pos);

        return buf;
    }

    public static byte[] Encode(InvitationAcceptedPacket packet)
    {
        var nameBytes = Encoding.UTF8.GetBytes(packet.RemoteName);
        // Payload: token(4) + name(N) + NUL(1)
        int payloadLen = 4 + nameBytes.Length + 1;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.InvitationAccepted, payloadLen);

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.InitiatorToken); pos += 4;
        nameBytes.CopyTo(buf, pos); pos += nameBytes.Length;
        buf[pos] = 0;

        return buf;
    }

    public static byte[] Encode(InvitationRefusedPacket packet)
    {
        // Payload: token(4) + reason(1)
        const int payloadLen = 5;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.InvitationRefused, payloadLen);

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.InitiatorToken); pos += 4;
        buf[pos] = (byte)packet.Reason;

        return buf;
    }

    public static byte[] Encode(ByePacket packet)
    {
        const int payloadLen = 4;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.Bye, payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.InitiatorToken);
        return buf;
    }

    public static byte[] Encode(ByeReplyPacket packet)
    {
        const int payloadLen = 4;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.ByeReply, payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.InitiatorToken);
        return buf;
    }

    public static byte[] Encode(PingPacket packet)
    {
        const int payloadLen = 4;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.Ping, payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.PingId);
        return buf;
    }

    public static byte[] Encode(PingReplyPacket packet)
    {
        const int payloadLen = 4;
        var buf = new byte[HeaderSize + payloadLen];
        int pos = WriteHeader(buf, NetworkMidi2Command.PingReply, payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), packet.PingId);
        return buf;
    }

    /// <summary>
    /// Prepends the 8-byte session header to an already-encoded payload.
    /// Used by UmpDataCommand encoding.
    /// </summary>
    internal static byte[] WrapWithHeader(NetworkMidi2Command cmd, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, cmd, payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses all commands packed into a single UDP datagram.
    /// Unknown or malformed commands are skipped. Stops at the first truncated header.
    /// Returns the successfully parsed commands.
    /// </summary>
    public static bool TryParseAll(ReadOnlySpan<byte> datagram, out IReadOnlyList<object> commands)
    {
        var result = new List<object>();
        int offset = 0;

        while (offset + HeaderSize <= datagram.Length)
        {
            if (!TryParseOne(datagram[offset..], out var cmd, out int consumed))
                break; // truncated or bad magic — stop

            if (cmd != null)
                result.Add(cmd);

            offset += consumed;
        }

        commands = result;
        return result.Count > 0;
    }

    /// <summary>
    /// Parses a single command from the start of <paramref name="data"/>.
    /// Sets <paramref name="consumed"/> to the total bytes consumed (header + payload).
    /// Returns false only on bad magic or truncation; unknown commands return true with null.
    /// </summary>
    public static bool TryParseOne(ReadOnlySpan<byte> data, out object? command, out int consumed)
    {
        command = null;
        consumed = 0;

        if (data.Length < HeaderSize) return false;
        if (!data[..4].SequenceEqual(Magic)) return false;

        var cmd = (NetworkMidi2Command)BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int payloadLen = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);

        if (data.Length < HeaderSize + payloadLen) return false;

        consumed = HeaderSize + payloadLen;
        var payload = data.Slice(HeaderSize, payloadLen);

        command = cmd switch
        {
            NetworkMidi2Command.Invitation         => ParseInvitation(payload),
            NetworkMidi2Command.InvitationAccepted => ParseInvitationAccepted(payload),
            NetworkMidi2Command.InvitationRefused  => ParseInvitationRefused(payload),
            NetworkMidi2Command.Bye                => ParseTokenOnly(payload, t => new ByePacket(t)),
            NetworkMidi2Command.ByeReply           => ParseTokenOnly(payload, t => new ByeReplyPacket(t)),
            NetworkMidi2Command.Ping               => ParseId(payload, id => new PingPacket(id)),
            NetworkMidi2Command.PingReply          => ParseId(payload, id => new PingReplyPacket(id)),
            NetworkMidi2Command.UmpData            => new UmpDataRawPayload(payload.ToArray()),
            _                                      => null, // unknown command, skip
        };

        return true;
    }

    // -------------------------------------------------------------------------
    // Authentication helpers
    // -------------------------------------------------------------------------

    /// <summary>Computes SHA-256 of the UTF-8 encoded PIN string.</summary>
    public static byte[] ComputePinHash(string pin)
        => SHA256.HashData(Encoding.UTF8.GetBytes(pin));

    /// <summary>Constant-time comparison of two byte arrays.</summary>
    public static bool PinHashesEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => CryptographicOperations.FixedTimeEquals(a, b);

    // -------------------------------------------------------------------------
    // Token / ID generation
    // -------------------------------------------------------------------------

    public static uint GenerateInitiatorToken()
        => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    public static uint GeneratePingId()
        => (uint)Random.Shared.NextInt64(0, uint.MaxValue);

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static int WriteHeader(byte[] buf, NetworkMidi2Command cmd, int payloadLen)
    {
        Magic.CopyTo(buf.AsSpan(0));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)cmd);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), (ushort)payloadLen);
        return HeaderSize;
    }

    private static InvitationPacket? ParseInvitation(ReadOnlySpan<byte> payload)
    {
        // token(4) + authType(1) + name(N) + NUL(1) [+ pinHash(32)]
        if (payload.Length < 6) return null;

        var token    = BinaryPrimitives.ReadUInt32BigEndian(payload);
        byte authType = payload[4];
        var nameSpan  = payload[5..];

        int nullIdx = nameSpan.IndexOf((byte)0);
        if (nullIdx < 0) return null;
        var name = Encoding.UTF8.GetString(nameSpan[..nullIdx]);

        byte[]? pinHash = null;
        if (authType == AuthTypePin)
        {
            int afterNull = 5 + nullIdx + 1;
            if (payload.Length >= afterNull + 32)
                pinHash = payload.Slice(afterNull, 32).ToArray();
        }

        return new InvitationPacket(token, name, pinHash);
    }

    private static InvitationAcceptedPacket? ParseInvitationAccepted(ReadOnlySpan<byte> payload)
    {
        // token(4) + name(N) + NUL(1)
        if (payload.Length < 5) return null;

        var token    = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var nameSpan = payload[4..];
        int nullIdx  = nameSpan.IndexOf((byte)0);
        var name     = Encoding.UTF8.GetString(nullIdx >= 0 ? nameSpan[..nullIdx] : nameSpan);

        return new InvitationAcceptedPacket(token, name);
    }

    private static InvitationRefusedPacket? ParseInvitationRefused(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5) return null;
        var token  = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var reason = (InvitationRefusedReason)payload[4];
        return new InvitationRefusedPacket(token, reason);
    }

    private static T? ParseTokenOnly<T>(ReadOnlySpan<byte> payload, Func<uint, T> create) where T : class
    {
        if (payload.Length < 4) return null;
        return create(BinaryPrimitives.ReadUInt32BigEndian(payload));
    }

    private static T? ParseId<T>(ReadOnlySpan<byte> payload, Func<uint, T> create) where T : class
    {
        if (payload.Length < 4) return null;
        return create(BinaryPrimitives.ReadUInt32BigEndian(payload));
    }
}

/// <summary>Raw UMP data payload bytes before FEC decode. Internal use only.</summary>
internal sealed record UmpDataRawPayload(byte[] Bytes);
