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

    [Theory]
    [InlineData(" microsoft/winget-pkgs")]
    [InlineData("microsoft / winget-pkgs")]
    [InlineData("microsoft/winget-pkgs ")]
    public void Parse_trims_surrounding_whitespace_from_owner_and_name(string value)
    {
        RepositoryCoordinates repository = RepositoryCoordinates.Parse(value);

        Assert.Equal("microsoft", repository.Owner);
        Assert.Equal("winget-pkgs", repository.Name);
    }

    [Fact]
    public void Constructor_rejects_invalid_parts_with_argument_exceptions()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryCoordinates("", "name"));
        Assert.Throws<ArgumentException>(() => new RepositoryCoordinates("owner", " "));
        Assert.Throws<ArgumentException>(() => new RepositoryCoordinates("ow/ner", "name"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("winget-pkgs")]
    [InlineData("microsoft/winget-pkgs/extra")]
    [InlineData("/winget-pkgs")]
    [InlineData("microsoft/")]
    [InlineData("/")]
    [InlineData(" /name")]
    [InlineData("owner/ ")]
    public void Parse_rejects_every_invalid_syntax_with_format_exception(string? value)
    {
        var exception = Assert.Throws<FormatException>(() => RepositoryCoordinates.Parse(value));

        Assert.Contains("owner/name", exception.Message, StringComparison.Ordinal);
    }
}
