namespace Haukcode.NetworkMidi2;

/// <summary>
/// Codec for a single UMP Data command (0xFF) without the datagram magic prefix.
///
/// Wire layout:
///   Byte 0   Command code = 0xFF
///   Byte 1   Payload length in 32-bit words (uint8)
///   Byte 2   Sequence number high byte (CSD1)
///   Byte 3   Sequence number low byte  (CSD2)
///   N*4      UMP words in big-endian byte order
///
/// Used internally by <see cref="UmpFec"/> to build and parse FEC datagrams.
/// </summary>
internal static class UmpDataCommand
{
    internal const byte CommandCode = 0xFF;
    internal const int HeaderSize   = 4;

    /// <summary>
    /// Encodes a single UMP Data command (header + UMP payload).
    /// Does NOT include the 4-byte datagram magic.
    /// </summary>
    public static byte[] Encode(ushort sequenceNumber, ReadOnlySpan<uint> umpWords)
    {
        var buf = new byte[HeaderSize + umpWords.Length * 4];
        buf[0] = CommandCode;
        buf[1] = (byte)umpWords.Length;
        buf[2] = (byte)(sequenceNumber >> 8);  // CSD1 = high byte
        buf[3] = (byte)(sequenceNumber & 0xFF); // CSD2 = low byte
        UmpHelpers.WriteWords(umpWords, buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>
    /// Decodes a single UMP Data command from the beginning of <paramref name="data"/>.
    /// Returns false if the data is truncated or the command code is not 0xFF.
    /// <paramref name="consumed"/> is set to the total bytes consumed (header + payload).
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        out ushort sequenceNumber,
        out uint[] umpWords,
        out int consumed)
    {
        sequenceNumber = 0;
        umpWords       = [];
        consumed       = 0;

        if (data.Length < HeaderSize) return false;
        if (data[0] != CommandCode) return false;

        int wordCount    = data[1];
        int payloadBytes = wordCount * 4;

        if (data.Length < HeaderSize + payloadBytes) return false;

        sequenceNumber = (ushort)((data[2] << 8) | data[3]);
        umpWords       = UmpHelpers.ReadWords(data.Slice(HeaderSize, payloadBytes));
        consumed       = HeaderSize + payloadBytes;
        return true;
    }
}
