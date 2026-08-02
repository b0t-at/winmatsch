using Spectre.Console;

namespace WinMatsch.Cli.Interaction;

/// <summary>
/// The interactive terminal implementation of <see cref="IUserInteraction"/> built on
/// Spectre.Console. The underlying console is bound to <em>standard error</em> so prompts and
/// status never contaminate piped standard output, and its color/ANSI capabilities follow the
/// host's <see cref="Hosting.ConsoleCapabilities"/> decision (NO_COLOR, redirection, CI).
/// </summary>
public sealed class SpectreUserInteraction : IUserInteraction
{
    private readonly IAnsiConsole _console;
    private readonly bool _progressEnabled;

    /// <param name="console">A console writing to standard error, from
    /// <see cref="CreateErrorConsole"/> or a test double.</param>
    public SpectreUserInteraction(IAnsiConsole console, bool progressEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
        _progressEnabled = progressEnabled;
    }

    public bool CanPrompt => true;

    /// <summary>Creates a Spectre console over the given standard error writer.</summary>
    public static IAnsiConsole CreateErrorConsole(TextWriter error, bool colorEnabled)
        => CreateErrorConsole(error, colorEnabled, AnsiSupport.Detect);

    internal static IAnsiConsole CreateErrorConsole(
        TextWriter error,
        bool colorEnabled,
        AnsiSupport ansiSupport)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = colorEnabled ? ansiSupport : AnsiSupport.No,
            ColorSystem = colorEnabled ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(error),
        });
    }

    public Task<bool> ConfirmAsync(
        string question,
        bool defaultValue = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var prompt = new ConfirmationPrompt(Markup.Escape(question)) { DefaultValue = defaultValue };
        return _console.PromptAsync(prompt, cancellationToken);
    }

    public async Task<string> AskAsync(
        string question,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var prompt = new TextPrompt<string>(Markup.Escape(question));
        if (defaultValue is not null)
        {
            prompt.DefaultValue(defaultValue);
        }

        return await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SelectAsync(
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        var prompt = new SelectionPrompt<string>()
            .Title(Markup.Escape(question))
            .AddChoices(choices);
        return await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public void ReportStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_progressEnabled)
        {
            _console.WriteLine(message);
        }
    }

    public async Task<T> RunProgressAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(operation);
        if (!_progressEnabled)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        T? result = default;
        await _console.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(new SpinnerColumn(), new TaskDescriptionColumn())
            .StartAsync(async progress =>
            {
                ProgressTask task = progress.AddTask(Markup.Escape(description), maxValue: 1);
                task.IsIndeterminate = true;
                result = await operation(cancellationToken).ConfigureAwait(false);
                task.IsIndeterminate = false;
                task.Value = 1;
            })
            .ConfigureAwait(false);
        return result!;
    }
}
