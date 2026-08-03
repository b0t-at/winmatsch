using WinMatsch.Analysis.Msi;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class MsiStreamNameTests
{
    // Known encoding vectors for real MSI stream names.
    [Theory]
    [InlineData("\u4840\u3B3F\u43F2\u4438\u45B1", "_Columns", true)]
    [InlineData("\u4840\u3F7F\u4164\u422F\u4836", "_Tables", true)]
    [InlineData("\u44CA\u47B3\u46E8\u4828", "App.exe", false)]
    [InlineData("\u0005SummaryInformation", "\u0005SummaryInformation", false)]
    public void Decode_produces_the_logical_name(string encoded, string expectedName, bool expectedIsTable)
    {
        string name = MsiStreamName.Decode(encoded, out bool isTable);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedIsTable, isTable);
    }

    [Theory]
    [InlineData("_StringPool", true)]
    [InlineData("_StringData", true)]
    [InlineData("Property", true)]
    [InlineData("Binary.Some_File.dll", false)]
    public void Decode_round_trips_the_fixture_encoder(string name, bool isTable)
    {
        string encoded = MsiFixtures.EncodeStreamName(name, isTable);

        string decoded = MsiStreamName.Decode(encoded, out bool decodedIsTable);

        Assert.Equal(name, decoded);
        Assert.Equal(isTable, decodedIsTable);
    }
}

public class MsiStringPoolTests
{
    [Fact]
    public void Short_ref_pool_is_parsed_with_two_byte_references()
    {
        // Codepage 65001, entries: ("Foo", refcount 3), ("Quux", refcount 7).
        byte[] pool = [0xE9, 0xFD, 0x00, 0x00, 0x03, 0x00, 0x03, 0x00, 0x04, 0x00, 0x07, 0x00];
        byte[] data = "FooQuux"u8.ToArray();

        MsiStringPool parsed = MsiStringPool.Read(pool, data);

        Assert.False(parsed.LongStringRefs);
        Assert.Equal(2, parsed.StringRefWidth);
        Assert.Equal(65001, parsed.Codepage);
        Assert.Equal("Foo", parsed.Get(1));
        Assert.Equal("Quux", parsed.Get(2));
        Assert.Null(parsed.Get(0));
        Assert.Null(parsed.Get(3));
    }

    [Fact]
    public void Long_ref_bit_switches_references_to_three_bytes()
    {
        // Bit 31 of the header dword marks three-byte string references.
        byte[] pool = [0xE9, 0xFD, 0x00, 0x80, 0x03, 0x00, 0x01, 0x00];
        byte[] data = "Foo"u8.ToArray();

        MsiStringPool parsed = MsiStringPool.Read(pool, data);

        Assert.True(parsed.LongStringRefs);
        Assert.Equal(3, parsed.StringRefWidth);
        Assert.Equal(65001, parsed.Codepage);
        Assert.Equal(0x123456, parsed.ReadStringRef([0x56, 0x34, 0x12], 0));
        Assert.Equal(0, parsed.ReadStringRef([0x00, 0x00, 0x00], 0));
    }

    [Fact]
    public void Large_string_entry_reads_the_length_from_the_following_dword()
    {
        // Entry with length 0 but refcount 1: the real length follows as a 32-bit value.
        byte[] pool = [0xE9, 0xFD, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x00, 0x00];
        byte[] data = "Foo"u8.ToArray();

        MsiStringPool parsed = MsiStringPool.Read(pool, data);

        Assert.Equal("Foo", parsed.Get(1));
    }

    [Fact]
    public void Empty_slot_entry_is_an_empty_string_and_consumes_no_data()
    {
        // ("Foo", 1), empty slot (0, 0), ("Quux", 7): the empty slot has no trailing dword.
        byte[] pool =
        [
            0xE9, 0xFD, 0x00, 0x00,
            0x03, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x07, 0x00,
        ];
        byte[] data = "FooQuux"u8.ToArray();

        MsiStringPool parsed = MsiStringPool.Read(pool, data);

        Assert.Equal("Foo", parsed.Get(1));
        Assert.Equal("", parsed.Get(2));
        Assert.Equal("Quux", parsed.Get(3));
    }

    [Fact]
    public void Truncated_string_data_throws()
    {
        byte[] pool = [0xE9, 0xFD, 0x00, 0x00, 0x10, 0x00, 0x01, 0x00];
        byte[] data = "short"u8.ToArray();

        Assert.Throws<InvalidDataException>(() => MsiStringPool.Read(pool, data));
    }

    [Fact]
    public void Pool_shorter_than_the_header_throws()
        => Assert.Throws<InvalidDataException>(() => MsiStringPool.Read([0xE9, 0xFD], []));
}
