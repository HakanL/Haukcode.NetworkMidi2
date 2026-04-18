using Haukcode.NetworkMidi2;

namespace NetworkMidi2.Tests;

public class UmpHelpersTests
{
    // -------------------------------------------------------------------------
    // GetWordCount
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0x0, 1)]
    [InlineData(0x1, 1)]
    [InlineData(0x2, 1)]
    [InlineData(0x6, 1)]
    [InlineData(0x7, 1)]
    public void GetWordCount_32BitTypes_Returns1(byte msgType, int expected)
    {
        uint word = (uint)msgType << 28;
        Assert.Equal(expected, UmpHelpers.GetWordCount(word));
    }

    [Theory]
    [InlineData(0x3, 2)]
    [InlineData(0x4, 2)]
    [InlineData(0xD, 2)]
    public void GetWordCount_64BitTypes_Returns2(byte msgType, int expected)
    {
        uint word = (uint)msgType << 28;
        Assert.Equal(expected, UmpHelpers.GetWordCount(word));
    }

    [Theory]
    [InlineData(0x5, 4)]
    [InlineData(0xF, 4)]
    public void GetWordCount_128BitTypes_Returns4(byte msgType, int expected)
    {
        uint word = (uint)msgType << 28;
        Assert.Equal(expected, UmpHelpers.GetWordCount(word));
    }

    // -------------------------------------------------------------------------
    // ReadWords / WriteWords
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadWords_BigEndian_Correct()
    {
        byte[] bytes = [0x12, 0x34, 0x56, 0x78,  0xAB, 0xCD, 0xEF, 0x00];
        var words = UmpHelpers.ReadWords(bytes);

        Assert.Equal(2, words.Length);
        Assert.Equal(0x12345678u, words[0]);
        Assert.Equal(0xABCDEF00u, words[1]);
    }

    [Fact]
    public void WriteWords_BigEndian_Correct()
    {
        uint[] words = [0x12345678u, 0xABCDEF00u];
        var bytes = new byte[8];
        UmpHelpers.WriteWords(words, bytes);

        Assert.Equal(0x12, bytes[0]);
        Assert.Equal(0x34, bytes[1]);
        Assert.Equal(0x56, bytes[2]);
        Assert.Equal(0x78, bytes[3]);
        Assert.Equal(0xAB, bytes[4]);
        Assert.Equal(0xCD, bytes[5]);
        Assert.Equal(0xEF, bytes[6]);
        Assert.Equal(0x00, bytes[7]);
    }

    [Fact]
    public void ReadWrite_RoundTrip()
    {
        uint[] original = [0xDEADBEEF, 0x12345678, 0x00000000, 0xFFFFFFFF];
        var bytes = UmpHelpers.ToBytes(original);
        var decoded = UmpHelpers.ReadWords(bytes);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ReadWords_EmptySpan_ReturnsEmpty()
    {
        var words = UmpHelpers.ReadWords([]);
        Assert.Empty(words);
    }

    // -------------------------------------------------------------------------
    // IsWellFormed
    // -------------------------------------------------------------------------

    [Fact]
    public void IsWellFormed_SingleType2Word_ReturnsTrue()
    {
        uint[] words = [0x20000000u]; // type 2, 1 word
        Assert.True(UmpHelpers.IsWellFormed(words));
    }

    [Fact]
    public void IsWellFormed_Type4TwoWords_ReturnsTrue()
    {
        uint[] words = [0x40000000u, 0x00000000u]; // type 4, 2 words
        Assert.True(UmpHelpers.IsWellFormed(words));
    }

    [Fact]
    public void IsWellFormed_Type5FourWords_ReturnsTrue()
    {
        uint[] words = [0x50000000u, 0x00000000u, 0x00000000u, 0x00000000u]; // type 5, 4 words
        Assert.True(UmpHelpers.IsWellFormed(words));
    }

    [Fact]
    public void IsWellFormed_Type4OneWord_ReturnsFalse()
    {
        // type 4 needs 2 words but only 1 given
        uint[] words = [0x40000000u];
        Assert.False(UmpHelpers.IsWellFormed(words));
    }

    [Fact]
    public void IsWellFormed_TwoCompleteMessages_ReturnsTrue()
    {
        uint[] words =
        [
            0x20000000u,  // type 2 (1 word)
            0x40000000u, 0x00000000u,  // type 4 (2 words)
        ];
        Assert.True(UmpHelpers.IsWellFormed(words));
    }

    [Fact]
    public void IsWellFormed_Empty_ReturnsTrue()
    {
        Assert.True(UmpHelpers.IsWellFormed([]));
    }
}
