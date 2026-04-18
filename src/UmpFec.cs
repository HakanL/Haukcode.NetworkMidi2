namespace Haukcode.NetworkMidi2;

/// <summary>
/// Encoder and decoder for the Network MIDI 2.0 FEC (Forward Error Correction) mechanism
/// described in M2-124-UM.
///
/// Each outgoing UMP Data Command carries the current UMP payload plus up to 2 previously
/// transmitted UMP Data Commands, allowing receivers to reconstruct dropped packets without
/// retransmission.
///
/// FEC block wire layout used here:
///   1 byte   FecCount (0–2): number of historical entries that follow
///   2 bytes  Primary byte count (big-endian ushort)
///   N bytes  Primary UMP words (big-endian uint32 each)
///   [FecCount repetitions of]:
///     4 bytes  Historical SequenceNumber (big-endian uint32)
///     2 bytes  Historical byte count (big-endian ushort)
///     N bytes  Historical UMP words (big-endian uint32 each)
///
/// NOTE: The M2-124-UM v1.0 specification defines the normative wire layout. This
/// implementation uses explicit length fields to avoid parsing ambiguity. Verify the
/// exact layout against the spec PDF before interoperability testing.
/// </summary>
internal static class UmpFec
{
    private const int FecCountSize   = 1;
    private const int LengthSize     = 2; // ushort
    private const int SeqNumSize     = 4;

    // -------------------------------------------------------------------------
    // Send-side history
    // -------------------------------------------------------------------------

    public sealed class SendHistory
    {
        private readonly record struct Entry(uint SequenceNumber, uint[] Words);
        private readonly Queue<Entry> queue = new(capacity: 3);
        private const int MaxHistory = 2;

        public void Record(uint sequenceNumber, uint[] umpWords)
        {
            if (queue.Count >= MaxHistory) queue.Dequeue();
            queue.Enqueue(new Entry(sequenceNumber, umpWords));
        }

        public IReadOnlyList<(uint SeqNum, uint[] Words)> GetHistory()
            => queue.Select(e => (e.SequenceNumber, e.Words)).ToList();
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <summary>Builds the FEC block for an outgoing UMP Data Command.</summary>
    public static byte[] Encode(
        ReadOnlySpan<uint> primaryWords,
        IReadOnlyList<(uint SeqNum, uint[] Words)> history)
    {
        int fecCount     = Math.Min(history.Count, 2);
        int primaryBytes = primaryWords.Length * 4;

        int totalSize = FecCountSize + LengthSize + primaryBytes;
        foreach (var (_, words) in history.Take(fecCount))
            totalSize += SeqNumSize + LengthSize + words.Length * 4;

        var buf = new byte[totalSize];
        int pos = 0;

        buf[pos++] = (byte)fecCount;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos), (ushort)primaryBytes); pos += 2;
        UmpHelpers.WriteWords(primaryWords, buf.AsSpan(pos)); pos += primaryBytes;

        foreach (var (seqNum, words) in history.Take(fecCount))
        {
            int histBytes = words.Length * 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), seqNum);        pos += 4;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos), (ushort)histBytes); pos += 2;
            UmpHelpers.WriteWords(words, buf.AsSpan(pos));                          pos += histBytes;
        }

        return buf;
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    public sealed class RecoveredCommand
    {
        public uint SequenceNumber { get; init; }
        public uint[] UmpWords { get; init; } = [];
    }

    /// <summary>
    /// Decodes a FEC block from a received UMP Data Command payload.
    /// On success returns primary words and any embedded historical commands.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> fecBlock,
        out uint[]? primaryWords,
        out IReadOnlyList<RecoveredCommand> historicalCommands)
    {
        primaryWords = null;
        historicalCommands = [];

        int pos = 0;
        if (fecBlock.Length < FecCountSize + LengthSize) return false;

        int fecCount = fecBlock[pos++];
        if (fecCount > 2) return false;

        int primaryBytes = BinaryPrimitives.ReadUInt16BigEndian(fecBlock[pos..]); pos += 2;
        if (pos + primaryBytes > fecBlock.Length) return false;

        primaryWords = UmpHelpers.ReadWords(fecBlock.Slice(pos, primaryBytes));
        pos += primaryBytes;

        var historical = new List<RecoveredCommand>(fecCount);
        for (int h = 0; h < fecCount; h++)
        {
            if (pos + SeqNumSize + LengthSize > fecBlock.Length) break;

            uint seqNum   = BinaryPrimitives.ReadUInt32BigEndian(fecBlock[pos..]); pos += 4;
            int histBytes = BinaryPrimitives.ReadUInt16BigEndian(fecBlock[pos..]); pos += 2;

            if (pos + histBytes > fecBlock.Length) break;

            historical.Add(new RecoveredCommand
            {
                SequenceNumber = seqNum,
                UmpWords = UmpHelpers.ReadWords(fecBlock.Slice(pos, histBytes)),
            });
            pos += histBytes;
        }

        historicalCommands = historical;
        return true;
    }
}
