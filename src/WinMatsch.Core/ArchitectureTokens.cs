using System.Collections.Immutable;

namespace WinMatsch.Core;

/// <summary>
/// A filename/URL architecture token. A token only counts when bounded by non-alphanumeric
/// characters or the string edges; a <see cref="SuffixToken"/> starts with its own separator
/// (e.g. "_64") and therefore only requires the trailing boundary.
/// </summary>
public sealed record ArchitectureTokenDefinition(
    Architecture Architecture,
    string Token,
    int Priority,
    bool SuffixToken = false);

/// <summary>
/// Single source of truth for filename/URL architecture tokens. Consumed by both
/// WinMatsch.Analysis.UrlArchitectureDetector and WinMatsch.Workflows.Mapping.
/// ArchitectureTokenClassifier so the analysis and mapping sides always classify the same
/// name identically and never manufacture an architecture conflict between each other.
/// </summary>
public static class ArchitectureTokens
{
    /// <summary>All known tokens; higher <see cref="ArchitectureTokenDefinition.Priority"/> wins over contained matches.</summary>
    public static ImmutableArray<ArchitectureTokenDefinition> All { get; } =
    [
        new(Architecture.Arm64, "winarm64", 400),
        new(Architecture.Arm64, "win64a", 400),
        new(Architecture.Arm64, "aarch64", 400),
        new(Architecture.Arm64, "arm64", 400),
        new(Architecture.X64, "x86_64", 300),
        new(Architecture.X64, "x86-64", 300),
        new(Architecture.X64, "64-bit", 300),
        new(Architecture.X64, "amd64", 300),
        new(Architecture.X64, "win64", 300),
        new(Architecture.X64, "x64", 300),
        new(Architecture.X64, "64bit", 300),
        new(Architecture.X64, "_64", 300, SuffixToken: true),
        new(Architecture.X86, "32-bit", 200),
        new(Architecture.X86, "win32", 200),
        new(Architecture.X86, "ia32", 200),
        new(Architecture.X86, "i386", 200),
        new(Architecture.X86, "i686", 200),
        new(Architecture.X86, "x86", 200),
        new(Architecture.X86, "386", 200),
        new(Architecture.X86, "686", 200),
        new(Architecture.X86, "32bit", 200),
        new(Architecture.X86, "_32", 200, SuffixToken: true),
        new(Architecture.Arm, "arm", 100),
    ];

    /// <summary>Returns the tokens mapped to <paramref name="architecture"/>.</summary>
    public static ImmutableArray<ArchitectureTokenDefinition> For(Architecture architecture)
        => [.. All.Where(token => token.Architecture == architecture)];
}
