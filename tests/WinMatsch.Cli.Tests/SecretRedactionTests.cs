using WinMatsch.Cli.Tests.Harness;
using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class SecretRedactionTests
{
    private const string Secret = "ghp_s3cr3tT0kenValue1234";

    [Fact]
    public async Task Formatted_token_output_is_redacted()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            ResolvedToken resolved = await context.Tokens.RequireAsync(context.CancellationToken);
            context.Output.WriteResult($"token: {resolved.Token} from {resolved.Source}");
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe", "--token", Secret]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(GitHubToken.RedactedPlaceholder, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_errors_never_echo_the_token_value()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "--token", Secret, "--bogus"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_token_values_fail_without_being_echoed()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        const string invalidSecret = "bad secret with spaces";

        CliRunResult result = await harness.RunAsync(["probe", "--token", invalidSecret]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--token", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidSecret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidSecret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_resolution_prefers_option_then_environment_then_keyring()
    {
        var harness = new CliHarness();
        TokenSource? observedSource = null;
        harness.Modules.Add(new ProbeModule(async context =>
        {
            ResolvedToken resolved = await context.Tokens.RequireAsync(context.CancellationToken);
            observedSource = resolved.Source;
            return ExitCodes.Success;
        }));
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "env-token-value";
        harness.TokenStore.StoredToken = new GitHubToken("keyring-token-value");

        CliRunResult optionResult = await harness.RunAsync(["probe", "--token", Secret]);
        Assert.Equal(ExitCodes.Success, optionResult.ExitCode);
        Assert.Equal(TokenSource.ExplicitOption, observedSource);

        CliRunResult environmentResult = await harness.RunAsync(["probe"]);
        Assert.Equal(ExitCodes.Success, environmentResult.ExitCode);
        Assert.Equal(TokenSource.EnvironmentVariable, observedSource);

        harness.EnvironmentVariables.Remove("GITHUB_TOKEN");
        CliRunResult keyringResult = await harness.RunAsync(["probe"]);
        Assert.Equal(ExitCodes.Success, keyringResult.ExitCode);
        Assert.Equal(TokenSource.Keyring, observedSource);
    }

    [Fact]
    public async Task Missing_required_token_maps_to_missing_input_with_guidance()
    {
        var harness = new CliHarness();
        harness.TokenStore.StoredToken = null;
        harness.Modules.Add(new ProbeModule(async context =>
        {
            await context.Tokens.RequireAsync(context.CancellationToken);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("--token", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", result.StandardError, StringComparison.Ordinal);
    }
}
