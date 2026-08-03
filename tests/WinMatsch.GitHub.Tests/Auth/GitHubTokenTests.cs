using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.GitHub.Tests.Auth;

public class GitHubTokenTests
{
    [Fact]
    public void ToString_returns_redacted_placeholder()
    {
        var token = new GitHubToken("ghp_example1234567890");

        Assert.Equal(GitHubToken.RedactedPlaceholder, token.ToString());
        Assert.DoesNotContain("ghp_example", token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RevealValue_returns_the_secret()
    {
        var token = new GitHubToken("ghp_example1234567890");

        Assert.Equal("ghp_example1234567890", token.RevealValue());
    }

    [Fact]
    public void Length_reports_the_secret_length_without_revealing_it()
    {
        var token = new GitHubToken("ghp_short");

        Assert.Equal(9, token.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_tokens_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new GitHubToken(value));
    }

    [Fact]
    public void Null_tokens_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new GitHubToken(null!));
    }

    [Theory]
    [InlineData("ghp_with space")]
    [InlineData("ghp_with\ttab")]
    [InlineData("ghp_with\nnewline")]
    [InlineData("ghp_with\u0007bell")]
    public void Tokens_with_whitespace_or_control_characters_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new GitHubToken(value));
    }

    [Fact]
    public void Tokens_with_the_same_value_are_equal()
    {
        var left = new GitHubToken("ghp_example1234567890");
        var right = new GitHubToken("ghp_example1234567890");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Tokens_with_different_values_are_not_equal()
    {
        var left = new GitHubToken("ghp_example1234567890");
        var right = new GitHubToken("ghp_other9876543210");

        Assert.NotEqual(left, right);
        Assert.False(left.Equals(null));
    }
}
