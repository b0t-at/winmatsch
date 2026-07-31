using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Interaction;

namespace WinMatsch.Cli.Tests.Harness;

/// <summary>
/// Runs the real <see cref="CliHost"/> composition fully in process as a pseudo-process:
/// arguments in, deterministic exit code plus captured standard output and standard error out.
/// The environment, file system, interaction, and token store are all faked, so no test
/// touches the machine it runs on (including CI environment variables of the test run itself).
/// Writers use LF line endings for byte-stable assertions across platforms.
/// </summary>
public sealed class CliHarness
{
    /// <summary>Fake environment variables; the process environment is never consulted.</summary>
    public Dictionary<string, string?> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>Fake text files keyed by full path; the disk is never consulted.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<ICommandModule> Modules { get; } = [];

    public FakeTokenStore TokenStore { get; } = new();

    /// <summary>The interaction handed to commands. Replace to script prompt answers.</summary>
    public FakeUserInteraction Interaction { get; set; } = new();

    /// <summary>
    /// When true (default) the host receives <see cref="Interaction"/> regardless of
    /// capabilities but honoring the contract via <see cref="ConsoleCapabilities.PromptsEnabled"/>:
    /// a prompt-disabled invocation gets a non-prompting fake. Set to false to exercise the
    /// host's own default interaction construction.
    /// </summary>
    public bool UseFakeInteraction { get; set; } = true;

    public bool IsInputRedirected { get; set; }

    public bool IsOutputRedirected { get; set; }

    public bool IsErrorRedirected { get; set; }

    public string? HomeDirectory { get; set; } = "/home/tester";

    /// <summary>Every capability decision the host made, newest last.</summary>
    public List<InteractionCreation> InteractionCreations { get; } = [];

    public async Task<CliRunResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        output.NewLine = "\n";
        error.NewLine = "\n";

        var host = new CliHost(new CliHostOptions
        {
            Output = output,
            Error = error,
            EnvironmentVariables = name =>
                EnvironmentVariables.TryGetValue(name, out string? value) ? value : null,
            ReadTextFile = path => Files.TryGetValue(path, out string? content) ? content : null,
            IsInputRedirected = IsInputRedirected,
            IsOutputRedirected = IsOutputRedirected,
            IsErrorRedirected = IsErrorRedirected,
            HomeDirectory = HomeDirectory,
            Modules = [.. Modules],
            TokenStore = TokenStore,
            InteractionFactory = UseFakeInteraction ? CreateInteraction : null,
        });

        int exitCode = await host.RunAsync(args, cancellationToken);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private IUserInteraction CreateInteraction(InteractionCreation creation)
    {
        InteractionCreations.Add(creation);
        if (!creation.Capabilities.PromptsEnabled)
        {
            return new NonInteractiveUserInteraction(
                creation.Error,
                creation.Capabilities.PromptsDisabledReason ?? "non-interactive session");
        }

        return Interaction;
    }
}

/// <summary>The observable outcome of one in-process CLI invocation.</summary>
public sealed record CliRunResult(int ExitCode, string StandardOutput, string StandardError);
