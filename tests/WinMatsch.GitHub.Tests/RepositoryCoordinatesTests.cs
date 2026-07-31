using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class RepositoryCoordinatesTests
{
    [Fact]
    public void Parse_round_trips_owner_and_name()
    {
        RepositoryCoordinates repository = RepositoryCoordinates.Parse("microsoft/winget-pkgs");

        Assert.Equal("microsoft", repository.Owner);
        Assert.Equal("winget-pkgs", repository.Name);
        Assert.Equal("microsoft/winget-pkgs", repository.ToString());
    }

    [Fact]
    public void Parse_rejects_empty_value()
        => Assert.Throws<ArgumentException>(() => RepositoryCoordinates.Parse(""));

    [Theory]
    [InlineData("winget-pkgs")]
    [InlineData("microsoft/winget-pkgs/extra")]
    public void Parse_rejects_invalid_shapes(string value)
        => Assert.Throws<FormatException>(() => RepositoryCoordinates.Parse(value));
}
