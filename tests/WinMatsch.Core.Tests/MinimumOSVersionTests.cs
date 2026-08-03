using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class MinimumOSVersionTests
{
    [Theory]
    [InlineData("10")]
    [InlineData("10.0")]
    [InlineData("10.0.17763")]
    [InlineData("10.0.17763.0")]
    [InlineData("0.0.0.0")]
    [InlineData("65535.65535.65535.65535")]
    public void Constructor_AcceptsValidVersions(string value)
    {
        Assert.Equal(value, new MinimumOSVersion(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.0.0")]
    [InlineData("65536")]
    [InlineData("01")]
    [InlineData("10..0")]
    [InlineData("10.0.a")]
    [InlineData("-1")]
    public void Constructor_RejectsInvalidVersions(string value)
    {
        Assert.Throws<ArgumentException>(() => new MinimumOSVersion(value));
        Assert.False(MinimumOSVersion.TryCreate(value, out _));
    }

    [Fact]
    public void Ordering_ComparesNumericallyWithImpliedZeros()
    {
        Assert.True(new MinimumOSVersion("10.0.17763.0") < new MinimumOSVersion("10.0.22000.0"));
        Assert.True(new MinimumOSVersion("6.3") < new MinimumOSVersion("10.0"));
        Assert.Equal(new MinimumOSVersion("10.0"), new MinimumOSVersion("10.0.0.0"));
        Assert.Equal(new MinimumOSVersion("10.0").GetHashCode(), new MinimumOSVersion("10.0.0.0").GetHashCode());
    }
}
