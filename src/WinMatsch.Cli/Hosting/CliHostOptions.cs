using WinMatsch.Cli.Interaction;
using WinMatsch.GitHub.Auth;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// The host's injection points. Production uses <see cref="CliHost.CreateDefault"/>; tests
/// substitute writers, environment, file access, interaction, and the token store to run the
/// host fully in process with deterministic behavior.
/// </summary>
public sealed record CliHostOptions
{
    /// <summary>The standard output writer. Carries command results only.</summary>
    public required TextWriter Output { get; init; }

    /// <summary>The standard error writer. Carries diagnostics, prompts, and errors.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Environment variable lookup.</summary>
    public Func<string, string?> EnvironmentVariables { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Reads a text file, or returns null when it does not exist.</summary>
    public Func<string, string?> ReadTextFile { get; init; } = DefaultReadTextFile;

    /// <summary>Whether standard input is redirected (disables prompting in auto mode).</summary>
    public bool IsInputRedirected { get; init; }

    /// <summary>Whether standard output is redirected.</summary>
    public bool IsOutputRedirected { get; init; }

    /// <summary>Whether standard error is redirected (disables color and auto-mode prompting).</summary>
    public bool IsErrorRedirected { get; init; }

    /// <summary>The user's home directory, for the default configuration file location.</summary>
    public string? HomeDirectory { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The command modules composed into this host.</summary>
    public IReadOnlyList<ICommandModule> Modules { get; init; } = [];

    /// <summary>
    /// Creates the <see cref="IUserInteraction"/> for an invocation. Defaults to Spectre when
    /// prompting is enabled and the fail-fast non-interactive implementation otherwise.
    /// </summary>
    public Func<InteractionCreation, IUserInteraction>? InteractionFactory { get; init; }

    /// <summary>The OS keyring adapter. Defaults to the current platform's store.</summary>
    public ITokenStore? TokenStore { get; init; }

    private static string? DefaultReadTextFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}

/// <summary>The inputs available when creating an invocation's <see cref="IUserInteraction"/>.</summary>
public sealed record InteractionCreation(ConsoleCapabilities Capabilities, TextWriter Error);
