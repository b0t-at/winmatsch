using System.CommandLine;
using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.Cli.Hosting;
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
    public async Task Built_in_short_help_alias_remains_supported()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "-h"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("true", WorkflowExecutionMode.Plan)]
    [InlineData("1", WorkflowExecutionMode.Plan)]
    [InlineData("false", WorkflowExecutionMode.Apply)]
    [InlineData("0", WorkflowExecutionMode.Apply)]
    public async Task Dry_run_environment_fallback_is_supported(
        string value,
        WorkflowExecutionMode expected)
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["DRY_RUN"] = value;

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(expected, probe.LastContext!.ExecutionMode);
    }

    [Fact]
    public async Task Dry_run_flag_takes_precedence_over_false_environment_value()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["DRY_RUN"] = "false";

        CliRunResult result = await harness.RunAsync(["probe", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(WorkflowExecutionMode.Plan, probe.LastContext!.ExecutionMode);
    }

    [Theory]
    [InlineData("--dry-run=false")]
    [InlineData("--dry-run", "false")]
    public async Task Explicit_false_dry_run_value_selects_apply(params string[] option)
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["DRY_RUN"] = "true";

        CliRunResult result = await harness.RunAsync(["probe", .. option]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(WorkflowExecutionMode.Apply, probe.LastContext!.ExecutionMode);
    }

    [Fact]
    public async Task Dry_run_operand_after_end_of_options_cannot_disable_environment_safety()
    {
        var harness = new CliHarness();
        var probe = new OperandProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["DRY_RUN"] = "true";

        CliRunResult result = await harness.RunAsync(
            ["operand-probe", "--", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(WorkflowExecutionMode.Plan, probe.Context!.ExecutionMode);
        Assert.Equal(["--dry-run"], probe.Operands);
    }

    [Fact]
    public async Task Dash_prefixed_known_option_value_is_not_reported_or_echoed()
    {
        const string opaque = "-opaque-secret";
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(
            ["probe", "--token", opaque, "--bogus"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--bogus", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(opaque, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_dry_run_environment_value_is_a_configuration_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.EnvironmentVariables["DRY_RUN"] = "sometimes";

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Contains("DRY_RUN", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Greedy_manifest_paths_do_not_swallow_unknown_options()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new DiagnosticsCommandModule(
            new FakeInstallerDiagnosticService(),
            new FakeManifestValidationService()));

        CliRunResult result = await harness.RunAsync(["validate", "--network"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unrecognized option '--network'", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Manifest path", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_of_options_allows_an_option_shaped_manifest_path()
    {
        var validation = new FakeManifestValidationService();
        var harness = new CliHarness();
        harness.Modules.Add(new DiagnosticsCommandModule(
            new FakeInstallerDiagnosticService(),
            validation));

        CliRunResult result = await harness.RunAsync(["validate", "--", "--manifest.yaml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(["--manifest.yaml"], validation.LastRequest!.Paths);
    }

    [Fact]
    public async Task Ghes_api_option_derives_same_host_graphql_endpoint()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);

        CliRunResult result = await harness.RunAsync(
            ["probe", "--github-api-url", "https://ghe.example.test/api/v3/"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            "https://ghe.example.test/api/v3/",
            probe.LastContext!.GitHubOptions.ApiBaseUri.AbsoluteUri);
        Assert.Equal(
            "https://ghe.example.test/api/graphql",
            probe.LastContext.GitHubOptions.GraphQlUri.AbsoluteUri);
    }

    [Fact]
    public async Task Ghes_rest_and_graphql_authorities_must_match()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(
        [
            "probe",
            "--github-api-url",
            "https://ghe.example.test/api/v3/",
            "--github-graphql-url",
            "https://api.github.com/graphql",
        ]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Contains("same scheme, host, and port", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_github_endpoints_require_https()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(
            ["probe", "--github-api-url", "http://ghe.example.test/api/v3/"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("requires an absolute HTTPS URL", result.StandardError, StringComparison.Ordinal);
    }

    private sealed class OperandProbeModule : ICommandModule
    {
        public string Name => "operand-probe";

        public CommandContext? Context { get; private set; }

        public string[] Operands { get; private set; } = [];

        public void RegisterCommands(ICommandRegistry registry)
        {
            var operands = new Argument<string[]>("operands")
            {
                Arity = ArgumentArity.ZeroOrMore,
            };
            var command = new Command("operand-probe")
            {
                Arguments = { operands },
            };
            registry.AddCommand(command);
            registry.SetHandler(command, context =>
            {
                Context = context;
                Operands = context.ParseResult.GetValue(operands) ?? [];
                return Task.FromResult(ExitCodes.Success);
            });
        }
    }
}
