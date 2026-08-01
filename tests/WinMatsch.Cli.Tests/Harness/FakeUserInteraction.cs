using WinMatsch.Cli.Interaction;

namespace WinMatsch.Cli.Tests.Harness;

/// <summary>
/// A scripted <see cref="IUserInteraction"/> that records every prompt and status message.
/// It is contract-faithful: when <see cref="CanPrompt"/> is false, prompts throw
/// <see cref="MissingInputException"/> exactly like the production non-interactive
/// implementation, so tests exercise the real failure path.
/// </summary>
public sealed class FakeUserInteraction : IUserInteraction
{
    private readonly Queue<bool> _confirmAnswers = [];
    private readonly Queue<string> _textAnswers = [];
    private readonly Queue<string> _selectionAnswers = [];

    public FakeUserInteraction(bool canPrompt = true)
    {
        CanPrompt = canPrompt;
    }

    public bool CanPrompt { get; }

    public List<string> Questions { get; } = [];

    public List<string> StatusMessages { get; } = [];

    public List<string> ProgressDescriptions { get; } = [];

    public void EnqueueConfirm(bool answer) => _confirmAnswers.Enqueue(answer);

    public void EnqueueText(string answer) => _textAnswers.Enqueue(answer);

    public void EnqueueSelection(string answer) => _selectionAnswers.Enqueue(answer);

    public Task<bool> ConfirmAsync(
        string question,
        bool defaultValue = true,
        CancellationToken cancellationToken = default)
    {
        GuardPrompt(question);
        Questions.Add(question);
        return Task.FromResult(_confirmAnswers.Count > 0 ? _confirmAnswers.Dequeue() : defaultValue);
    }

    public Task<string> AskAsync(
        string question,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
        GuardPrompt(question);
        Questions.Add(question);
        if (_textAnswers.Count > 0)
        {
            return Task.FromResult(_textAnswers.Dequeue());
        }

        return defaultValue is not null
            ? Task.FromResult(defaultValue)
            : throw new InvalidOperationException($"No scripted answer for: {question}");
    }

    public Task<string> SelectAsync(
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
    {
        GuardPrompt(question);
        Questions.Add(question);
        return _selectionAnswers.Count > 0
            ? Task.FromResult(_selectionAnswers.Dequeue())
            : throw new InvalidOperationException($"No scripted selection for: {question}");
    }

    public void ReportStatus(string message) => StatusMessages.Add(message);

    public Task<T> RunProgressAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ProgressDescriptions.Add(description);
        return operation(cancellationToken);
    }

    private void GuardPrompt(string question)
    {
        if (!CanPrompt)
        {
            throw new MissingInputException(
                $"Input is required but prompting is unavailable (fake non-interactive): {question}");
        }
    }
}
