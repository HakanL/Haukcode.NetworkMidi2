namespace Haukcode.NetworkMidi2;

/// <summary>
/// Forward Error Correction for Network MIDI 2.0 (M2-124-UM v1.0).
///
/// FEC is implemented by packing multiple UMP Data commands (0xFF) into a single
/// UDP datagram, oldest historical entry first, current entry last:
///
///   [magic:4]
///   [0xFF][wordCount_N-2][seqHi_N-2][seqLo_N-2][ump_words_N-2...]   ← oldest history
///   [0xFF][wordCount_N-1][seqHi_N-1][seqLo_N-1][ump_words_N-1...]   ← newer history
///   [0xFF][wordCount_N  ][seqHi_N  ][seqLo_N  ][ump_words_N  ...]   ← current
///
/// A receiver that missed a historical packet can recover it from the FEC data
/// in the next datagram without requiring retransmission.
/// </summary>
internal static class UmpFec
{
    private static ReadOnlySpan<byte> Magic => "MIDI"u8;
    private const int MaxHistory = 2;

    // -------------------------------------------------------------------------
    // Send-side history
    // -------------------------------------------------------------------------

    public sealed class SendHistory
    {
        private readonly record struct Entry(ushort SequenceNumber, uint[] Words);
        private readonly Queue<Entry> queue = new(capacity: MaxHistory + 1);

        public void Record(ushort sequenceNumber, uint[] umpWords)
        {
            if (queue.Count >= MaxHistory) queue.Dequeue();
            queue.Enqueue(new Entry(sequenceNumber, umpWords));
        }

        /// <summary>Returns history entries oldest-first, at most <see cref="MaxHistory"/> entries.</summary>
        public IReadOnlyList<(ushort SeqNum, uint[] UmpWords)> GetHistory()
            => queue.Select(e => (e.SequenceNumber, e.Words)).ToList();

        /// <summary>
        /// Tries to find a recorded entry by sequence number.
        /// Returns true and sets <paramref name="umpWords"/> if found; false otherwise.
        /// </summary>
        public bool TryGet(ushort sequenceNumber, out uint[] umpWords)
        {
            foreach (var entry in queue)
            {
                if (entry.SequenceNumber == sequenceNumber)
                {
                    umpWords = entry.Words;
                    return true;
                }
            }
            umpWords = [];
            return false;
        }

        /// <summary>Clears all recorded history entries.</summary>
        public void Clear() => queue.Clear();
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a complete UDP datagram with FEC packing.
    /// Up to <see cref="MaxHistory"/> history entries are prepended (oldest first)
    /// before the current UMP Data command.
    /// </summary>
    public static byte[] EncodeDatagram(
        ushort sequenceNumber,
        ReadOnlySpan<uint> umpWords,
        IReadOnlyList<(ushort SeqNum, uint[] UmpWords)> history)
    {
        int histCount = Math.Min(history.Count, MaxHistory);

        // Calculate total size: magic + historical commands + current command
        int totalSize = 4; // magic
        for (int i = 0; i < histCount; i++)
            totalSize += UmpDataCommand.HeaderSize + history[i].UmpWords.Length * 4;
        totalSize += UmpDataCommand.HeaderSize + umpWords.Length * 4;

        var buf = new byte[totalSize];
        Magic.CopyTo(buf);
        int pos = 4;

        // Prepend historical commands (oldest first)
        for (int i = 0; i < histCount; i++)
        {
            var encoded = UmpDataCommand.Encode(history[i].SeqNum, history[i].UmpWords);
            encoded.CopyTo(buf.AsSpan(pos));
            pos += encoded.Length;
        }

        // Append current command
        var current = UmpDataCommand.Encode(sequenceNumber, umpWords);
        current.CopyTo(buf.AsSpan(pos));

        return buf;
    }
}
