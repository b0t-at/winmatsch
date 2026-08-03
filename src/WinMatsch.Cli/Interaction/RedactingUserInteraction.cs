using WinMatsch.Cli.Output;

namespace WinMatsch.Cli.Interaction;

/// <summary>Applies the central redaction contract before any prompt, option, or status is shown.</summary>
public sealed class RedactingUserInteraction(IUserInteraction inner) : IUserInteraction
{
    private readonly IUserInteraction _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    public bool CanPrompt => _inner.CanPrompt;

    public Task<bool> ConfirmAsync(
        string question,
        bool defaultValue = true,
        CancellationToken cancellationToken = default)
        => _inner.ConfirmAsync(CliRedactor.Redact(question), defaultValue, cancellationToken);

    public Task<string> AskAsync(
        string question,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
        => _inner.AskAsync(
            CliRedactor.Redact(question),
            CliRedactor.RedactNullable(defaultValue),
            cancellationToken);

    public Task<string> SelectAsync(
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
        => _inner.SelectAsync(
            CliRedactor.Redact(question),
            choices.Select(CliRedactor.Redact).ToArray(),
            cancellationToken);

    public void ReportStatus(string message) => _inner.ReportStatus(CliRedactor.Redact(message));

    public Task<T> RunProgressAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
        => _inner.RunProgressAsync(CliRedactor.Redact(description), operation, cancellationToken);
}
