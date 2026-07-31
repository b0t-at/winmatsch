using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class ExecutionContractTests
{
    [Fact]
    public async Task Cancellation_returns_130_and_reports_on_stderr()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return ExitCodes.Success;
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        CliRunResult result = await harness.RunAsync(["probe"], cancellation.Token);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_exceptions_map_to_exit_code_2()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ =>
            throw new CliUsageException("Pass exactly one of --urls or --manifest.")));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--urls", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_input_exceptions_map_to_exit_code_4()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ =>
            throw new MissingInputException("A package identifier is required.")));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("package identifier", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operation_exceptions_map_to_exit_code_5()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ =>
            throw new CliOperationException("The remote rejected the submission.")));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("rejected", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_exceptions_map_to_exit_code_1()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ =>
            throw new InvalidOperationException("boom")));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.UnexpectedError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Unexpected error", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("boom", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_exit_codes_pass_through_unchanged()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ => Task.FromResult(ExitCodes.OperationFailed)));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
    }
}
