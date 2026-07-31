using System.Reflection;

namespace WinMatsch.Cli;

/// <summary>The version the CLI reports for <c>--version</c>.</summary>
public static class CliVersion
{
    /// <summary>The informational version of the winmatsch CLI assembly.</summary>
    public static string InformationalVersion { get; } =
        typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
