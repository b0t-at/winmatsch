using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class ConfigCommandTests
{
    private static readonly string _defaultPath =
        Path.Combine("/home/tester", ".config", "winmatsch", "config.yaml");

    [Fact]
    public async Task Show_reports_default_provenance_when_nothing_is_set()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "show"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("repository = microsoft/winget-pkgs [default]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("concurrentDownloads = 2 [default]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("interaction = auto [default]", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("token", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Show_reports_command_environment_and_user_provenance()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.EnvironmentVariables["WINMATSCH_CONCURRENT_DOWNLOADS"] = "7";
        harness.Files[_defaultPath] = "interaction: \"never\"\n";

        CliRunResult result = await harness.RunAsync(["config", "show", "--repo", "contoso/pkgs"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("repository = contoso/pkgs [command]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("concurrentDownloads = 7 [environment]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("interaction = never [user]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_emits_stable_json()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "show", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith(
            "{\"schemaVersion\":\"1.0\",\"settings\":[{\"key\":\"repository\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("\"source\":\"default\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_writes_deterministic_yaml_the_parser_accepts()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult first = await harness.RunAsync(["config", "set", "repository", "contoso/pkgs"]);
        CliRunResult second = await harness.RunAsync(["config", "set", "cache.enabled", "false"]);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        string yaml = harness.Files[_defaultPath];
        Assert.Equal("repository: \"contoso/pkgs\"\ncache:\n  enabled: false\n", yaml);
        ConfigurationLayer parsed = ConfigurationYamlParser.Parse(yaml);
        Assert.Equal("contoso/pkgs", parsed.Repository);
        Assert.False(parsed.CacheEnabled);
    }

    [Fact]
    public async Task Set_preserves_value_casing_and_paths()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(
            ["config", "set", "cache.directory", "D:\\Cache Dir\\WinMatsch"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        ConfigurationLayer parsed = ConfigurationYamlParser.Parse(harness.Files[_defaultPath]);
        Assert.Equal("D:\\Cache Dir\\WinMatsch", parsed.CacheDirectory);
    }

    [Fact]
    public async Task Set_rejects_unknown_keys()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "set", "bogusKey", "1"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown configuration key", result.StandardError, StringComparison.Ordinal);
        Assert.False(harness.Files.ContainsKey(_defaultPath));
    }

    [Fact]
    public async Task Set_points_case_mismatches_at_the_exact_key()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "set", "Repository", "contoso/pkgs"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Did you mean 'repository'", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repository", "no-slash-here")]
    [InlineData("concurrentDownloads", "zero")]
    [InlineData("concurrentDownloads", "0")]
    [InlineData("cache.enabled", "maybe")]
    [InlineData("freshnessDelay", "-1.00:00:00")]
    [InlineData("output.format", "xml")]
    [InlineData("interaction", "sometimes")]
    public async Task Set_rejects_invalid_values(string key, string value)
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "set", key, value]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.False(harness.Files.ContainsKey(_defaultPath));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("github_token")]
    [InlineData("githubToken")]
    public async Task Set_never_stores_tokens(string key)
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "set", key, "ghp_secretValue123"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("token add", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secretValue123", result.StandardError, StringComparison.Ordinal);
        Assert.False(harness.Files.ContainsKey(_defaultPath));
    }

    [Fact]
    public async Task Set_failure_leaves_the_previous_file_intact()
    {
        (CliHarness harness, DictionaryConfigFileSystem fileSystem) = CreateHarness();
        harness.Files[_defaultPath] = "repository: \"contoso/pkgs\"\n";
        fileSystem.WriteFailure = new IOException("disk full");

        CliRunResult result = await harness.RunAsync(["config", "set", "interaction", "never"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("previous configuration is unchanged", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("repository: \"contoso/pkgs\"\n", harness.Files[_defaultPath]);
    }

    [Fact]
    public async Task Set_honors_the_explicit_config_option()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.Files["/etc/winmatsch.yaml"] = "interaction: \"never\"\n";

        CliRunResult result = await harness.RunAsync(
            ["config", "set", "repository", "contoso/pkgs", "--config", "/etc/winmatsch.yaml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            "repository: \"contoso/pkgs\"\ninteraction: \"never\"\n",
            harness.Files["/etc/winmatsch.yaml"]);
        Assert.False(harness.Files.ContainsKey(_defaultPath));
    }

    [Fact]
    public async Task Set_on_a_malformed_file_is_a_configuration_error()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.Files[_defaultPath] = "unknownKey: true\n";

        CliRunResult result = await harness.RunAsync(["config", "set", "interaction", "never"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Equal("unknownKey: true\n", harness.Files[_defaultPath]);
    }

    [Theory]
    [InlineData("# operator note\nrepository: \"contoso/pkgs\"\n")]
    [InlineData("repository: \"contoso/pkgs\" # keep this explanation\n")]
    public async Task Set_explicitly_refuses_to_discard_existing_comments(string yaml)
    {
        (CliHarness harness, DictionaryConfigFileSystem fileSystem) = CreateHarness();
        harness.Files[_defaultPath] = yaml;

        CliRunResult result = await harness.RunAsync(
            ["config", "set", "interaction", "never"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("contains comments", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("edit the file manually", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(yaml, harness.Files[_defaultPath]);
        Assert.Empty(fileSystem.Writes);
    }

    [Fact]
    public async Task Hash_inside_a_plain_scalar_is_not_treated_as_a_comment()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.Files[_defaultPath] = "cache:\n  directory: C:\\cache#v1\n";

        CliRunResult result = await harness.RunAsync(
            ["config", "set", "interaction", "never"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        ConfigurationLayer parsed = ConfigurationYamlParser.Parse(harness.Files[_defaultPath]);
        Assert.Equal(@"C:\cache#v1", parsed.CacheDirectory);
    }

    [Fact]
    public async Task Set_and_unset_dry_run_never_write()
    {
        (CliHarness harness, DictionaryConfigFileSystem fileSystem) = CreateHarness();
        harness.Files[_defaultPath] = "repository: \"contoso/pkgs\"\n";

        CliRunResult set = await harness.RunAsync(["config", "set", "interaction", "never", "--dry-run"]);
        CliRunResult unset = await harness.RunAsync(["config", "unset", "repository", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, set.ExitCode);
        Assert.Equal(ExitCodes.Success, unset.ExitCode);
        Assert.Contains("dry run", set.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("dry run", unset.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(fileSystem.Writes);
        Assert.Equal("repository: \"contoso/pkgs\"\n", harness.Files[_defaultPath]);
    }

    [Fact]
    public async Task Unset_removes_a_key_and_is_idempotent()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.Files[_defaultPath] = "repository: \"contoso/pkgs\"\ninteraction: \"never\"\n";

        CliRunResult first = await harness.RunAsync(["config", "unset", "repository"]);
        CliRunResult second = await harness.RunAsync(["config", "unset", "repository"]);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Equal("interaction: \"never\"\n", harness.Files[_defaultPath]);
    }

    [Fact]
    public async Task Unset_rejects_unknown_keys()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "unset", "bogus"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task Path_reports_the_default_location()
    {
        (CliHarness harness, _) = CreateHarness();

        CliRunResult result = await harness.RunAsync(["config", "path"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_defaultPath, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Source: default", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Exists: no", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Path_reports_the_explicit_location_as_json()
    {
        (CliHarness harness, _) = CreateHarness();
        harness.Files["/tmp/custom.yaml"] = "interaction: \"never\"\n";

        CliRunResult result = await harness.RunAsync(
            ["config", "path", "--config", "/tmp/custom.yaml", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"path\":\"/tmp/custom.yaml\","
            + "\"source\":\"explicit\",\"exists\":true}\n",
            result.StandardOutput);
    }

    [Fact]
    public void Deterministic_serialization_orders_all_keys_stably()
    {
        var layer = new ConfigurationLayer
        {
            Repository = "contoso/pkgs",
            ConcurrentDownloads = 3,
            EnabledRules = ["rule-b", "rule-a"],
            DisabledRules = ["rule-c"],
            CacheEnabled = true,
            CacheDirectory = "/var/cache",
            FreshnessDelay = TimeSpan.FromDays(2),
            OutputFormat = OutputFormat.Json,
            OutputDirectory = "/out",
            Interaction = InteractionMode.Never,
        };

        string yaml = ConfigCommandModule.SerializeDeterministic(layer);

        Assert.Equal(
            "repository: \"contoso/pkgs\"\n"
            + "concurrentDownloads: 3\n"
            + "rules:\n"
            + "  enabled:\n"
            + "    - \"rule-b\"\n"
            + "    - \"rule-a\"\n"
            + "  disabled:\n"
            + "    - \"rule-c\"\n"
            + "cache:\n"
            + "  enabled: true\n"
            + "  directory: \"/var/cache\"\n"
            + "freshnessDelay: \"2.00:00:00\"\n"
            + "output:\n"
            + "  format: \"json\"\n"
            + "  directory: \"/out\"\n"
            + "interaction: \"never\"\n",
            yaml);
        ConfigurationLayer parsed = ConfigurationYamlParser.Parse(yaml);
        Assert.Equal(layer.Repository, parsed.Repository);
        Assert.Equal(layer.FreshnessDelay, parsed.FreshnessDelay);
        Assert.Equal(layer.EnabledRules, parsed.EnabledRules);
    }

    private static (CliHarness Harness, DictionaryConfigFileSystem FileSystem) CreateHarness()
    {
        var harness = new CliHarness();
        var fileSystem = new DictionaryConfigFileSystem(harness.Files);
        harness.Modules.Add(new ConfigCommandModule(
            environment: name => harness.EnvironmentVariables.GetValueOrDefault(name),
            homeDirectory: harness.HomeDirectory,
            fileSystem: fileSystem));
        return (harness, fileSystem);
    }
}
