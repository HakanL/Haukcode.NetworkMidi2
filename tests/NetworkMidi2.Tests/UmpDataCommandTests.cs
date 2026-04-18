using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

public class UmpDataCommandTests
{
    [Fact]
    public void EncodePayload_ThenTryParse_RoundTrip()
    {
        // FEC block: just 1 byte FecCount=0 + 2-byte primary len=0
        byte[] fecBlock = [0x00, 0x00, 0x00];

        var encoded = UmpDataCommand.EncodePayload(0xDEADBEEF, fecBlock);

        Assert.True(UmpDataCommand.TryParsePayload(encoded, out var seqNum, out var parsedFec));
        Assert.Equal(0xDEADBEEFu, seqNum);
        Assert.Equal(fecBlock, parsedFec.ToArray());
    }

    [Fact]
    public void SequenceNumber_PreservedExactly()
    {
        byte[] fecBlock = [0x00, 0x00, 0x00];

        foreach (uint seq in new uint[] { 0, 1, 0x7FFFFFFF, 0xFFFFFFFF })
        {
            var encoded = UmpDataCommand.EncodePayload(seq, fecBlock);
            Assert.True(UmpDataCommand.TryParsePayload(encoded, out var parsed, out _));
            Assert.Equal(seq, parsed);
        }
    }

    [Fact]
    public void EmptyFecBlock_RoundTrip()
    {
        var encoded = UmpDataCommand.EncodePayload(42, []);
        Assert.True(UmpDataCommand.TryParsePayload(encoded, out var seqNum, out var fec));
        Assert.Equal(42u, seqNum);
        Assert.True(fec.IsEmpty);
    }

    [Fact]
    public void TryParsePayload_TooShort_ReturnsFalse()
    {
        // Only 3 bytes — need at least 4 for sequence number
        var buf = new byte[] { 0x00, 0x00, 0x00 };
        Assert.False(UmpDataCommand.TryParsePayload(buf, out _, out _));
    }

    [Fact]
    public void LargePayload_RoundTrip()
    {
        // Simulate a FEC block with 8 UMP words worth of data in it
        var fakeFec = new byte[1 + 2 + 8 * 4]; // FecCount + primaryLen + 8 words
        fakeFec[0] = 0; // FecCount
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fakeFec.AsSpan(1), (ushort)(8 * 4));
        for (int i = 0; i < 8; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fakeFec.AsSpan(3 + i * 4), (uint)(i + 1));

        var encoded = UmpDataCommand.EncodePayload(999, fakeFec);
        Assert.True(UmpDataCommand.TryParsePayload(encoded, out var seqNum, out var parsedFec));
        Assert.Equal(999u, seqNum);
        Assert.Equal(fakeFec, parsedFec.ToArray());
    }
}
