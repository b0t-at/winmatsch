namespace WinMatsch.Cli.Interaction;

/// <summary>
/// The interaction used when prompting is unavailable (redirected input, CI, JSON output, or
/// <c>--interaction never</c>). Every prompt throws <see cref="MissingInputException"/> naming
/// the question, so runs fail fast with <see cref="ExitCodes.MissingInput"/> instead of hanging.
/// </summary>
public sealed class NonInteractiveUserInteraction : IUserInteraction
{
    private readonly TextWriter _error;
    private readonly string _reason;

    /// <param name="error">The standard error writer used for status messages.</param>
    /// <param name="reason">Why prompting is unavailable; included in failure messages.</param>
    public NonInteractiveUserInteraction(TextWriter error, string reason)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _error = error;
        _reason = reason;
    }

    public bool CanPrompt => false;

    public Task<bool> ConfirmAsync(
        string question,
        bool defaultValue = true,
        CancellationToken cancellationToken = default) => throw Missing(question);

    public Task<string> AskAsync(
        string question,
        string? defaultValue = null,
        CancellationToken cancellationToken = default) => throw Missing(question);

    public Task<string> SelectAsync(
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default) => throw Missing(question);

    public void ReportStatus(string message) => _error.WriteLine(message);

    public async Task<T> RunProgressAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(operation);
        _error.WriteLine($"{description}...");
        T result = await operation(cancellationToken).ConfigureAwait(false);
        _error.WriteLine($"{description}: complete.");
        return result;
    }

    private MissingInputException Missing(string question) =>
        new($"Input is required but prompting is unavailable ({_reason}): {question}");
}
