using WinMatsch.Cli.Output;
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

    [Theory]
    [InlineData("opaque-value-that-is-not-a-known-secret-shape")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("github_pat_11AA0abcdefghijklmnopqrstuv_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData(
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
        + "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ."
        + "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    public async Task Real_secret_shapes_are_redacted_from_host_errors(string secret)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(["probe", "--tokn", secret]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presigned_url_values_are_redacted_from_json_strings()
    {
        const string url =
            "https://bucket.example.test/object?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=AKIAEXAMPLE%2F20260801%2Fregion%2Fs3%2Faws4_request"
            + "&X-Amz-Signature=0123456789abcdef";
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteJsonResult(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("url", url);
                writer.WriteEndObject();
            });
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("AKIAEXAMPLE", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"access_token\":\"oauth-secret\"}", "oauth-secret")]
    [InlineData("{\\\"access_token\\\":\\\"oauth-secret\\\"}", "oauth-secret")]
    [InlineData("GITHUB_TOKEN=opaque-secret", "opaque-secret")]
    public void Json_and_namespaced_secret_assignments_are_redacted(
        string input,
        string secret)
    {
        string result = CliRedactor.Redact(input);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains(CliRedactor.Placeholder, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Benign_query_values_are_preserved()
    {
        const string url = "https://example.test/packages?page=2&sort=name";

        Assert.Equal(url, CliRedactor.Redact(url));
    }

    [Fact]
    public void Percent_encoded_sensitive_query_keys_are_redacted()
    {
        const string url = "https://example.test/packages?%74oken=opaque-secret&page=2";

        string result = CliRedactor.Redact(url);

        Assert.DoesNotContain("opaque-secret", result, StringComparison.Ordinal);
        Assert.Contains("page=2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_url_mode_redacts_every_query_value()
    {
        const string url =
            "https://example.test/download?auth=opaque-download-credential&page=2";

        string result = CliRedactor.RedactUrl(url, redactAllQueryValues: true);

        Assert.DoesNotContain("opaque-download-credential", result, StringComparison.Ordinal);
        Assert.DoesNotContain("page=2", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_secret_properties_are_redacted_structurally()
    {
        const string opaque = "short-opaque-value";
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteJsonResult(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("access_token", opaque);
                writer.WriteEndObject();
            });
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json"]);

        Assert.DoesNotContain(opaque, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(CliRedactor.Placeholder, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_assignment_spanning_chunk_boundary_is_redacted()
    {
        string value = new string('a', 32 * 1024 - 6)
            + " token"
            + new string(' ', 20 * 1024)
            + "=opaque-window-secret";

        string result = CliRedactor.Redact(value);

        Assert.DoesNotContain("opaque-window-secret", result, StringComparison.Ordinal);
        Assert.Contains(CliRedactor.Placeholder, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_quotes_do_not_expose_secret_suffixes()
    {
        const string value = "token=\"alpha\\\"omega\"";

        string result = CliRedactor.Redact(value);

        Assert.DoesNotContain("alpha", result, StringComparison.Ordinal);
        Assert.DoesNotContain("omega", result, StringComparison.Ordinal);
        Assert.Contains(CliRedactor.Placeholder, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft.VisualStudioCode.Insiders")]
    [InlineData("MongoDB.Compass.Community")]
    [InlineData("Contoso.One.Two")]
    public void Package_identifiers_are_not_mistaken_for_jwts(string packageIdentifier)
    {
        Assert.Equal(packageIdentifier, CliRedactor.Redact(packageIdentifier));
    }

    [Fact]
    public void Large_nonsecret_output_is_not_truncated()
    {
        string value = new('a', 4 * 1024 * 1024);

        string redacted = CliRedactor.Redact(value);

        Assert.Equal(value, redacted);
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
    public async Task Malformed_github_token_environment_variable_is_a_configuration_error()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            await context.Tokens.RequireAsync(context.CancellationToken);
            return ExitCodes.Success;
        }));
        const string brokenSecret = "ghp_broken token\twith whitespace";
        harness.EnvironmentVariables["GITHUB_TOKEN"] = brokenSecret;

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("GITHUB_TOKEN", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(brokenSecret, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_broken", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
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

    [Fact]
    public async Task Required_token_keyring_failure_is_an_operation_failure()
    {
        var harness = new CliHarness();
        harness.TokenStore.GetFailure = new IOException("secret-tool could not start");
        harness.Modules.Add(new ProbeModule(async context =>
        {
            _ = await context.Tokens.RequireAsync(context.CancellationToken);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("keyring", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unexpected error", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_stored_token_is_normalized_as_a_keyring_failure()
    {
        var harness = new CliHarness();
        harness.TokenStore.GetFailure = new ArgumentException("stored token is corrupt");
        harness.Modules.Add(new ProbeModule(async context =>
        {
            _ = await context.Tokens.RequireAsync(context.CancellationToken);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("keyring", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unexpected error", result.StandardError, StringComparison.Ordinal);
    }
}
