namespace Haukcode.NetworkMidi2;

/// <summary>
/// Codec for the UMP Data Command payload within a Network MIDI 2.0 datagram.
///
/// Wire layout of the UMP Data Command payload (after the 8-byte session header):
///   4 bytes  SequenceNumber (big-endian uint32)
///   N bytes  FEC block (see <see cref="UmpFec"/>)
/// </summary>
public static class UmpDataCommand
{
    private const int SequenceNumberSize = 4;

    /// <summary>
    /// Encodes the UMP Data Command payload (sequence number + FEC block).
    /// The returned bytes do NOT include the outer session header — use
    /// <see cref="NetworkMidi2Protocol.WrapWithHeader"/> to produce a full datagram.
    /// </summary>
    public static byte[] EncodePayload(uint sequenceNumber, ReadOnlySpan<byte> fecBlock)
    {
        var buf = new byte[SequenceNumberSize + fecBlock.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, sequenceNumber);
        fecBlock.CopyTo(buf.AsSpan(SequenceNumberSize));
        return buf;
    }

    /// <summary>
    /// Parses the payload portion of a UMP Data Command (after the 8-byte session header).
    /// Returns the sequence number and the raw FEC block bytes for further decoding.
    /// </summary>
    public static bool TryParsePayload(
        ReadOnlySpan<byte> payload,
        out uint sequenceNumber,
        out ReadOnlySpan<byte> fecBlock)
    {
        sequenceNumber = 0;
        fecBlock = default;

        if (payload.Length < SequenceNumberSize) return false;

        sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(payload);
        fecBlock = payload[SequenceNumberSize..];
        return true;
    }
}
