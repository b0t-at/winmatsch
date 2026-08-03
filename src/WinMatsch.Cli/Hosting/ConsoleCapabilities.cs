using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// The negotiated console behavior for one invocation. Encodes the documented
/// no-color / CI / redirection rules in one place:
/// <list type="bullet">
/// <item><b>Color</b> is disabled by <c>--no-color</c>, a non-empty <c>NO_COLOR</c> environment
/// variable (https://no-color.org), or a redirected standard error stream (prompts and status
/// render on standard error, so that is the stream whose capabilities matter).</item>
/// <item><b>Prompts</b> are disabled by JSON output (machine consumers must never see a hung
/// prompt), by <c>--interaction never</c>, and in <c>auto</c> mode by CI environments
/// (<c>CI</c>, <c>GITHUB_ACTIONS</c>, or <c>TF_BUILD</c> set to a truthy value) or redirected
/// standard input/error. <c>--interaction always</c> forces prompting.</item>
/// <item><b>Progress</b> is enabled only for plain interactive text terminals. JSON, CI,
/// no-color, no-interaction, or any redirected standard stream runs the operation silently.</item>
/// </list>
/// </summary>
public sealed record ConsoleCapabilities
{
    /// <summary>Whether ANSI color may be used on standard error.</summary>
    public required bool ColorEnabled { get; init; }

    /// <summary>Whether prompting is allowed. See the type docs for the rules.</summary>
    public required bool PromptsEnabled { get; init; }

    /// <summary>Whether transient Spectre progress may render on standard error.</summary>
    public required bool ProgressEnabled { get; init; }

    /// <summary>Why prompting is disabled; null when <see cref="PromptsEnabled"/> is true.</summary>
    public string? PromptsDisabledReason { get; init; }

    /// <summary>Whether a CI environment was detected.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Applies the documented rules to one invocation's inputs.</summary>
    public static ConsoleCapabilities Resolve(
        InteractionMode interaction,
        OutputFormat format,
        bool noColorRequested,
        Func<string, string?> environment,
        bool isInputRedirected,
        bool isOutputRedirected,
        bool isErrorRedirected)
    {
        ArgumentNullException.ThrowIfNull(environment);

        bool isCi = IsTruthy(environment("CI"))
            || IsTruthy(environment("GITHUB_ACTIONS"))
            || IsTruthy(environment("TF_BUILD"));

        bool noColor = noColorRequested || !string.IsNullOrEmpty(environment("NO_COLOR"));
        bool colorEnabled = !noColor
            && !isErrorRedirected;
        bool progressEnabled = interaction != InteractionMode.Never
            && format != OutputFormat.Json
            && !noColor
            && !isCi
            && !isInputRedirected
            && !isOutputRedirected
            && !isErrorRedirected;

        (bool promptsEnabled, string? reason) = DecidePrompting(
            interaction, format, isCi, isInputRedirected, isErrorRedirected);

        return new ConsoleCapabilities
        {
            ColorEnabled = colorEnabled,
            PromptsEnabled = promptsEnabled,
            ProgressEnabled = progressEnabled,
            PromptsDisabledReason = reason,
            IsContinuousIntegration = isCi,
        };
    }

    private static (bool Enabled, string? Reason) DecidePrompting(
        InteractionMode interaction,
        OutputFormat format,
        bool isCi,
        bool isInputRedirected,
        bool isErrorRedirected)
    {
        if (format == OutputFormat.Json)
        {
            return (false, "JSON output never prompts");
        }

        return interaction switch
        {
            InteractionMode.Never => (false, "interaction mode is 'never'"),
            InteractionMode.Always => (true, null),
            _ when isCi => (false, "a CI environment was detected"),
            _ when isInputRedirected => (false, "standard input is redirected"),
            _ when isErrorRedirected => (false, "standard error is redirected"),
            _ => (true, null),
        };
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
