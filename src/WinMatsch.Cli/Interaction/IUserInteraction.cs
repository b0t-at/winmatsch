namespace WinMatsch.Cli.Interaction;

/// <summary>
/// The single seam through which commands talk to the user. All prompts and status messages
/// render on <em>standard error</em>, keeping standard output reserved for command results.
///
/// <para>Contract for implementations and callers:</para>
/// <list type="bullet">
/// <item>When <see cref="CanPrompt"/> is false, every prompt method throws
/// <see cref="MissingInputException"/> instead of blocking, so non-interactive runs fail fast
/// and deterministically with <see cref="ExitCodes.MissingInput"/>.</item>
/// <item>JSON output always disables prompting; commands that need input in JSON mode must
/// receive it via options, environment variables, or configuration.</item>
/// <item>Prompt text must never contain secret values; tokens are redacted by
/// <see cref="WinMatsch.GitHub.Auth.GitHubToken"/> and must stay wrapped.</item>
/// </list>
/// </summary>
public interface IUserInteraction
{
    /// <summary>Whether prompts may be shown in this session.</summary>
    public bool CanPrompt { get; }

    /// <summary>Asks a yes/no question.</summary>
    public Task<bool> ConfirmAsync(
        string question,
        bool defaultValue = true,
        CancellationToken cancellationToken = default);

    /// <summary>Asks for a free-form text value.</summary>
    public Task<string> AskAsync(
        string question,
        string? defaultValue = null,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the user to pick exactly one of <paramref name="choices"/>.</summary>
    public Task<string> SelectAsync(
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a transient status/progress message to standard error. Safe to call in any
    /// interaction mode; never throws for being non-interactive.
    /// </summary>
    public void ReportStatus(string message);
}
