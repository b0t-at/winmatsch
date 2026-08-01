using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class CompletionCommandTests
{
    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    public async Task Scripts_are_deterministic_and_side_effect_free(string shell)
    {
        CliHarness harness = CreateHarness();

        CliRunResult first = await harness.RunAsync(["completion", shell]);
        CliRunResult second = await harness.RunAsync(["completion", shell]);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal("", first.StandardError);
        Assert.NotEmpty(first.StandardOutput);
        // Zero side effects: no token store access, no prompts, no file writes.
        Assert.Null(harness.TokenStore.StoredToken);
        Assert.Empty(harness.Interaction.Questions);
        Assert.Empty(harness.Files);
        // No ANSI escapes on standard output.
        Assert.DoesNotContain("\u001b[", first.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bash_script_completes_commands_subcommands_and_options()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "bash"]);

        string script = result.StandardOutput;
        Assert.Contains("complete -F _winmatsch winmatsch", script, StringComparison.Ordinal);
        Assert.Contains("cache", script, StringComparison.Ordinal);
        Assert.Contains("clear inspect list prune", script, StringComparison.Ordinal);
        Assert.Contains("--yes", script, StringComparison.Ordinal);
        Assert.Contains("--token", script, StringComparison.Ordinal);
        Assert.Contains("--dry-run", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hidden_commands_never_appear_in_completions()
    {
        CliHarness harness = CreateHarness();

        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            CliRunResult result = await harness.RunAsync(["completion", shell]);
            Assert.DoesNotContain("remove-dead-versions", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Zsh_script_declares_compdef()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "zsh"]);

        Assert.StartsWith("#compdef winmatsch", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fish_script_uses_long_options()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "fish"]);

        Assert.Contains("complete -c winmatsch -f", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-l yes", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-l format", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "-a sync -d 'Synchronize the fork\\'s default branch",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShell_script_registers_a_native_completer()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "powershell"]);

        Assert.Contains(
            "Register-ArgumentCompleter -Native -CommandName 'winmatsch'",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("$tree['cache'] = @(", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_skip_leading_global_options_when_locating_the_command()
    {
        CliHarness harness = CreateHarness();

        CliRunResult bash = await harness.RunAsync(["completion", "bash"]);
        CliRunResult zsh = await harness.RunAsync(["completion", "zsh"]);
        CliRunResult powerShell = await harness.RunAsync(["completion", "powershell"]);

        Assert.Contains("local command=\"\" word option", bash.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("value_opts=", bash.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("local command=\"\" word option", zsh.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("$valueOptions", powerShell.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("$booleanOptions", powerShell.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("@('true', 'false')", powerShell.StandardOutput, StringComparison.Ordinal);
        CliRunResult fish = await harness.RunAsync(["completion", "fish"]);
        Assert.Contains("__winmatsch_command", fish.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("set -l value_opts", fish.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-l output -r", fish.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_never_embed_unsafe_characters_from_descriptions()
    {
        CliHarness harness = CreateHarness();

        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            CliRunResult result = await harness.RunAsync(["completion", shell]);
            Assert.DoesNotContain("$(", result.StandardOutput.Replace(
                "$(compgen", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("`", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Json_mode_wraps_the_script_in_one_document()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["completion", "bash", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith(
            "{\"schemaVersion\":\"1.0\",\"shell\":\"bash\",\"script\":\"",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_drops_names_with_shell_metacharacters()
    {
        Assert.Equal("sync", CompletionCommandModule.CompletionTree.Sanitize("sync"));
        Assert.Equal("--dry-run", CompletionCommandModule.CompletionTree.Sanitize("--dry-run"));
        Assert.Null(CompletionCommandModule.CompletionTree.Sanitize("evil;rm -rf"));
        Assert.Null(CompletionCommandModule.CompletionTree.Sanitize("$(cmd)"));
        Assert.Null(CompletionCommandModule.CompletionTree.Sanitize("a`b"));
        Assert.Null(CompletionCommandModule.CompletionTree.Sanitize("quote\"d"));
    }

    private static CliHarness CreateHarness()
    {
        CliHarness harness = MaintenanceParseAndHelpTests.CreateHarness();
        return harness;
    }
}
