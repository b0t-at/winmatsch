using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class PackageIdentifierTests
{
    [Theory]
    [InlineData("Microsoft.PowerToys")]
    [InlineData("A.B")]
    [InlineData("A.B.C.D.E.F.G.H")]
    [InlineData("Microsoft.VCRedist.2015+.x64")]
    [InlineData("7zip.7zip")]
    public void Constructor_AcceptsValidIdentifiers(string value)
    {
        var identifier = new PackageIdentifier(value);
        Assert.Equal(value, identifier.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SingleSegment")]
    [InlineData("A.B.C.D.E.F.G.H.I")]
    [InlineData("A..B")]
    [InlineData(".A.B")]
    [InlineData("A.B.")]
    [InlineData("A B.C")]
    [InlineData("A.B/C")]
    [InlineData("A.B|C")]
    public void Constructor_RejectsInvalidIdentifiers(string value)
    {
        Assert.Throws<ArgumentException>(() => new PackageIdentifier(value));
        Assert.False(PackageIdentifier.TryCreate(value, out _));
    }

    [Fact]
    public void Constructor_RejectsOverlongSegmentsAndValues()
    {
        Assert.Throws<ArgumentException>(() => new PackageIdentifier($"A.{new string('b', PackageIdentifier.MaxSegmentLength + 1)}"));

        string overlong = string.Join('.', Enumerable.Repeat(new string('x', PackageIdentifier.MaxSegmentLength), 5));
        Assert.True(overlong.Length > PackageIdentifier.MaxLength);
        Assert.Throws<ArgumentException>(() => new PackageIdentifier(overlong));
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var lower = new PackageIdentifier("microsoft.powertoys");
        var mixed = new PackageIdentifier("Microsoft.PowerToys");

        Assert.Equal(lower, mixed);
        Assert.Equal(lower.GetHashCode(), mixed.GetHashCode());
        Assert.True(lower == mixed);
        Assert.Equal("microsoft.powertoys", lower.Value);
    }

    [Fact]
    public void Segments_SplitOnDots()
    {
        var identifier = new PackageIdentifier("Microsoft.VisualStudio.2022.Community");
        Assert.Equal(["Microsoft", "VisualStudio", "2022", "Community"], identifier.Segments);
    }
}
