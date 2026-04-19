using System.Security.Cryptography;

namespace Haukcode.NetworkMidi2;

/// <summary>
/// Network MIDI 2.0 session command codes (M2-124-UM v1.0).
/// Each is a single byte in the command header.
/// </summary>
public enum NetworkMidi2Command : byte
{
    Invitation                         = 0x01,
    InvitationAuthenticate             = 0x02,
    InvitationUserAuthenticate         = 0x03,
    InvitationAccepted                 = 0x10,
    InvitationPending                  = 0x11,
    InvitationAuthenticationRequired   = 0x12,
    InvitationUserAuthenticationRequired = 0x13,
    Ping                               = 0x20,
    PingReply                          = 0x21,
    Retransmit                         = 0x80,
    RetransmitError                    = 0x81,
    SessionReset                       = 0x82,
    SessionResetReply                  = 0x83,
    Nak                                = 0x8F,
    Bye                                = 0xF0,
    ByeReply                           = 0xF1,
    UmpData                            = 0xFF,
}

/// <summary>
/// Reason codes for Nak (0x8F) commands (M2-124-UM v1.0).
/// </summary>
public enum NakReason : byte
{
    CommandNotSupported = 0x01,
    CommandNotExpected  = 0x02,
    CommandMalformed    = 0x03,
    BadPingReply        = 0x20,
}

/// <summary>
/// Reason codes for Bye (0xF0) commands (M2-124-UM v1.0 §X, Wireshark dissector).
/// </summary>
public enum ByeReason : byte
{
    Reserved                       = 0x00,
    UserTerminated                 = 0x01,
    PowerDown                      = 0x02,
    TooManyMissingPackets          = 0x03,
    Timeout                        = 0x04,
    SessionNotEstablished          = 0x05,
    NoPendingSession               = 0x06,
    ProtocolError                  = 0x07,
    TooManyOpenSessions            = 0x40,
    AuthMissingPriorInvitation     = 0x41,
    UserRejected                   = 0x42,
    AuthFailed                     = 0x43,
    UserNameNotFound               = 0x44,
    NoMatchingAuthMethod           = 0x45,
    InvitationCanceled             = 0x80,
}

// ---------------------------------------------------------------------------
// Packet records — reflect the actual wire fields
// ---------------------------------------------------------------------------

/// <summary>Invitation (0x01): Client → Host, open session request.</summary>
/// <param name="EndpointName">UTF-8 name, word-padded on wire.</param>
/// <param name="ProductInstanceId">ASCII product ID, word-padded on wire.</param>
public record InvitationPacket(string EndpointName, string ProductInstanceId = "");

/// <summary>InvitationAccepted (0x10): Host → Client, session established.</summary>
public record InvitationAcceptedPacket(string EndpointName, string ProductInstanceId = "");

/// <summary>Ping (0x20): liveness check, either direction.</summary>
public record PingPacket(uint PingId);

/// <summary>PingReply (0x21): acknowledgment of Ping.</summary>
public record PingReplyPacket(uint PingId);

/// <summary>Bye (0xF0): end the session, with a reason code in CSD1.</summary>
public record ByePacket(ByeReason Reason);

/// <summary>ByeReply (0xF1): acknowledgment of Bye (no payload).</summary>
public record ByeReplyPacket;

/// <summary>
/// InvitationPending (0x11): Host → Client — session is busy; client should retry later.
/// No payload.
/// </summary>
public record InvitationPendingPacket;

/// <summary>
/// InvitationAuthenticationRequired (0x12): Host → Client — PIN authentication required.
/// CSD1 = endpoint-name word count, CSD2 = auth state (0 = first request, 1 = retry).
/// Payload = 16-byte nonce | endpoint name (word-padded) | product ID (word-padded).
/// </summary>
public record InvitationAuthenticationRequiredPacket(
    byte[] Nonce,
    string EndpointName,
    string ProductInstanceId,
    byte AuthState);

/// <summary>
/// InvitationAuthenticate (0x02): Client → Host — challenge response for PIN auth.
/// CSD1 = endpoint-name word count, CSD2 = 0.
/// Payload = 32-byte HMAC-SHA-256 digest | endpoint name (word-padded) | product ID (word-padded).
/// </summary>
public record InvitationAuthenticatePacket(
    byte[] AuthDigest,
    string EndpointName,
    string ProductInstanceId);

/// <summary>Nak (0x8F): generic negative acknowledgment. CSD1 = reason code.</summary>
public record NakPacket(NakReason Reason);

/// <summary>
/// Retransmit (0x80): receiver → sender — explicit request to resend missing packets.
/// Payload = pairs of 16-bit sequence numbers (big-endian, packed two per 32-bit word).
/// </summary>
public record RetransmitPacket(ushort[] SequenceNumbers);

/// <summary>
/// RetransmitError (0x81): sender → receiver — requested packets are unavailable.
/// Payload = pairs of 16-bit sequence numbers (big-endian, packed two per 32-bit word).
/// </summary>
public record RetransmitErrorPacket(ushort[] SequenceNumbers);

/// <summary>
/// SessionReset (0x82): either side → other side — reset mid-session sequence counters.
/// No payload.
/// </summary>
public record SessionResetPacket;

/// <summary>SessionResetReply (0x83): acknowledgment of SessionReset. No payload.</summary>
public record SessionResetReplyPacket;

/// <summary>
/// A parsed UMP Data command (0xFF).
/// Multiple of these may appear in one datagram (FEC: oldest first, current last).
/// </summary>
internal record UmpDataPacket(ushort SequenceNumber, uint[] UmpWords);

// ---------------------------------------------------------------------------
// Codec
// ---------------------------------------------------------------------------

/// <summary>
/// Codec for the Network MIDI 2.0 session protocol (M2-124-UM v1.0).
/// All methods are static and I/O-free.
///
/// Wire format per UDP datagram:
///   4 bytes  Magic: "MIDI" (0x4D 0x49 0x44 0x49) — exactly once per datagram
///   Then, one or more commands packed back-to-back:
///     Byte 0  Command code (byte)
///     Byte 1  Payload length in 32-bit words (byte)
///     Byte 2  CSD1 — command-specific data 1
///     Byte 3  CSD2 — command-specific data 2
///     N*4     Payload (N = payload-length-in-words)
/// </summary>
public static class NetworkMidi2Protocol
{
    public const int DefaultPort = 5004;

    private static ReadOnlySpan<byte> Magic => "MIDI"u8;
    private const int CommandHeaderSize = 4; // code + payloadWordLen + CSD1 + CSD2

    // -------------------------------------------------------------------------
    // Encode — each method returns a complete UDP datagram (magic + one command)
    // -------------------------------------------------------------------------

    public static byte[] Encode(InvitationPacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.Invitation,
            csd1: (byte)NameWordCount(packet.EndpointName),
            csd2: 0,
            EncodeNamePayload(packet.EndpointName, packet.ProductInstanceId));

    public static byte[] Encode(InvitationAcceptedPacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.InvitationAccepted,
            csd1: (byte)NameWordCount(packet.EndpointName),
            csd2: 0,
            EncodeNamePayload(packet.EndpointName, packet.ProductInstanceId));

    public static byte[] Encode(PingPacket packet)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, packet.PingId);
        return EncodeDatagramSingle(NetworkMidi2Command.Ping, 0, 0, payload);
    }

    public static byte[] Encode(PingReplyPacket packet)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, packet.PingId);
        return EncodeDatagramSingle(NetworkMidi2Command.PingReply, 0, 0, payload);
    }

    public static byte[] Encode(ByePacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.Bye, csd1: (byte)packet.Reason, csd2: 0, []);

    public static byte[] Encode(ByeReplyPacket _)
        => EncodeDatagramSingle(NetworkMidi2Command.ByeReply, 0, 0, []);

    public static byte[] Encode(InvitationPendingPacket _)
        => EncodeDatagramSingle(NetworkMidi2Command.InvitationPending, 0, 0, []);

    public static byte[] Encode(InvitationAuthenticationRequiredPacket packet)
    {
        if (packet.Nonce.Length != 16)
            throw new ArgumentException("Nonce must be exactly 16 bytes.", nameof(packet));

        var namePayload = EncodeNamePayload(packet.EndpointName, packet.ProductInstanceId);
        var payload     = new byte[16 + namePayload.Length];
        packet.Nonce.CopyTo(payload, 0);
        namePayload.CopyTo(payload, 16);

        return EncodeDatagramSingle(
            NetworkMidi2Command.InvitationAuthenticationRequired,
            csd1: (byte)NameWordCount(packet.EndpointName),
            csd2: packet.AuthState,
            payload);
    }

    public static byte[] Encode(InvitationAuthenticatePacket packet)
    {
        if (packet.AuthDigest.Length != 32)
            throw new ArgumentException("AuthDigest must be exactly 32 bytes.", nameof(packet));

        var namePayload = EncodeNamePayload(packet.EndpointName, packet.ProductInstanceId);
        var payload     = new byte[32 + namePayload.Length];
        packet.AuthDigest.CopyTo(payload, 0);
        namePayload.CopyTo(payload, 32);

        return EncodeDatagramSingle(
            NetworkMidi2Command.InvitationAuthenticate,
            csd1: (byte)NameWordCount(packet.EndpointName),
            csd2: 0,
            payload);
    }

    public static byte[] Encode(NakPacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.Nak, csd1: (byte)packet.Reason, csd2: 0, []);

    public static byte[] Encode(RetransmitPacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.Retransmit, 0, 0, EncodeSequenceNumbers(packet.SequenceNumbers));

    public static byte[] Encode(RetransmitErrorPacket packet)
        => EncodeDatagramSingle(NetworkMidi2Command.RetransmitError, 0, 0, EncodeSequenceNumbers(packet.SequenceNumbers));

    public static byte[] Encode(SessionResetPacket _)
        => EncodeDatagramSingle(NetworkMidi2Command.SessionReset, 0, 0, []);

    public static byte[] Encode(SessionResetReplyPacket _)
        => EncodeDatagramSingle(NetworkMidi2Command.SessionResetReply, 0, 0, []);

    // -------------------------------------------------------------------------
    // Multi-command datagrams
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packs the command bodies from several single-command Encode() results into
    /// one datagram, with a single magic prefix.  Used for testing and FEC.
    /// Pass the result of any Encode() overload — the per-datagram magic is stripped
    /// and the bodies are concatenated under one shared magic.
    /// </summary>
    internal static byte[] CombineDatagram(params byte[][] encodedDatagrams)
    {
        int total = 4; // magic
        foreach (var d in encodedDatagrams) total += d.Length - 4;

        var buf = new byte[total];
        Magic.CopyTo(buf);
        int pos = 4;
        foreach (var d in encodedDatagrams)
        {
            var body = d.AsSpan(4);
            body.CopyTo(buf.AsSpan(pos));
            pos += body.Length;
        }
        return buf;
    }

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses all commands packed into a single UDP datagram.
    /// The datagram must begin with the 4-byte "MIDI" magic.
    /// Returns true only when at least one command was successfully parsed.
    /// Stops and returns false on bad magic or a truncated command.
    /// </summary>
    public static bool TryParseAll(ReadOnlySpan<byte> datagram, out IReadOnlyList<object> commands)
    {
        commands = [];
        if (datagram.Length < 4) return false;
        if (!datagram[..4].SequenceEqual(Magic)) return false;

        var result = new List<object>();
        int offset = 4; // skip magic

        while (offset + CommandHeaderSize <= datagram.Length)
        {
            byte code           = datagram[offset];
            byte payloadWords   = datagram[offset + 1];
            byte csd1           = datagram[offset + 2];
            byte csd2           = datagram[offset + 3];
            int  payloadBytes   = payloadWords * 4;

            int cmdEnd = offset + CommandHeaderSize + payloadBytes;
            if (cmdEnd > datagram.Length) return false; // truncated

            var payload = datagram.Slice(offset + CommandHeaderSize, payloadBytes);
            var cmd     = ParseCommand((NetworkMidi2Command)code, csd1, csd2, payload);
            if (cmd != null) result.Add(cmd);

            offset = cmdEnd;
        }

        commands = result;
        return result.Count > 0;
    }

    // -------------------------------------------------------------------------
    // Authentication helpers
    // -------------------------------------------------------------------------

    /// <summary>Computes SHA-256 of the UTF-8 encoded PIN string.</summary>
    public static byte[] ComputePinHash(string pin)
        => SHA256.HashData(Encoding.UTF8.GetBytes(pin));

    /// <summary>
    /// Computes the challenge-response digest for PIN authentication.
    /// Returns HMAC-SHA-256 with the PIN hash as key and the 16-byte nonce as data.
    /// </summary>
    public static byte[] ComputeAuthDigest(string pin, ReadOnlySpan<byte> nonce)
    {
        var key = ComputePinHash(pin);
        return HMACSHA256.HashData(key, nonce);
    }

    /// <summary>Constant-time comparison of two byte spans.</summary>
    public static bool PinHashesEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => CryptographicOperations.FixedTimeEquals(a, b);

    // -------------------------------------------------------------------------
    // ID generation
    // -------------------------------------------------------------------------

    public static uint GeneratePingId()
        => (uint)Random.Shared.NextInt64(0, uint.MaxValue);

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static int NameWordCount(string name)
        => (Encoding.UTF8.GetByteCount(name) + 3) / 4;

    private static byte[] EncodeNamePayload(string name, string productId)
    {
        var nameBytes    = Encoding.UTF8.GetBytes(name);
        var productBytes = Encoding.ASCII.GetBytes(productId);

        int nameWords    = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int totalBytes   = (nameWords + productWords) * 4;

        var buf = new byte[totalBytes]; // zero-initialized → natural null-padding
        nameBytes.CopyTo(buf, 0);
        productBytes.CopyTo(buf, nameWords * 4);
        return buf;
    }

    private static byte[] EncodeDatagramSingle(
        NetworkMidi2Command code, byte csd1, byte csd2, byte[] payload)
    {
        int payloadWords = payload.Length / 4;
        var buf = new byte[4 + CommandHeaderSize + payload.Length];
        Magic.CopyTo(buf);
        buf[4] = (byte)code;
        buf[5] = (byte)payloadWords;
        buf[6] = csd1;
        buf[7] = csd2;
        payload.CopyTo(buf, 8);
        return buf;
    }

    private static object? ParseCommand(
        NetworkMidi2Command code, byte csd1, byte csd2, ReadOnlySpan<byte> payload)
        => code switch
        {
            NetworkMidi2Command.Invitation         => ParseNamePacket(csd1, payload,
                (n, p) => new InvitationPacket(n, p)),
            NetworkMidi2Command.InvitationAuthenticate => ParseAuthenticatePacket(csd1, payload),
            NetworkMidi2Command.InvitationAccepted => ParseNamePacket(csd1, payload,
                (n, p) => new InvitationAcceptedPacket(n, p)),
            NetworkMidi2Command.InvitationPending  => new InvitationPendingPacket(),
            NetworkMidi2Command.InvitationAuthenticationRequired => ParseAuthRequiredPacket(csd1, csd2, payload),
            NetworkMidi2Command.Ping               => ParsePingId(payload,
                id => new PingPacket(id)),
            NetworkMidi2Command.PingReply          => ParsePingId(payload,
                id => new PingReplyPacket(id)),
            NetworkMidi2Command.Retransmit         => new RetransmitPacket(ParseSequenceNumbers(payload)),
            NetworkMidi2Command.RetransmitError    => new RetransmitErrorPacket(ParseSequenceNumbers(payload)),
            NetworkMidi2Command.SessionReset       => new SessionResetPacket(),
            NetworkMidi2Command.SessionResetReply  => new SessionResetReplyPacket(),
            NetworkMidi2Command.Nak                => new NakPacket((NakReason)csd1),
            NetworkMidi2Command.Bye                => new ByePacket((ByeReason)csd1),
            NetworkMidi2Command.ByeReply           => new ByeReplyPacket(),
            NetworkMidi2Command.UmpData            => ParseUmpData(csd1, csd2, payload),
            _                                      => null, // unknown — skip
        };

    private static T? ParseNamePacket<T>(
        byte nameWordCount, ReadOnlySpan<byte> payload, Func<string, string, T> create)
        where T : class
    {
        int nameBytes = nameWordCount * 4;
        if (payload.Length < nameBytes) return null;

        string name = DecodeWordPaddedString(payload[..nameBytes], Encoding.UTF8);
        string prod = DecodeWordPaddedString(payload[nameBytes..], Encoding.ASCII);
        return create(name, prod);
    }

    private static T? ParsePingId<T>(ReadOnlySpan<byte> payload, Func<uint, T> create)
        where T : class
    {
        if (payload.Length < 4) return null;
        return create(BinaryPrimitives.ReadUInt32BigEndian(payload));
    }

    private static UmpDataPacket ParseUmpData(byte csd1, byte csd2, ReadOnlySpan<byte> payload)
    {
        ushort seqNum   = (ushort)((csd1 << 8) | csd2);
        uint[] umpWords = UmpHelpers.ReadWords(payload);
        return new UmpDataPacket(seqNum, umpWords);
    }

    /// <summary>Decodes a word-padded byte field, stripping trailing null bytes.</summary>
    private static string DecodeWordPaddedString(ReadOnlySpan<byte> data, Encoding enc)
    {
        // Trim trailing null padding
        int len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        return enc.GetString(data[..len]);
    }

    private static InvitationAuthenticatePacket? ParseAuthenticatePacket(
        byte nameWordCount, ReadOnlySpan<byte> payload)
    {
        // Payload = [32-byte digest][name (word-padded)][product ID]
        if (payload.Length < 32) return null;
        var digest = payload[..32].ToArray();
        int nameBytes = nameWordCount * 4;
        if (payload.Length < 32 + nameBytes) return null;
        string name = DecodeWordPaddedString(payload.Slice(32, nameBytes), Encoding.UTF8);
        string prod = DecodeWordPaddedString(payload[(32 + nameBytes)..], Encoding.ASCII);
        return new InvitationAuthenticatePacket(digest, name, prod);
    }

    private static InvitationAuthenticationRequiredPacket? ParseAuthRequiredPacket(
        byte nameWordCount, byte authState, ReadOnlySpan<byte> payload)
    {
        // Payload = [16-byte nonce][name (word-padded)][product ID]
        if (payload.Length < 16) return null;
        var nonce = payload[..16].ToArray();
        int nameBytes = nameWordCount * 4;
        if (payload.Length < 16 + nameBytes) return null;
        string name = DecodeWordPaddedString(payload.Slice(16, nameBytes), Encoding.UTF8);
        string prod = DecodeWordPaddedString(payload[(16 + nameBytes)..], Encoding.ASCII);
        return new InvitationAuthenticationRequiredPacket(nonce, name, prod, authState);
    }

    /// <summary>
    /// Encodes a list of 16-bit sequence numbers into a byte array,
    /// packing two numbers per 32-bit word, big-endian.
    /// </summary>
    private static byte[] EncodeSequenceNumbers(ushort[] seqNums)
    {
        // Round up to even count (pad with 0 if needed), then pack 2 per word
        int words = (seqNums.Length + 1) / 2;
        var buf   = new byte[words * 4];
        for (int i = 0; i < seqNums.Length; i++)
        {
            int byteOffset = i * 2;
            buf[byteOffset]     = (byte)(seqNums[i] >> 8);
            buf[byteOffset + 1] = (byte)(seqNums[i] & 0xFF);
        }
        return buf;
    }

    /// <summary>
    /// Parses a list of 16-bit sequence numbers packed two per 32-bit word (big-endian).
    /// </summary>
    private static ushort[] ParseSequenceNumbers(ReadOnlySpan<byte> payload)
    {
        int count = payload.Length / 2;
        var result = new ushort[count];
        for (int i = 0; i < count; i++)
            result[i] = (ushort)((payload[i * 2] << 8) | payload[i * 2 + 1]);
        return result;
    }
}
