using WinMatsch.Core;
using WinMatsch.Workflows.Mapping;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

public sealed class ArchitectureTokenClassifierTests
{
    [Theory]
    [InlineData("curl-win64a-mingw.zip", Architecture.Arm64)]
    [InlineData("tool-aarch64.exe", Architecture.Arm64)]
    [InlineData("tool-winarm64.zip", Architecture.Arm64)]
    [InlineData("electron-win32-arm64.zip", Architecture.Arm64)]
    [InlineData("electron-win32-x64.zip", Architecture.X64)]
    [InlineData("electron-win32-ia32.zip", Architecture.X86)]
    [InlineData("tool-x86_64.zip", Architecture.X64)]
    [InlineData("tool_64.exe", Architecture.X64)]
    [InlineData("tool_32.exe", Architecture.X86)]
    [InlineData("tool-arm.exe", Architecture.Arm)]
    public void Detects_bounded_most_specific_architecture(string name, Architecture expected)
    {
        ArchitectureTokenEvidence result = ArchitectureTokenClassifier.Classify(name);

        Assert.Equal(expected, result.Architecture);
        Assert.False(result.IsAmbiguous);
    }

    [Theory]
    [InlineData("charm.exe")]
    [InlineData("farm-tool.zip")]
    [InlineData("x640.exe")]
    [InlineData("amd640.zip")]
    [InlineData("i3860.zip")]
    [InlineData("64bits.zip")]
    [InlineData("winchester32.exe")]
    public void Hostile_unbounded_tokens_do_not_match(string name)
    {
        ArchitectureTokenEvidence result = ArchitectureTokenClassifier.Classify(name);

        Assert.Null(result.Architecture);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Conflicting_bounded_tokens_are_ambiguous()
    {
        ArchitectureTokenEvidence result = ArchitectureTokenClassifier.Classify("tool-x64-arm64.zip");

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Architecture);
        Assert.Equal(
            new[] { Architecture.X64, Architecture.Arm64 },
            result.Candidates.ToArray());
    }

    [Fact]
    public void Does_not_generate_neutral_architecture()
    {
        ArchitectureTokenEvidence result = ArchitectureTokenClassifier.Classify("UHK.Agent-5.0.0-win.exe");

        Assert.Null(result.Architecture);
        Assert.DoesNotContain(Architecture.Neutral, result.Candidates);
    }
}
