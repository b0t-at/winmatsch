using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Workflows.Mapping;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

/// <summary>
/// The analysis-side <see cref="UrlArchitectureDetector"/> and the mapping-side
/// <see cref="ArchitectureTokenClassifier"/> share one token table; disagreement between
/// them manufactures false ARCH_CONFLICT stops (e.g. Unipro.UGENE's "win-x86-64" naming).
/// </summary>
public sealed class ArchitectureTokenConsistencyTests
{
    [Theory]
    [InlineData("ugene-53.1-win-x86-64.exe", Architecture.X64)]
    [InlineData("app-x86-64.zip", Architecture.X64)]
    [InlineData("tool-x86_64.zip", Architecture.X64)]
    [InlineData("setup-i686.exe", Architecture.X86)]
    [InlineData("app-arm64.msi", Architecture.Arm64)]
    [InlineData("app_AARCH64.tar.zip", Architecture.Arm64)]
    [InlineData("tool-winarm64.zip", Architecture.Arm64)]
    [InlineData("curl-win64a-mingw.zip", Architecture.Arm64)]
    [InlineData("app-win64-setup.exe", Architecture.X64)]
    [InlineData("app-x64.exe", Architecture.X64)]
    [InlineData("app-amd64.exe", Architecture.X64)]
    [InlineData("app_64bit.exe", Architecture.X64)]
    [InlineData("app-64-bit.exe", Architecture.X64)]
    [InlineData("tool_64.exe", Architecture.X64)]
    [InlineData("app-x86.exe", Architecture.X86)]
    [InlineData("app-win32.exe", Architecture.X86)]
    [InlineData("app-ia32.zip", Architecture.X86)]
    [InlineData("app-i386.exe", Architecture.X86)]
    [InlineData("app.386.exe", Architecture.X86)]
    [InlineData("app-686.exe", Architecture.X86)]
    [InlineData("app_32-bit.exe", Architecture.X86)]
    [InlineData("app_32bit.exe", Architecture.X86)]
    [InlineData("tool_32.exe", Architecture.X86)]
    [InlineData("app-arm.exe", Architecture.Arm)]
    public void Detector_and_classifier_agree_on_single_architecture_names(string name, Architecture expected)
    {
        Assert.Equal(expected, UrlArchitectureDetector.Detect(name));

        ArchitectureTokenEvidence evidence = ArchitectureTokenClassifier.Classify(name);
        Assert.Equal(expected, evidence.Architecture);
        Assert.False(evidence.IsAmbiguous);
    }

    [Theory]
    [InlineData("charm.exe")]
    [InlineData("x640.zip")]
    [InlineData("armory.exe")]
    [InlineData("64bits.zip")]
    [InlineData("i3860.zip")]
    [InlineData("app.exe")]
    public void Detector_and_classifier_agree_on_tokenless_names(string name)
    {
        Assert.Null(UrlArchitectureDetector.Detect(name));
        Assert.Null(ArchitectureTokenClassifier.Classify(name).Architecture);
    }
}
