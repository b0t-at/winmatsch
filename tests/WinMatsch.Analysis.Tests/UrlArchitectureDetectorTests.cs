using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class UrlArchitectureDetectorTests
{
    [Theory]
    [InlineData("https://x.com/app-arm64.msi", Architecture.Arm64)]
    [InlineData("https://x.com/app_AARCH64.tar.zip", Architecture.Arm64)]
    [InlineData("https://x.com/app-arm.exe", Architecture.Arm)]
    [InlineData("https://x.com/arm/app.exe", Architecture.Arm)]
    [InlineData("https://x.com/app-win64-setup.exe", Architecture.X64)]
    [InlineData("https://x.com/app-x64.exe", Architecture.X64)]
    [InlineData("https://x.com/app-x86_64.pkg.zip", Architecture.X64)]
    [InlineData("https://x.com/app-x86-64.zip", Architecture.X64)]
    [InlineData("https://x.com/app-amd64.exe", Architecture.X64)]
    [InlineData("https://x.com/app_64bit.exe", Architecture.X64)]
    [InlineData("https://x.com/app-64-bit.exe", Architecture.X64)]
    [InlineData("win64_setup.exe", Architecture.X64)]
    [InlineData("app-arm64.exe", Architecture.Arm64)]
    [InlineData("https://x.com/app-x86.exe", Architecture.X86)]
    [InlineData("https://x.com/app-win32.exe", Architecture.X86)]
    [InlineData("https://x.com/app-ia32.zip", Architecture.X86)]
    [InlineData("https://x.com/app.386.exe", Architecture.X86)]
    [InlineData("https://x.com/app-686.exe", Architecture.X86)]
    [InlineData("app_32-bit.exe", Architecture.X86)]
    [InlineData("https://x.com/app_32bit.exe", Architecture.X86)]
    public void Detect_finds_bounded_architecture_tokens(string url, Architecture expected)
        => Assert.Equal(expected, UrlArchitectureDetector.Detect(url));

    [Theory]
    [InlineData("charm.exe")]
    [InlineData("x640.zip")]
    [InlineData("app.exe")]
    [InlineData("https://example.com/downloads/app.zip")]
    [InlineData("armory.exe")]
    [InlineData("https://x.com/farm/app.exe")]
    public void Detect_returns_null_when_no_token_is_bounded(string url)
        => Assert.Null(UrlArchitectureDetector.Detect(url));

    [Fact]
    public void Arm64_wins_over_x64_and_x86_tokens()
        => Assert.Equal(Architecture.Arm64, UrlArchitectureDetector.Detect("https://x.com/x64/app-arm64-x86.exe"));

    [Fact]
    public void X64_group_wins_over_the_x86_substring_of_x86_64()
        => Assert.Equal(Architecture.X64, UrlArchitectureDetector.Detect("app-x86_64.exe"));
}
