using System.Text;
using WinMatsch.Analysis.Burn;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class CabinetReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_single_file_roundtrips(bool msZip)
    {
        byte[] data = Encoding.UTF8.GetBytes("<BurnManifest>manifest payload</BurnManifest>");
        byte[] cabinet = BurnFixtures.BuildCabinet([("0", data)], msZip);

        Assert.Equal(data, CabinetReader.ReadFile(cabinet, "0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_spanning_multiple_data_blocks_roundtrips(bool msZip)
    {
        byte[] data = new byte[100_000]; // Forces four CFDATA blocks of at most 32 KiB each.
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) ^ (i >> 8));
        }

        byte[] cabinet = BurnFixtures.BuildCabinet([("0", data)], msZip);

        Assert.Equal(data, CabinetReader.ReadFile(cabinet, "0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_file_is_sliced_at_its_folder_offset(bool msZip)
    {
        byte[] first = Encoding.UTF8.GetBytes("first file");
        byte[] second = Encoding.UTF8.GetBytes("second file with different content");
        byte[] cabinet = BurnFixtures.BuildCabinet([("0", first), ("u1", second)], msZip);

        Assert.Equal(first, CabinetReader.ReadFile(cabinet, "0"));
        Assert.Equal(second, CabinetReader.ReadFile(cabinet, "u1"));
    }

    [Fact]
    public void A_missing_file_returns_null()
    {
        byte[] cabinet = BurnFixtures.BuildCabinet([("0", "data"u8.ToArray())]);

        Assert.Null(CabinetReader.ReadFile(cabinet, "1"));
    }

    [Fact]
    public void A_missing_signature_throws()
        => Assert.Throws<InvalidDataException>(() => CabinetReader.ReadFile(new byte[64], "0"));

    [Fact]
    public void An_unsupported_compression_type_throws()
    {
        // Compression type 3 is LZX, which this reader does not implement.
        byte[] cabinet = BurnFixtures.BuildCabinet([("0", "data"u8.ToArray())], compressionTypeOverride: 3);

        var exception = Assert.Throws<InvalidDataException>(() => CabinetReader.ReadFile(cabinet, "0"));

        Assert.Contains("compression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_data_block_throws()
    {
        byte[] cabinet = BurnFixtures.BuildCabinet([("0", "data that gets cut off"u8.ToArray())]);

        Assert.Throws<InvalidDataException>(() => CabinetReader.ReadFile(cabinet[..^8], "0"));
    }
}
