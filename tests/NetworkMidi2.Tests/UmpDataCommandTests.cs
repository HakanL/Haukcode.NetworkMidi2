using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

/// <summary>
/// Tests for UmpDataCommand — the single-command codec (without datagram magic).
///
/// Wire layout of a single UMP Data command:
///   Byte 0   0xFF (command code)
///   Byte 1   Word count (uint8)
///   Byte 2   Sequence number high byte (CSD1)
///   Byte 3   Sequence number low byte  (CSD2)
///   N*4      UMP words big-endian
/// </summary>
public class UmpDataCommandTests
{
    private static uint[] Words(params uint[] w) => w;

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    [Fact]
    public void Encode_CommandCode_IsFF()
    {
        var encoded = UmpDataCommand.Encode(0, [0x20000000u]);
        Assert.Equal(0xFF, encoded[0]);
    }

    [Fact]
    public void Encode_PayloadWordLen_CorrectWordCount()
    {
        var encoded1 = UmpDataCommand.Encode(0, [0x20000000u]);
        Assert.Equal(1, encoded1[1]); // 1 word

        var encoded2 = UmpDataCommand.Encode(0, [0x40000000u, 0x00000000u]);
        Assert.Equal(2, encoded2[1]); // 2 words
    }

    [Fact]
    public void Encode_SequenceNumber_InCsdBytes()
    {
        var encoded = UmpDataCommand.Encode(0x1234, [0x20000000u]);
        Assert.Equal(0x12, encoded[2]); // CSD1 = high byte
        Assert.Equal(0x34, encoded[3]); // CSD2 = low byte
    }

    [Fact]
    public void Encode_UmpWords_BigEndian()
    {
        var encoded = UmpDataCommand.Encode(0, [0xDEADBEEFu]);
        Assert.Equal(0xDE, encoded[4]);
        Assert.Equal(0xAD, encoded[5]);
        Assert.Equal(0xBE, encoded[6]);
        Assert.Equal(0xEF, encoded[7]);
    }

    [Fact]
    public void Encode_EmptyWords_HeaderOnly()
    {
        var encoded = UmpDataCommand.Encode(0x0001, []);
        Assert.Equal(4, encoded.Length); // header only
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0, encoded[1]);     // wordCount = 0
        Assert.Equal(0x00, encoded[2]);  // seqHi
        Assert.Equal(0x01, encoded[3]);  // seqLo
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    [Fact]
    public void Decode_RoundTrip_OneWord()
    {
        var original = Words(0x20906040u);
        var encoded  = UmpDataCommand.Encode(0xABCD, original);

        Assert.True(UmpDataCommand.TryDecode(encoded, out ushort seq, out uint[] words, out int consumed));
        Assert.Equal((ushort)0xABCD, seq);
        Assert.Equal(original, words);
        Assert.Equal(encoded.Length, consumed);
    }

    [Fact]
    public void Decode_RoundTrip_TwoWords()
    {
        var original = Words(0x40000000u, 0xDEADBEEFu);
        var encoded  = UmpDataCommand.Encode(0x0001, original);

        Assert.True(UmpDataCommand.TryDecode(encoded, out ushort seq, out uint[] words, out int consumed));
        Assert.Equal((ushort)0x0001, seq);
        Assert.Equal(original, words);
        Assert.Equal(12, consumed); // 4 header + 2*4 payload
    }

    [Fact]
    public void Decode_EmptyData_ReturnsFalse()
    {
        Assert.False(UmpDataCommand.TryDecode([], out _, out _, out _));
    }

    [Fact]
    public void Decode_WrongCommandCode_ReturnsFalse()
    {
        byte[] data = [0x20, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.False(UmpDataCommand.TryDecode(data, out _, out _, out _));
    }

    [Fact]
    public void Decode_TruncatedPayload_ReturnsFalse()
    {
        // Header says wordCount=2 but only 4 payload bytes provided
        byte[] data = [0xFF, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.False(UmpDataCommand.TryDecode(data, out _, out _, out _));
    }

    // -------------------------------------------------------------------------
    // Sequence number edge cases
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0001)]
    [InlineData(0x7FFF)]
    [InlineData(0xFFFF)]
    public void SequenceNumber_PreservedExactly(ushort seq)
    {
        var encoded = UmpDataCommand.Encode(seq, [0x20000000u]);
        Assert.True(UmpDataCommand.TryDecode(encoded, out ushort decoded, out _, out _));
        Assert.Equal(seq, decoded);
    }

    // -------------------------------------------------------------------------
    // Consumed bytes
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDecode_Consumed_SkipsExactCommand()
    {
        // Two commands concatenated; TryDecode should consume only the first
        var cmd1 = UmpDataCommand.Encode(1, [0x20000001u]);
        var cmd2 = UmpDataCommand.Encode(2, [0x20000002u]);
        var combined = cmd1.Concat(cmd2).ToArray();

        Assert.True(UmpDataCommand.TryDecode(combined, out _, out _, out int consumed));
        Assert.Equal(cmd1.Length, consumed);

        // Decode second command from remaining bytes
        Assert.True(UmpDataCommand.TryDecode(combined[consumed..], out ushort seq2, out _, out _));
        Assert.Equal((ushort)2, seq2);
    }
}
