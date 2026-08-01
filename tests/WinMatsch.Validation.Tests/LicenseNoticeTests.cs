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
        Assert.Contains("OpenMcdf 3.1.4", notice, StringComparison.Ordinal);
        Assert.Contains("Mozilla Public License 2.0", notice, StringComparison.Ordinal);
        Assert.Contains("SharpCompress 1.0.0", notice, StringComparison.Ordinal);
        Assert.Contains("Spectre.Console and Spectre.Console.Ansi 0.57.2", notice, StringComparison.Ordinal);
        Assert.Contains("System.CommandLine 2.0.10", notice, StringComparison.Ordinal);
        Assert.Contains("JsonSchema.Net 8.0.5", notice, StringComparison.Ordinal);
        Assert.Contains("Humanizer.Core 3.0.1", notice, StringComparison.Ordinal);
        Assert.Contains("YamlDotNet 16.3.0", notice, StringComparison.Ordinal);
    }
}
