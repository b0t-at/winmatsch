using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class LicenseNoticeTests
{
    [Fact]
    public void Third_party_notice_is_shipped_with_validation_outputs()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

        Assert.True(File.Exists(path), $"Expected third-party notice at '{path}'.");
        string notice = File.ReadAllText(path);
        Assert.Contains("WinGet manifest schemas 1.12.0", notice, StringComparison.Ordinal);
        Assert.Contains("JsonSchema.Net 8.0.5", notice, StringComparison.Ordinal);
        Assert.Contains("Humanizer.Core 3.0.1", notice, StringComparison.Ordinal);
        Assert.Contains("YamlDotNet 16.3.0", notice, StringComparison.Ordinal);
    }
}
