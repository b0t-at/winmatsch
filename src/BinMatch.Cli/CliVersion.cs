using System.Reflection;

namespace BinMatch.Cli;

/// <summary>The version the CLI reports for <c>--version</c>.</summary>
public static class CliVersion
{
    /// <summary>The informational version of the binmatch CLI assembly.</summary>
    public static string InformationalVersion { get; } =
        typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>The package version without build metadata.</summary>
    public static string PackageVersion { get; } =
        InformationalVersion.Split('+', 2, StringSplitOptions.None)[0];
}
