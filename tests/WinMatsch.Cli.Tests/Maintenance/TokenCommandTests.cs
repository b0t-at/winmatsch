using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class TokenCommandTests
{
    private const string Secret = "ghp_maintenanceS3cretValue42";

    [Fact]
    public async Task Add_validates_and_stores_a_token_from_stdin()
    {
        var harness = new CliHarness();
        var validator = new RecordingValidator(TokenValidationResult.Valid("octocat", ["repo"]));
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            validator,
            () => new StringReader(Secret + "\n")));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(harness.TokenStore.StoredToken);
        Assert.Equal(Secret, harness.TokenStore.StoredToken!.RevealValue());
        Assert.Contains("octocat", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task Add_accepts_the_global_token_option()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Valid("octocat"))));

        CliRunResult result = await harness.RunAsync(["token", "add", "--token", Secret]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(Secret, harness.TokenStore.StoredToken!.RevealValue());
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_rejects_stdin_and_option_together()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Valid("octocat")),
            () => new StringReader(Secret)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin", "--token", Secret]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_without_a_source_is_missing_input()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Valid("octocat"))));

        CliRunResult result = await harness.RunAsync(["token", "add"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("--stdin", result.StandardError, StringComparison.Ordinal);
        Assert.Null(harness.TokenStore.StoredToken);
    }

    [Fact]
    public async Task Add_does_not_store_an_invalid_token_and_never_echoes_it()
    {
        var harness = new CliHarness();
        var validator = new RecordingValidator(TokenValidationResult.Invalid("GitHub rejected the token as unauthorized."));
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            validator,
            () => new StringReader(Secret)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.Contains("unauthorized", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_rejects_malformed_stdin_tokens_without_echoing()
    {
        var harness = new CliHarness();
        const string malformed = "bad token with spaces";
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Valid("octocat")),
            () => new StringReader(malformed)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.DoesNotContain(malformed, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_reports_an_unavailable_keyring_actionably()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(
            new FakeTokenStore { IsAvailable = false },
            new RecordingValidator(TokenValidationResult.Valid("octocat")),
            () => new StringReader(Secret)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("GITHUB_TOKEN", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_dry_run_validates_but_never_stores()
    {
        var harness = new CliHarness();
        var validator = new RecordingValidator(TokenValidationResult.Valid("octocat"));
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            validator,
            () => new StringReader(Secret)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.Equal(1, validator.Calls);
        Assert.Contains("dry run", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_dry_run_keeps_the_stored_token()
    {
        var harness = new CliHarness();
        harness.TokenStore.StoredToken = new GitHubToken(Secret);
        harness.Modules.Add(new TokenCommandModule(harness.TokenStore));

        CliRunResult result = await harness.RunAsync(["token", "remove", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(harness.TokenStore.StoredToken);
        Assert.Contains("Would remove", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_is_idempotent()
    {
        var harness = new CliHarness();
        harness.TokenStore.StoredToken = new GitHubToken(Secret);
        harness.Modules.Add(new TokenCommandModule(harness.TokenStore));

        CliRunResult first = await harness.RunAsync(["token", "remove"]);
        CliRunResult second = await harness.RunAsync(["token", "remove"]);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Contains("Removed", first.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Contains("nothing to remove", second.StandardOutput, StringComparison.Ordinal);
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.DoesNotContain(Secret, first.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_redacted_store_state()
    {
        var harness = new CliHarness();
        harness.TokenStore.StoredToken = new GitHubToken(Secret);
        harness.Modules.Add(new TokenCommandModule(harness.TokenStore));

        CliRunResult result = await harness.RunAsync(["token", "status"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("stored", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_handles_an_unavailable_keyring()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(new FakeTokenStore { IsAvailable = false }));

        CliRunResult result = await harness.RunAsync(["token", "status"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Available: no", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_outputs_never_contain_the_secret()
    {
        var harness = new CliHarness();
        harness.TokenStore.StoredToken = new GitHubToken(Secret);
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Valid("octocat", ["repo"])),
            () => new StringReader(Secret)));

        CliRunResult add = await harness.RunAsync(["token", "add", "--stdin", "--format", "json"]);
        CliRunResult status = await harness.RunAsync(["token", "status", "--format", "json"]);
        CliRunResult remove = await harness.RunAsync(["token", "remove", "--format", "json"]);

        foreach (CliRunResult result in new[] { add, status, remove })
        {
            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.StartsWith("{", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
        }

        Assert.Contains("\"hasToken\":true", status.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"removed\":true", remove.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_error_paths_never_contain_the_secret()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new TokenCommandModule(
            harness.TokenStore,
            new RecordingValidator(TokenValidationResult.Invalid("validation failed.")),
            () => new StringReader(Secret)));

        CliRunResult result = await harness.RunAsync(["token", "add", "--stdin", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.DoesNotContain(Secret, result.StandardError, StringComparison.Ordinal);
    }

    private sealed class RecordingValidator : ITokenValidator
    {
        private readonly TokenValidationResult _result;

        public RecordingValidator(TokenValidationResult result)
        {
            _result = result;
        }

        public int Calls { get; private set; }

        public Task<TokenValidationResult> ValidateAsync(
            GitHubToken token,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
