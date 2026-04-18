namespace Haukcode.NetworkMidi2;

/// <summary>
/// Utilities for working with Universal MIDI Packets (UMP, per M2-104-UM).
/// UMP messages are fixed-size groups of 32-bit words; the message type in
/// the top 4 bits of the first word determines how many words follow.
/// </summary>
public static class UmpHelpers
{
    /// <summary>
    /// Returns the number of 32-bit words in a UMP message given the first word.
    /// The message type occupies bits 31–28.
    /// </summary>
    public static int GetWordCount(uint firstWord)
    {
        byte messageType = (byte)(firstWord >> 28);
        return messageType switch
        {
            0x0 or 0x1 or 0x2 or 0x6 or 0x7 => 1,  // 32-bit messages
            0x3 or 0x4                        => 2,  // 64-bit messages
            0xD                               => 2,  // 64-bit (Flex Data)
            0x5 or 0xF                        => 4,  // 128-bit messages
            _                                => 1,   // unknown — treat as 1
        };
    }

    /// <summary>
    /// Reads big-endian uint words from a byte span.
    /// The span length must be a multiple of 4.
    /// </summary>
    public static uint[] ReadWords(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 4;
        var words = new uint[count];
        for (int i = 0; i < count; i++)
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes[(i * 4)..]);
        return words;
    }

    /// <summary>
    /// Writes uint words as big-endian bytes into a destination span.
    /// <paramref name="dest"/> must be at least <c>words.Length * 4</c> bytes.
    /// </summary>
    public static void WriteWords(ReadOnlySpan<uint> words, Span<byte> dest)
    {
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(dest[(i * 4)..], words[i]);
    }

    /// <summary>
    /// Converts a uint[] to a big-endian byte array.
    /// </summary>
    public static byte[] ToBytes(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * 4];
        WriteWords(words, bytes);
        return bytes;
    }

    /// <summary>
    /// Returns true if the word sequence forms valid, complete UMP messages —
    /// i.e. each message occupies exactly its declared word count with no truncation.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<uint> words)
    {
        int i = 0;
        while (i < words.Length)
        {
            int size = GetWordCount(words[i]);
            i += size;
        }
        return i == words.Length;
    }
}
