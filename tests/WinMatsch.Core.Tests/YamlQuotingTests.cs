using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Core.Tests;

/// <summary>
/// The quoting decision is the linchpin of clean manifests: quote too little and YAML parsers
/// silently change types; quote too much and diffs against existing manifests get noisy.
/// </summary>
public sealed class YamlQuotingTests
{
    [Theory]
    // Ordinary strings
    [InlineData("hello")]
    [InlineData("Test Package")]
    [InlineData("über")]
    // Versions: two dots make them non-numeric
    [InlineData("1.2.3")]
    [InlineData("10.0.17763.0")]
    // Switches and paths
    [InlineData("/S")]
    [InlineData("--silent")]
    [InlineData("-quiet")]
    [InlineData("/qn ALLUSERS=1")]
    [InlineData("C:\\Program Files\\App")]
    // Colon not followed by a space is fine
    [InlineData("x:y")]
    [InlineData("https://example.com/app.exe")]
    // Hash not preceded by a space is fine
    [InlineData("a#b")]
    // Hex-looking but not a number (letters beyond a-f range or no 0x prefix)
    [InlineData("A3F5E8D9")]
    [InlineData("22H2")]
    public void PlainScalars_AreNotQuoted(string value)
    {
        Assert.False(YamlEmitter.NeedsQuoting(value));
    }

    [Theory]
    // Booleans and null-likes (YAML 1.1 and 1.2)
    [InlineData("true")]
    [InlineData("False")]
    [InlineData("YES")]
    [InlineData("no")]
    [InlineData("on")]
    [InlineData("Off")]
    [InlineData("y")]
    [InlineData("N")]
    [InlineData("null")]
    [InlineData("~")]
    // Numbers in every YAML flavor
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("-5")]
    [InlineData("+7")]
    [InlineData("0x1F")]
    [InlineData("0b101")]
    [InlineData("0o17")]
    [InlineData("010")]
    [InlineData("1e5")]
    [InlineData("1.5e-3")]
    [InlineData(".5")]
    [InlineData(".inf")]
    [InlineData(".NaN")]
    [InlineData("1_000")]
    [InlineData("1:30")]
    // Timestamps
    [InlineData("2024-01-15")]
    [InlineData("2001-12-14 21:59:43")]
    // Structural indicators at the start
    [InlineData("[foo")]
    [InlineData("{bar")]
    [InlineData("#comment")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("!tag")]
    [InlineData("|literal")]
    [InlineData(">folded")]
    [InlineData("'single")]
    [InlineData("\"double")]
    [InlineData("%directive")]
    [InlineData("@at")]
    [InlineData("`tick")]
    [InlineData(",comma")]
    [InlineData("=eq")]
    // Indicators only when followed by whitespace
    [InlineData("- item")]
    [InlineData("? key")]
    [InlineData(": value")]
    [InlineData("-")]
    // Mapping-like content
    [InlineData("key: value")]
    [InlineData("ends:")]
    // Comment start within the value
    [InlineData("value # comment")]
    // Whitespace at the edges, tabs, empty
    [InlineData(" x")]
    [InlineData("x ")]
    [InlineData("a\tb")]
    [InlineData("")]
    public void AmbiguousScalars_AreQuoted(string value)
    {
        Assert.True(YamlEmitter.NeedsQuoting(value));
    }
}
