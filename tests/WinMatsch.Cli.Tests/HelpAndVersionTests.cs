using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class HelpAndVersionTests
{
    [Fact]
    public async Task Help_prints_usage_and_global_options_on_stdout()
    {
        var harness = new CliHarness();

        CliRunResult result = await harness.RunAsync(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--repo", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--dry-run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--format", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--interaction", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--token", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Bare_invocation_prints_help_and_succeeds()
    {
        var harness = new CliHarness();

        CliRunResult result = await harness.RunAsync([]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Help_lists_registered_module_commands()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("probe", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_prints_the_cli_informational_version_on_stdout()
    {
        var harness = new CliHarness();

        CliRunResult result = await harness.RunAsync(["--version"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(CliVersion.InformationalVersion + "\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }
}
