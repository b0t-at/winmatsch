using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class PackageVersionTests
{
    [Theory]
    // Plain numeric ordering
    [InlineData("1.0", "2.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("0.9", "1.0")]
    [InlineData("1.9", "1.10")]
    [InlineData("13.9.8", "14.0")]
    // A version with a suffix is less than the same version without one
    [InlineData("1.0-rc", "1.0")]
    [InlineData("1.0.0-beta", "1.0.0")]
    // Suffixes compare case-insensitively and alphabetically
    [InlineData("1.0-alpha", "1.0-beta")]
    // Marketing-style versions
    [InlineData("22H2", "23H1")]
    // Unknown sorts below everything
    [InlineData("unknown", "0.0.1")]
    [InlineData("Unknown", "1.0")]
    // A numeric part overflowing ulong is treated as a suffix-only part (number 0)
    [InlineData("99999999999999999999", "1")]
    public void Ordering_LeftIsLessThanRight(string smaller, string larger)
    {
        var left = new PackageVersion(smaller);
        var right = new PackageVersion(larger);

        Assert.True(left.CompareTo(right) < 0);
        Assert.True(right.CompareTo(left) > 0);
        Assert.True(left < right);
        Assert.True(right > left);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1", "1.0.0.0.0")]
    [InlineData("1.2.3", "1.02.03")]
    [InlineData("1.0.RC", "1.0.rc")]
    [InlineData("unknown", "UNKNOWN")]
    public void Equivalence_IgnoresTrailingZerosAndSuffixCase(string left, string right)
    {
        var a = new PackageVersion(left);
        var b = new PackageVersion(right);

        Assert.True(a.IsEquivalentTo(b));
        Assert.True(b.IsEquivalentTo(a));
    }

    [Fact]
    public void CompareTo_UsesRawStringAsTiebreakForEquivalentVersions()
    {
        var shorter = new PackageVersion("1.0");
        var longer = new PackageVersion("1.0.0");

        int forward = shorter.CompareTo(longer);
        int backward = longer.CompareTo(shorter);

        Assert.True(shorter.IsEquivalentTo(longer));
        Assert.NotEqual(0, forward);
        Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
    }

    [Fact]
    public void Equality_IsExactStringIdentity()
    {
        Assert.Equal(new PackageVersion("1.0"), new PackageVersion("1.0"));
        Assert.NotEqual(new PackageVersion("1.0"), new PackageVersion("1.0.0"));
        Assert.True(new PackageVersion("1.0") == new PackageVersion("1.0"));
    }

    [Fact]
    public void Sorting_ProducesWinGetOrder()
    {
        List<PackageVersion> versions =
        [
            new("1.10.0"),
            new("unknown"),
            new("1.2.0"),
            new("1.2.0-rc.1"),
            new("0.9"),
            new("1.2.0.1"),
        ];

        versions.Sort();

        Assert.Equal(
            ["unknown", "0.9", "1.2.0-rc.1", "1.2.0", "1.2.0.1", "1.10.0"],
            versions.ConvertAll(static v => v.Value));
    }

    [Fact]
    public void IsUnknown_DetectsCaseInsensitively()
    {
        Assert.True(new PackageVersion("unknown").IsUnknown);
        Assert.True(new PackageVersion("Unknown").IsUnknown);
        Assert.False(new PackageVersion("unknown2").IsUnknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1<0")]
    [InlineData("1|0")]
    [InlineData("1/0")]
    [InlineData("1:0")]
    [InlineData("1*0")]
    public void Constructor_RejectsInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new PackageVersion(value));
        Assert.False(PackageVersion.TryCreate(value, out _));
    }

    [Fact]
    public void Constructor_RejectsOverlongValues()
    {
        string value = new('1', PackageVersion.MaxLength + 1);
        Assert.Throws<ArgumentException>(() => new PackageVersion(value));
    }

    [Fact]
    public void Value_PreservesRawString()
    {
        Assert.Equal("01.02.003", new PackageVersion("01.02.003").Value);
    }
}
