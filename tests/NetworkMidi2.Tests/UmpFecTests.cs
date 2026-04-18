using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

public class UmpFecTests
{
    private static uint[] Words(params uint[] w) => w;

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    [Fact]
    public void Encode_NoHistory_FecCountIsZero()
    {
        var primary = Words(0x20906040u, 0x00000000u); // dummy MIDI1 note-on
        var block   = UmpFec.Encode(primary, []);

        // First byte must be FecCount=0
        Assert.Equal(0, block[0]);

        // Decode back
        Assert.True(UmpFec.TryDecode(block, out var decoded, out var hist));
        Assert.Equal(primary, decoded);
        Assert.Empty(hist);
    }

    [Fact]
    public void Encode_OneHistory_FecCountIsOne()
    {
        var primary = Words(0x20906040u);
        var history = new List<(uint, uint[])> { (10u, Words(0x20804000u)) };
        var block   = UmpFec.Encode(primary, history);

        Assert.Equal(1, block[0]);

        Assert.True(UmpFec.TryDecode(block, out var decoded, out var hist));
        Assert.Equal(primary, decoded);
        Assert.Single(hist);
        Assert.Equal(10u, hist[0].SequenceNumber);
        Assert.Equal(Words(0x20804000u), hist[0].UmpWords);
    }

    [Fact]
    public void Encode_TwoHistory_FecCountIsTwo()
    {
        var primary = Words(0x20906040u);
        var history = new List<(uint, uint[])>
        {
            (10u, Words(0x20804000u)),
            (11u, Words(0x20904020u)),
        };
        var block = UmpFec.Encode(primary, history);

        Assert.Equal(2, block[0]);

        Assert.True(UmpFec.TryDecode(block, out var decoded, out var hist));
        Assert.Equal(primary, decoded);
        Assert.Equal(2, hist.Count);
        Assert.Equal(10u, hist[0].SequenceNumber);
        Assert.Equal(11u, hist[1].SequenceNumber);
    }

    [Fact]
    public void Encode_MoreThanTwoHistory_ClampsToTwo()
    {
        var primary = Words(0x20906040u);
        var history = new List<(uint, uint[])>
        {
            (1u, Words(0x20000001u)),
            (2u, Words(0x20000002u)),
            (3u, Words(0x20000003u)), // should be ignored
        };
        var block = UmpFec.Encode(primary, history);

        Assert.Equal(2, block[0]);
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    [Fact]
    public void Decode_NoHistory_PrimaryWordsCorrect()
    {
        var primary = Words(0xDEADBEEFu, 0x12345678u);
        var block   = UmpFec.Encode(primary, []);

        Assert.True(UmpFec.TryDecode(block, out var decoded, out _));
        Assert.Equal(primary, decoded);
    }

    [Fact]
    public void Decode_EmptyFecBlock_ReturnsFalse()
    {
        Assert.False(UmpFec.TryDecode([], out _, out _));
    }

    // -------------------------------------------------------------------------
    // Packet loss recovery simulation
    // -------------------------------------------------------------------------

    [Fact]
    public void PacketLossRecovery_OneDropped_RecoveredFromFec()
    {
        // Packet 10 sent with no history
        var words10 = Words(0x20906040u);
        var block10 = UmpFec.Encode(words10, []);
        var payload10 = UmpDataCommand.EncodePayload(10, block10);

        // Packet 11 sent with history [seq=10]
        var words11 = Words(0x20804000u);
        var history11 = new List<(uint, uint[])> { (10u, words10) };
        var block11 = UmpFec.Encode(words11, history11);
        var payload11 = UmpDataCommand.EncodePayload(11, block11);

        // Simulate: packet 10 is LOST; packet 11 arrives
        Assert.True(UmpDataCommand.TryParsePayload(payload11, out var seqNum, out var fecBlock));
        Assert.Equal(11u, seqNum);
        Assert.True(UmpFec.TryDecode(fecBlock, out var primary11, out var hist));

        // Primary of packet 11 should decode correctly
        Assert.Equal(words11, primary11);

        // Historical entry recovers packet 10
        Assert.Single(hist);
        Assert.Equal(10u, hist[0].SequenceNumber);
        Assert.Equal(words10, hist[0].UmpWords);
    }

    [Fact]
    public void PacketLossRecovery_TwoDropped_BothRecoveredFromNextPacket()
    {
        var words10 = Words(0x20906040u);
        var words11 = Words(0x20804000u);
        var words12 = Words(0x20904020u);

        // Packet 12 carries history [10, 11]
        var history12 = new List<(uint, uint[])>
        {
            (10u, words10),
            (11u, words11),
        };
        var block12   = UmpFec.Encode(words12, history12);
        var payload12 = UmpDataCommand.EncodePayload(12, block12);

        // Packets 10 and 11 are both lost; packet 12 arrives
        Assert.True(UmpDataCommand.TryParsePayload(payload12, out _, out var fecBlock));
        Assert.True(UmpFec.TryDecode(fecBlock, out var primary12, out var hist));

        Assert.Equal(words12, primary12);
        Assert.Equal(2, hist.Count);
        Assert.Equal(10u, hist[0].SequenceNumber);
        Assert.Equal(11u, hist[1].SequenceNumber);
        Assert.Equal(words10, hist[0].UmpWords);
        Assert.Equal(words11, hist[1].UmpWords);
    }

    [Fact]
    public void PacketLossRecovery_ThreeDropped_OnlyTwoRecoverable()
    {
        // FEC only carries 2 historical entries — a 3-packet loss cannot be fully recovered
        var history = new List<(uint, uint[])>
        {
            (10u, Words(0x20000001u)),
            (11u, Words(0x20000002u)),
            // packet 12 also dropped — not representable in FEC
        };
        var block13   = UmpFec.Encode(Words(0x20000004u), history);
        Assert.True(UmpFec.TryDecode(block13, out _, out var hist));

        // At most 2 entries recovered
        Assert.Equal(2, hist.Count);
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
        Assert.Equal(2u, list[0].SeqNum);
        Assert.Equal(3u, list[1].SeqNum);
    }

    [Fact]
    public void SendHistory_Empty_ReturnsEmpty()
    {
        var h = new UmpFec.SendHistory();
        Assert.Empty(h.GetHistory());
    }

    // -------------------------------------------------------------------------
    // Gap detection wraparound (tested via IsInGap logic via decode simulation)
    // -------------------------------------------------------------------------

    [Fact]
    public void Encode_Decode_MaxSequenceNumber_RoundTrip()
    {
        var primary = Words(0x20000000u);
        var history = new List<(uint, uint[])> { (uint.MaxValue, Words(0x20000001u)) };
        var block   = UmpFec.Encode(primary, history);

        Assert.True(UmpFec.TryDecode(block, out var decoded, out var hist));
        Assert.Equal(primary, decoded);
        Assert.Single(hist);
        Assert.Equal(uint.MaxValue, hist[0].SequenceNumber);
    }
}
