using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

/// <summary>
/// Tests for UmpFec — Forward Error Correction via datagram packing.
///
/// FEC packs multiple UMP Data commands (0xFF) in one UDP datagram:
///   [magic:4] [hist0:4+N] [hist1:4+N] [current:4+N]
/// Oldest history first, newest (current) last.
/// Receivers use the historical entries to recover dropped packets.
/// </summary>
public class UmpFecTests
{
    private static uint[] Words(params uint[] w) => w;

    // -------------------------------------------------------------------------
    // EncodeDatagram — structure
    // -------------------------------------------------------------------------

    [Fact]
    public void EncodeDatagram_StartsWithMidiMagic()
    {
        var datagram = UmpFec.EncodeDatagram(0, [0x20000000u], []);
        Assert.Equal((byte)'M', datagram[0]);
        Assert.Equal((byte)'I', datagram[1]);
        Assert.Equal((byte)'D', datagram[2]);
        Assert.Equal((byte)'I', datagram[3]);
    }

    [Fact]
    public void EncodeDatagram_NoHistory_OneUmpDataCommand()
    {
        var umpWords = Words(0x20906040u);
        var datagram = UmpFec.EncodeDatagram(10, umpWords, []);

        // magic(4) + header(4) + 1 word(4) = 12 bytes
        Assert.Equal(12, datagram.Length);

        // Only one command: current, at offset 4
        Assert.Equal(0xFF, datagram[4]);  // code = UMP Data
        Assert.Equal(1,    datagram[5]);  // wordCount = 1
        Assert.Equal(0x00, datagram[6]);  // seqHi = 0
        Assert.Equal(0x0A, datagram[7]);  // seqLo = 10
    }

    [Fact]
    public void EncodeDatagram_OneHistory_TwoUmpDataCommands()
    {
        var histWords = Words(0x20804000u);
        var curWords  = Words(0x20906040u);
        var history   = new List<(ushort, uint[])> { ((ushort)9, histWords) };

        var datagram = UmpFec.EncodeDatagram(10, curWords, history);

        // magic(4) + [hist: header(4) + 1 word(4)] + [cur: header(4) + 1 word(4)] = 4+8+8=20
        Assert.Equal(20, datagram.Length);

        // First command = history (seq=9)
        Assert.Equal(0xFF, datagram[4]);
        Assert.Equal(1,    datagram[5]);  // wordCount
        Assert.Equal(0x00, datagram[6]);  // seqHi
        Assert.Equal(0x09, datagram[7]);  // seqLo = 9

        // Second command = current (seq=10)
        Assert.Equal(0xFF, datagram[12]);
        Assert.Equal(0x00, datagram[14]); // seqHi
        Assert.Equal(0x0A, datagram[15]); // seqLo = 10
    }

    [Fact]
    public void EncodeDatagram_MoreThanTwoHistory_ClampsToTwo()
    {
        var history = new List<(ushort, uint[])>
        {
            (1, Words(0x20000001u)),
            (2, Words(0x20000002u)),
            (3, Words(0x20000003u)), // should be ignored (exceeds MaxHistory=2)
        };
        var datagram = UmpFec.EncodeDatagram(4, Words(0x20000004u), history);

        // Only 2 history entries + 1 current = 3 UMP Data commands
        // magic(4) + 3*(header(4)+word(4)) = 4 + 3*8 = 28
        Assert.Equal(28, datagram.Length);
    }

    // -------------------------------------------------------------------------
    // EncodeDatagram → TryParseAll round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void EncodeDatagram_ThenParseAll_NoHistory()
    {
        var umpWords = Words(0xDEADBEEFu);
        var datagram = UmpFec.EncodeDatagram(42, umpWords, []);

        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram, out var cmds));
        Assert.Single(cmds);

        Assert.True(UmpDataCommand.TryDecode(datagram[4..], out ushort seq, out uint[] words, out _));
        Assert.Equal((ushort)42, seq);
        Assert.Equal(umpWords, words);
    }

    [Fact]
    public void EncodeDatagram_ThenParseAll_TwoHistory_ThreeCommands()
    {
        var words8 = Words(0x20000008u);
        var words9 = Words(0x20000009u);
        var words10 = Words(0x2000000Au);

        var history = new List<(ushort, uint[])>
        {
            (8, words8),
            (9, words9),
        };
        var datagram = UmpFec.EncodeDatagram(10, words10, history);

        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram, out var cmds));
        Assert.Equal(3, cmds.Count); // 2 historical + 1 current
    }

    // -------------------------------------------------------------------------
    // Packet loss recovery simulation via FEC
    // -------------------------------------------------------------------------

    [Fact]
    public void PacketLossRecovery_OneDropped_RecoveredFromFec()
    {
        var words10 = Words(0x20906040u);
        var words11 = Words(0x20804000u);

        // Packet 10 sent without history
        // Packet 11 sent with history [seq=10]

        var history11 = new List<(ushort, uint[])> { (10, words10) };
        var datagram11 = UmpFec.EncodeDatagram(11, words11, history11);

        // Simulate: packet 10 was LOST; packet 11 arrives
        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram11, out var cmds));
        Assert.Equal(2, cmds.Count); // hist[10] + current[11]

        // First command = recovered packet 10
        Assert.True(UmpDataCommand.TryDecode(datagram11[4..], out ushort seq10, out uint[] recovered10, out int consumed));
        Assert.Equal((ushort)10, seq10);
        Assert.Equal(words10, recovered10);

        // Second command = current packet 11
        Assert.True(UmpDataCommand.TryDecode(datagram11[(4 + consumed)..], out ushort seq11, out uint[] current11, out _));
        Assert.Equal((ushort)11, seq11);
        Assert.Equal(words11, current11);
    }

    [Fact]
    public void PacketLossRecovery_TwoDropped_BothRecoveredFromNextPacket()
    {
        var words10 = Words(0x20906040u);
        var words11 = Words(0x20804000u);
        var words12 = Words(0x20904020u);

        var history12 = new List<(ushort, uint[])>
        {
            (10, words10),
            (11, words11),
        };
        var datagram12 = UmpFec.EncodeDatagram(12, words12, history12);

        // Parse all — should yield 3 UMP Data commands
        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram12, out var cmds));
        Assert.Equal(3, cmds.Count);

        // Verify all three sequence numbers and words
        int pos = 4;
        ushort[] expectedSeqs = [10, 11, 12];
        uint[][] expectedWords = [words10, words11, words12];

        for (int i = 0; i < 3; i++)
        {
            Assert.True(UmpDataCommand.TryDecode(datagram12[pos..], out ushort seq, out uint[] w, out int consumed));
            Assert.Equal(expectedSeqs[i], seq);
            Assert.Equal(expectedWords[i], w);
            pos += consumed;
        }
    }

    [Fact]
    public void PacketLossRecovery_ThreeDropped_OnlyTwoRecoverable()
    {
        // FEC carries at most 2 history entries — 3-packet loss cannot be fully recovered
        var history = new List<(ushort, uint[])>
        {
            (10, Words(0x20000001u)),
            (11, Words(0x20000002u)),
        };
        var datagram13 = UmpFec.EncodeDatagram(13, Words(0x20000004u), history);

        Assert.True(NetworkMidi2Protocol.TryParseAll(datagram13, out var cmds));
        Assert.Equal(3, cmds.Count); // 2 FEC + 1 current (seq 12 is simply missing)
    }

    // -------------------------------------------------------------------------
    // SendHistory
    // -------------------------------------------------------------------------

    [Fact]
    public void SendHistory_RecordsUpToTwo()
    {
        var h = new UmpFec.SendHistory();
        h.Record(1, Words(0x20000001u));
        h.Record(2, Words(0x20000002u));
        h.Record(3, Words(0x20000003u)); // evicts oldest (1)

        var list = h.GetHistory();
        Assert.Equal(2, list.Count);
        Assert.Equal((ushort)2, list[0].SeqNum);
        Assert.Equal((ushort)3, list[1].SeqNum);
    }

    [Fact]
    public void SendHistory_Empty_ReturnsEmpty()
    {
        var h = new UmpFec.SendHistory();
        Assert.Empty(h.GetHistory());
    }

    [Fact]
    public void SendHistory_OneEntry_ReturnsOneEntry()
    {
        var h = new UmpFec.SendHistory();
        h.Record(42, Words(0x20000000u));

        var list = h.GetHistory();
        Assert.Single(list);
        Assert.Equal((ushort)42, list[0].SeqNum);
        Assert.Equal(Words(0x20000000u), list[0].UmpWords);
    }

    // -------------------------------------------------------------------------
    // Sequence number wraparound
    // -------------------------------------------------------------------------

    [Fact]
    public void EncodeDatagram_MaxSequenceNumber_RoundTrip()
    {
        var umpWords = Words(0x20000000u);
        var history  = new List<(ushort, uint[])> { (0xFFFE, Words(0x20000001u)) };
        var datagram = UmpFec.EncodeDatagram(0xFFFF, umpWords, history);

        // First command: seq=0xFFFE
        Assert.True(UmpDataCommand.TryDecode(datagram[4..], out ushort seq1, out _, out int c1));
        Assert.Equal((ushort)0xFFFE, seq1);

        // Second command: seq=0xFFFF
        Assert.True(UmpDataCommand.TryDecode(datagram[(4 + c1)..], out ushort seq2, out uint[] words2, out _));
        Assert.Equal((ushort)0xFFFF, seq2);
        Assert.Equal(umpWords, words2);
    }
}
