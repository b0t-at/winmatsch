using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests;

/// <summary>
/// Malformed repository values from any configuration layer must stay inside the CLI
/// contract: exit code <see cref="ExitCodes.ConfigurationError"/>, one concise diagnostic on
/// standard error, no CLR stack trace, and nothing on standard output.
/// </summary>
public sealed class RepositoryInputContractTests
{
    public static TheoryData<string> MalformedRepositories() =>
        new(
            "winget-pkgs",
            "microsoft/winget-pkgs/extra",
            "/winget-pkgs",
            "microsoft/",
            "/");

    [Theory]
    [MemberData(nameof(MalformedRepositories))]
    [InlineData(" ")]
    public async Task Malformed_repo_option_is_a_configuration_error(string repository)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "--repo", repository]);

        AssertConfigurationErrorContract(result);
    }

    [Theory]
    [MemberData(nameof(MalformedRepositories))]
    public async Task Malformed_repository_environment_variable_is_a_configuration_error(
        string repository)
    {
        // Whitespace-only WINMATSCH_* variables are treated as unset by design, so the
        // environment layer is exercised with shaped-but-invalid values only.
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.EnvironmentVariables["WINMATSCH_REPOSITORY"] = repository;

        CliRunResult result = await harness.RunAsync(["probe"]);

        AssertConfigurationErrorContract(result);
    }

    [Theory]
    [MemberData(nameof(MalformedRepositories))]
    public async Task Malformed_repository_in_user_config_file_is_a_configuration_error(
        string repository)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.Files["repo.yaml"] = $"repository: \"{repository}\"";

        CliRunResult result = await harness.RunAsync(["probe", "--config", "repo.yaml"]);

        AssertConfigurationErrorContract(result);
    }

    [Fact]
    public async Task Malformed_repo_never_leaks_a_token_and_emits_no_json()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        const string secret = "ghp_s3cr3tT0kenValue1234";

        CliRunResult result = await harness.RunAsync(
            ["probe", "--repo", "bad", "--format", "json", "--token", secret]);

        AssertConfigurationErrorContract(result);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("{", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_padded_repo_option_is_trimmed_not_broken()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);

        CliRunResult result = await harness.RunAsync(["probe", "--repo", " microsoft/winget-pkgs "]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        Assert.Equal("microsoft/winget-pkgs", probe.LastContext.Configuration.Repository.ToString());
    }

    private static void AssertConfigurationErrorContract(CliRunResult result)
    {
        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Configuration error", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("owner/name", result.StandardError, StringComparison.Ordinal);
        // Concise: a single diagnostic line, never a CLR stack trace or exception dump.
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
    }
}
