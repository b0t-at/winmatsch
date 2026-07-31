using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.Cli.Hosting;

namespace WinMatsch.Cli;

/// <summary>
/// The executable entry point. All behavior lives in <see cref="CliHost"/> so tests can run
/// the identical composition fully in process. Ctrl+C is translated by System.CommandLine
/// into the invocation's cancellation token, which the host maps to
/// <see cref="ExitCodes.Cancelled"/>.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args) =>
        CliHost.CreateDefault([new DiagnosticsCommandModule()]).RunAsync(args);
}
