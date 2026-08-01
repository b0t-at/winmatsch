using System.CommandLine;
using System.CommandLine.Parsing;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// The documented global options, available on every command (recursive). Each maps to the
/// command layer of the configuration precedence chain
/// <c>command &gt; environment (WINMATSCH_*) &gt; user configuration file &gt; built-in defaults</c>;
/// an option left unset falls through to the next layer.
///
/// <list type="table">
/// <item><term><c>--repo</c></term><description>Target repository in <c>owner/name</c> form.</description></item>
/// <item><term><c>--format</c></term><description>Result format on standard output: <c>text</c> or <c>json</c>.</description></item>
/// <item><term><c>--output</c></term><description>Directory where generated manifests and reports are written.</description></item>
/// <item><term><c>--concurrent-downloads</c></term><description>Maximum parallel installer downloads.</description></item>
/// <item><term><c>--dry-run</c></term><description>Plan mode: validate and show what would change; never mutates.</description></item>
/// <item><term><c>--interaction</c></term><description>Prompting policy: <c>auto</c>, <c>always</c>, or <c>never</c>.</description></item>
/// <item><term><c>--no-color</c></term><description>Disable ANSI color (also honored via <c>NO_COLOR</c>).</description></item>
/// <item><term><c>--config</c></term><description>Path to the user configuration file.</description></item>
/// <item><term><c>--token</c></term><description>GitHub token; overrides <c>GITHUB_TOKEN</c> and the OS keyring.</description></item>
/// </list>
/// </summary>
public sealed class GlobalOptions
{
    public GlobalOptions()
    {
        Repository = new Option<string?>("--repo")
        {
            Description = "Target repository in owner/name form (default: microsoft/winget-pkgs).",
            HelpName = "owner/name",
            Recursive = true,
        };

        Format = new Option<OutputFormat?>("--format")
        {
            Description = "Result format written to standard output: text or json. JSON output never prompts.",
            HelpName = "text|json",
            Recursive = true,
            CustomParser = ParseOutputFormat,
        };

        OutputDirectory = new Option<string?>("--output")
        {
            Description = "Directory where generated manifests and reports are written.",
            HelpName = "directory",
            Recursive = true,
        };

        ConcurrentDownloads = new Option<int?>("--concurrent-downloads")
        {
            Description = "Maximum number of installers downloaded in parallel.",
            HelpName = "count",
            Recursive = true,
        };

        OverrideStoreDirectory = new Option<string?>("--override-store")
        {
            Description = "Directory for approved learned override packs and recovery journals.",
            HelpName = "directory",
            Recursive = true,
        };

        DryRun = new Option<bool>("--dry-run")
        {
            Description = "Plan mode: validate and show what would change without mutating anything.",
            Recursive = true,
        };

        Interaction = new Option<InteractionMode?>("--interaction")
        {
            Description = "Prompting policy: auto (prompt only on an interactive terminal), always, or never.",
            HelpName = "auto|always|never",
            Recursive = true,
            CustomParser = ParseInteractionMode,
        };

        NoColor = new Option<bool>("--no-color")
        {
            Description = "Disable ANSI color output (the NO_COLOR environment variable is also honored).",
            Recursive = true,
        };

        ConfigFile = new Option<string?>("--config")
        {
            Description = "Path to the user configuration file (default: ~/.config/winmatsch/config.yaml).",
            HelpName = "file",
            Recursive = true,
        };

        Token = new Option<GitHubToken?>("--token")
        {
            Description = "GitHub token. Precedence: --token > GITHUB_TOKEN > OS keyring. Never echoed.",
            HelpName = "token",
            Recursive = true,
            CustomParser = ParseToken,
        };
    }

    public Option<string?> Repository { get; }

    public Option<OutputFormat?> Format { get; }

    public Option<string?> OutputDirectory { get; }

    public Option<int?> ConcurrentDownloads { get; }

    public Option<string?> OverrideStoreDirectory { get; }

    public Option<bool> DryRun { get; }

    public Option<InteractionMode?> Interaction { get; }

    public Option<bool> NoColor { get; }

    public Option<string?> ConfigFile { get; }

    public Option<GitHubToken?> Token { get; }

    /// <summary>Every global option, for registration on the root command.</summary>
    public IReadOnlyList<Option> All =>
    [
        Repository,
        Format,
        OutputDirectory,
        ConcurrentDownloads,
        OverrideStoreDirectory,
        DryRun,
        Interaction,
        NoColor,
        ConfigFile,
        Token,
    ];

    /// <summary>Builds the command-line configuration layer from parsed global options.</summary>
    public ConfigurationLayer BindConfigurationLayer(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return new ConfigurationLayer
        {
            Repository = parseResult.GetValue(Repository),
            ConcurrentDownloads = parseResult.GetValue(ConcurrentDownloads),
            OverrideStoreDirectory = parseResult.GetValue(OverrideStoreDirectory),
            OutputFormat = parseResult.GetValue(Format),
            OutputDirectory = parseResult.GetValue(OutputDirectory),
            Interaction = parseResult.GetValue(Interaction),
        };
    }

    private static OutputFormat? ParseOutputFormat(ArgumentResult result)
    {
        string value = result.Tokens[0].Value;
        switch (value.Trim().ToLowerInvariant())
        {
            case "text":
                return OutputFormat.Text;
            case "json":
                return OutputFormat.Json;
            default:
                result.AddError($"'{value}' is not a valid output format. Use 'text' or 'json'.");
                return null;
        }
    }

    private static InteractionMode? ParseInteractionMode(ArgumentResult result)
    {
        string value = result.Tokens[0].Value;
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                return InteractionMode.Auto;
            case "always":
                return InteractionMode.Always;
            case "never":
                return InteractionMode.Never;
            default:
                result.AddError($"'{value}' is not a valid interaction mode. Use 'auto', 'always', or 'never'.");
                return null;
        }
    }

    private static GitHubToken? ParseToken(ArgumentResult result)
    {
        // The raw value must never be echoed back in the error message: it may be a secret
        // that merely violates the token shape rules.
        try
        {
            return new GitHubToken(result.Tokens[0].Value);
        }
        catch (ArgumentException)
        {
            result.AddError(
                "The --token value is invalid: it must be non-empty and contain no whitespace or control characters.");
            return null;
        }
    }
}
