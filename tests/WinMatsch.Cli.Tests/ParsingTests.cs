using WinMatsch.Cli.Tests.Harness;
using WinMatsch.GitHub;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class ParsingTests
{
    [Fact]
    public async Task Unknown_option_fails_with_usage_error_on_stderr()
    {
        var harness = new CliHarness();

        CliRunResult result = await harness.RunAsync(["--nonsense"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--nonsense", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--help", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_command_fails_with_usage_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["frobnicate"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("frobnicate", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--format", "yaml", "output format")]
    [InlineData("--interaction", "sometimes", "interaction mode")]
    public async Task Invalid_enum_option_value_fails_with_usage_error(
        string option,
        string value,
        string expectedPhrase)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", option, value]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains(expectedPhrase, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_numeric_concurrent_downloads_fails_with_usage_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "--concurrent-downloads", "many"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task Global_options_bind_into_the_command_context()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);

        CliRunResult result = await harness.RunAsync(
        [
            "probe",
            "--repo", "cli/repo",
            "--format", "json",
            "--output", "outdir",
            "--concurrent-downloads", "5",
            "--dry-run",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        Assert.Equal(RepositoryCoordinates.Parse("cli/repo"), probe.LastContext.Configuration.Repository);
        Assert.Equal(OutputFormat.Json, probe.LastContext.Configuration.OutputFormat);
        Assert.Equal("outdir", probe.LastContext.Configuration.OutputDirectory);
        Assert.Equal(5, probe.LastContext.Configuration.ConcurrentDownloads);
        Assert.Equal(WorkflowExecutionMode.Plan, probe.LastContext.ExecutionMode);
        Assert.True(probe.LastContext.IsDryRun);
    }

    [Fact]
    public async Task Without_dry_run_the_execution_mode_is_apply()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        Assert.Equal(WorkflowExecutionMode.Apply, probe.LastContext.ExecutionMode);
        Assert.False(probe.LastContext.IsDryRun);
    }
}
