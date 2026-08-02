using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class InteractionContractTests
{
    private static ProbeModule PromptingProbe() =>
        new(async context =>
        {
            string answer = await context.Interaction.AskAsync("Package identifier?");
            context.Output.WriteResult(answer);
            return ExitCodes.Success;
        });

    [Fact]
    public async Task Json_output_never_prompts_and_fails_with_missing_input()
    {
        var harness = new CliHarness();
        harness.Modules.Add(PromptingProbe());
        harness.Interaction.EnqueueText("should-not-be-used");

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("JSON output", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Interaction_never_fails_with_missing_input_instead_of_prompting()
    {
        var harness = new CliHarness();
        harness.Modules.Add(PromptingProbe());

        CliRunResult result = await harness.RunAsync(["probe", "--interaction", "never"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("never", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Theory]
    [InlineData("CI", "true")]
    [InlineData("CI", "1")]
    [InlineData("GITHUB_ACTIONS", "true")]
    [InlineData("TF_BUILD", "true")]
    public async Task Ci_environments_disable_prompting_in_auto_mode(string variable, string value)
    {
        var harness = new CliHarness();
        harness.Modules.Add(PromptingProbe());
        harness.EnvironmentVariables[variable] = value;

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Empty(harness.Interaction.Questions);
        InteractionCreation creation = Assert.Single(harness.InteractionCreations);
        Assert.True(creation.Capabilities.IsContinuousIntegration);
        Assert.False(creation.Capabilities.PromptsEnabled);
        Assert.False(creation.Capabilities.ProgressEnabled);
    }

    [Fact]
    public async Task Redirected_input_disables_prompting_in_auto_mode()
    {
        var harness = new CliHarness { IsInputRedirected = true };
        harness.Modules.Add(PromptingProbe());

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Interaction_always_enables_prompting_even_in_ci()
    {
        var harness = new CliHarness();
        harness.Modules.Add(PromptingProbe());
        harness.EnvironmentVariables["CI"] = "true";
        harness.Interaction.EnqueueText("Fancy.Package");

        CliRunResult result = await harness.RunAsync(["probe", "--interaction", "always"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Fancy.Package\n", result.StandardOutput);
        Assert.Single(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Interactive_sessions_prompt_and_use_the_answer()
    {
        var harness = new CliHarness();
        harness.Modules.Add(PromptingProbe());
        harness.Interaction.EnqueueText("Fancy.Package");

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Fancy.Package\n", result.StandardOutput);
        Assert.Equal("Package identifier?", Assert.Single(harness.Interaction.Questions));
    }

    [Fact]
    public async Task No_color_option_disables_color()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        await harness.RunAsync(["probe", "--no-color"]);

        InteractionCreation creation = Assert.Single(harness.InteractionCreations);
        Assert.False(creation.Capabilities.ColorEnabled);
        Assert.False(creation.Capabilities.ProgressEnabled);
    }

    [Fact]
    public async Task No_color_environment_variable_disables_color()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());
        harness.EnvironmentVariables["NO_COLOR"] = "1";

        await harness.RunAsync(["probe"]);

        InteractionCreation creation = Assert.Single(harness.InteractionCreations);
        Assert.False(creation.Capabilities.ColorEnabled);
        Assert.False(creation.Capabilities.ProgressEnabled);
    }

    [Fact]
    public async Task Redirected_stderr_disables_color()
    {
        var harness = new CliHarness { IsErrorRedirected = true };
        harness.Modules.Add(new ProbeModule());

        await harness.RunAsync(["probe"]);

        InteractionCreation creation = Assert.Single(harness.InteractionCreations);
        Assert.False(creation.Capabilities.ColorEnabled);
        Assert.False(creation.Capabilities.ProgressEnabled);
    }

    [Fact]
    public async Task Color_is_enabled_on_a_plain_interactive_terminal()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        await harness.RunAsync(["probe"]);

        InteractionCreation creation = Assert.Single(harness.InteractionCreations);
        Assert.True(creation.Capabilities.ColorEnabled);
        Assert.True(creation.Capabilities.PromptsEnabled);
        Assert.True(creation.Capabilities.ProgressEnabled);
    }

    [Fact]
    public async Task Prompt_text_is_redacted_before_reaching_the_terminal()
    {
        const string prompt =
            "Choose https://example.test/app?X-Amz-Signature=secret-signature";
        var harness = new CliHarness();
        harness.Interaction.EnqueueText("answer");
        harness.Modules.Add(new ProbeModule(async context =>
        {
            _ = await context.Interaction.AskAsync(prompt);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        string actual = Assert.Single(harness.Interaction.Questions);
        Assert.DoesNotContain("secret-signature", actual, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirected_progress_runs_silently()
    {
        var harness = new CliHarness
        {
            IsInputRedirected = true,
            IsErrorRedirected = true,
            UseFakeInteraction = false,
        };
        harness.Modules.Add(new ProbeModule(async context =>
        {
            int value = await context.Interaction.RunProgressAsync(
                "Analyzing",
                async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    return 42;
                },
                context.CancellationToken);
            Assert.Equal(42, value);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Plain_interactive_terminal_uses_spectre_progress()
    {
        var harness = new CliHarness { UseFakeInteraction = false };
        harness.Modules.Add(new ProbeModule(async context =>
        {
            int value = await context.Interaction.RunProgressAsync(
                "Analyzing",
                _ => Task.FromResult(42),
                context.CancellationToken);
            Assert.Equal(42, value);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Analyzing", result.StandardError, StringComparison.Ordinal);
    }
}
