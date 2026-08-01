using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

/// <summary>
/// Parse and help coverage for every maintenance command and subcommand: each renders help,
/// rejects unknown options, and the hidden command stays out of the root help.
/// </summary>
public sealed class MaintenanceParseAndHelpTests
{
    public static TheoryData<string> HelpInvocations => new()
    {
        "sync --help",
        "cleanup --help",
        "complete --help",
        "token --help",
        "token add --help",
        "token remove --help",
        "token status --help",
        "config --help",
        "config show --help",
        "config set --help",
        "config unset --help",
        "config path --help",
        "cache --help",
        "cache list --help",
        "cache inspect --help",
        "cache clear --help",
        "cache prune --help",
        "completion --help",
    };
    [Theory]
    [MemberData(nameof(HelpInvocations))]
    public async Task Every_command_renders_help(string invocation)
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(invocation.Split(' '));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("", result.StandardError);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("cleanup")]
    [InlineData("complete")]
    [InlineData("token")]
    [InlineData("config")]
    [InlineData("cache")]
    [InlineData("completion")]
    public async Task Root_help_lists_the_visible_commands(string command)
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_help_hides_remove_dead_versions()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["--help"]);

        Assert.DoesNotContain("remove-dead-versions", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_dead_versions_still_parses_and_requires_arguments()
    {
        CliHarness harness = CreateHarness();

        CliRunResult help = await harness.RunAsync(["remove-dead-versions", "--help"]);
        CliRunResult missing = await harness.RunAsync(["remove-dead-versions"]);

        // Hidden commands render no help body, but they parse and enforce their arguments.
        Assert.Equal(ExitCodes.Success, help.ExitCode);
        Assert.Equal(ExitCodes.UsageError, missing.ExitCode);
        Assert.Contains("Required", missing.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            missing.StandardError.Split('\n').Count(line =>
                line.Contains("Required", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("sync", "--bogus")]
    [InlineData("token", "add", "--bogus")]
    [InlineData("config", "set", "--bogus")]
    [InlineData("cache", "clear", "--bogus")]
    [InlineData("completion", "bash", "--bogus")]
    public async Task Unknown_options_are_usage_errors(params string[] args)
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(args);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.NotEqual("", result.StandardError);
    }

    [Fact]
    public async Task Completion_rejects_unknown_shells()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "tcsh"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task Token_without_subcommand_is_a_usage_error()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["token"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task Sync_rejects_malformed_fork_values()
    {
        CliHarness harness = CreateHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token-value";

        CliRunResult result = await harness.RunAsync(["sync", "--fork", "not-a-repo"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--fork", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_dead_versions_rejects_invalid_identifiers()
    {
        CliHarness harness = CreateHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token-value";

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", "..", "1.0.0"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("package identifier", result.StandardError, StringComparison.Ordinal);
    }

    internal static CliHarness CreateHarness(
        FakeMaintenanceGitHubClient? client = null,
        FakeDeadVersionInspector? inspector = null)
    {
        var harness = new CliHarness();
        FakeMaintenanceGitHubClient gitHub = client ?? new FakeMaintenanceGitHubClient();
        harness.Modules.Add(new MaintenanceCommandModule(
            clientFactory: _ => gitHub,
            inspectorFactory: inspector is null ? null : _ => inspector));
        harness.Modules.Add(new TokenCommandModule(
            store: harness.TokenStore,
            validator: new StubTokenValidator(),
            standardInput: static () => TextReader.Null));
        harness.Modules.Add(new ConfigCommandModule(
            environment: name => harness.EnvironmentVariables.GetValueOrDefault(name),
            homeDirectory: harness.HomeDirectory,
            fileSystem: new DictionaryConfigFileSystem(harness.Files)));
        harness.Modules.Add(new CacheCommandModule());
        harness.Modules.Add(new CompletionCommandModule());
        return harness;
    }

    private sealed class StubTokenValidator : WinMatsch.GitHub.Auth.ITokenValidator
    {
        public Task<WinMatsch.GitHub.Auth.TokenValidationResult> ValidateAsync(
            WinMatsch.GitHub.Auth.GitHubToken token,
            CancellationToken cancellationToken = default)
            => Task.FromResult(WinMatsch.GitHub.Auth.TokenValidationResult.Valid("octocat"));
    }
}

/// <summary>An in-memory <see cref="IConfigFileSystem"/> over the harness's fake file map.</summary>
internal sealed class DictionaryConfigFileSystem : IConfigFileSystem
{
    private readonly Dictionary<string, string> _files;

    public DictionaryConfigFileSystem(Dictionary<string, string> files)
    {
        _files = files;
    }

    /// <summary>When set, every write throws to exercise the atomic-failure path.</summary>
    public Exception? WriteFailure { get; set; }

    public List<string> Writes { get; } = [];

    public string? ReadText(string path) => _files.GetValueOrDefault(path);

    public void WriteTextAtomic(string path, string content)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Writes.Add(path);
        _files[path] = content;
    }
}
