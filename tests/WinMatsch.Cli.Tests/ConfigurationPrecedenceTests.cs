using WinMatsch.Cli.Tests.Harness;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class ConfigurationPrecedenceTests
{
    [Fact]
    public async Task Command_line_beats_environment_beats_file_beats_defaults()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["WINMATSCH_REPOSITORY"] = "env/repo";
        harness.EnvironmentVariables["WINMATSCH_OUTPUT_FORMAT"] = "json";
        harness.Files[DefaultConfigPath(harness)] =
            """
            repository: file/repo
            concurrentDownloads: 7
            """;

        CliRunResult result = await harness.RunAsync(["probe", "--repo", "cli/repo"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        WinMatschConfiguration configuration = probe.LastContext.Configuration;
        // Command layer wins over environment and file.
        Assert.Equal(RepositoryCoordinates.Parse("cli/repo"), configuration.Repository);
        // Environment wins where the command is silent.
        Assert.Equal(OutputFormat.Json, configuration.OutputFormat);
        // File wins where command and environment are silent.
        Assert.Equal(7, configuration.ConcurrentDownloads);
        // Built-in default where every layer is silent.
        Assert.Equal(InteractionMode.Auto, configuration.Interaction);
    }

    [Fact]
    public async Task Invalid_environment_variable_is_a_configuration_error_naming_the_variable()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.EnvironmentVariables["WINMATSCH_CONCURRENT_DOWNLOADS"] = "several";

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("WINMATSCH_CONCURRENT_DOWNLOADS", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_explicit_config_file_is_a_configuration_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "--config", "missing.yaml"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Contains("missing.yaml", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_config_file_is_a_configuration_error_naming_the_path()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.Files["bad.yaml"] = "concurrentDownloads: lots";

        CliRunResult result = await harness.RunAsync(["probe", "--config", "bad.yaml"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Contains("bad.yaml", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_default_config_file_is_not_an_error()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        Assert.Equal(
            RepositoryCoordinates.Parse(ConfigurationDefaults.Repository),
            probe.LastContext.Configuration.Repository);
    }

    [Fact]
    public async Task Xdg_config_home_overrides_the_default_config_location()
    {
        var harness = new CliHarness();
        var probe = new ProbeModule();
        harness.Modules.Add(probe);
        harness.EnvironmentVariables["XDG_CONFIG_HOME"] = "xdg";
        harness.Files[Path.Combine("xdg", "winmatsch", "config.yaml")] = "repository: xdg/repo";

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(probe.LastContext);
        Assert.Equal(
            RepositoryCoordinates.Parse("xdg/repo"),
            probe.LastContext.Configuration.Repository);
    }

    [Fact]
    public async Task Unreadable_config_file_is_a_configuration_error_not_a_crash()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.FileReadErrors["locked.yaml"] = new IOException("The file is locked by another process.");

        CliRunResult result = await harness.RunAsync(["probe", "--config", "locked.yaml"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("locked", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_denied_config_file_is_a_configuration_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.FileReadErrors["secret.yaml"] = new UnauthorizedAccessException("Access denied.");

        CliRunResult result = await harness.RunAsync(["probe", "--config", "secret.yaml"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Contains("Access denied", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unclassified_context_creation_failure_maps_to_exit_code_1_not_a_crash()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.FileReadErrors["odd.yaml"] = new InvalidOperationException("boom");

        CliRunResult result = await harness.RunAsync(["probe", "--config", "odd.yaml"]);

        Assert.Equal(ExitCodes.UnexpectedError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Unexpected error", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    private static string DefaultConfigPath(CliHarness harness) =>
        Path.Combine(harness.HomeDirectory!, ".config", "winmatsch", "config.yaml");
}
