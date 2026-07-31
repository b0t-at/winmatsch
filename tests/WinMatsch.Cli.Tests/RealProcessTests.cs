using WinMatsch.Testing.Infrastructure;
using Xunit;

namespace WinMatsch.Cli.Tests;

/// <summary>
/// End-to-end checks of the real executable boundary: the published entry point running in a
/// separate process must honor the same exit-code and stream contract as the in-process host.
/// The CLI assembly copied next to the tests is launched through the dotnet muxer.
/// </summary>
public sealed class RealProcessTests
{
    [Fact]
    public async Task Version_succeeds_with_only_the_version_on_stdout()
    {
        ProcessResult result = await RunCliAsync("--version");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            CliVersion.InformationalVersion,
            result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Unknown_option_exits_2_with_concise_diagnostics_and_no_stack_trace()
    {
        ProcessResult result = await RunCliAsync("--nonsense");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--nonsense", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        string cliAssembly = Path.Combine(AppContext.BaseDirectory, "WinMatsch.Cli.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly not found at '{cliAssembly}'.");

        var runner = new PhysicalProcessRunner();
        return await runner.RunAsync(new ProcessRequest
        {
            FileName = FindDotnetMuxer(),
            Arguments = [cliAssembly, .. args],
        });
    }

    private static string FindDotnetMuxer()
    {
        // The test host itself runs on the muxer; reuse it when possible so the test never
        // depends on PATH. Fall back to DOTNET_ROOT, then to PATH resolution.
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            string candidate = Path.Combine(
                dotnetRoot,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }
}
