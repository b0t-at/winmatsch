using System.CommandLine;
using WinMatsch.Cli.Interaction;
using WinMatsch.Cli.Output;
using WinMatsch.GitHub;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// Everything a command handler needs for one invocation, composed by the host:
/// the resolved configuration (command &gt; environment &gt; user file &gt; defaults), the
/// negotiated console capabilities, the interaction and output seams, token access, and the
/// invocation's cancellation token.
///
/// <para><b>Dry-run contract:</b> when <see cref="IsDryRun"/> is true,
/// <see cref="ExecutionMode"/> is <see cref="WorkflowExecutionMode.Plan"/> and the handler must
/// not perform any mutation — no file writes outside temporary scratch space, no remote calls
/// that change state. Handlers produce an <see cref="OperationPlan"/>-shaped result and report
/// what would happen; the same code path runs for real when the mode is
/// <see cref="WorkflowExecutionMode.Apply"/>.</para>
/// </summary>
public sealed class CommandContext
{
    /// <summary>The parse result, for reading module-specific options and arguments.</summary>
    public required ParseResult ParseResult { get; init; }

    /// <summary>The fully resolved and validated configuration.</summary>
    public required WinMatschConfiguration Configuration { get; init; }

    /// <summary>Plan when <c>--dry-run</c> was passed; Apply otherwise.</summary>
    public required WorkflowExecutionMode ExecutionMode { get; init; }

    /// <summary>The negotiated color/prompting capabilities for this invocation.</summary>
    public required ConsoleCapabilities Capabilities { get; init; }

    /// <summary>Prompting and status reporting (renders on standard error).</summary>
    public required IUserInteraction Interaction { get; init; }

    /// <summary>Result and diagnostic output (results on standard output, the rest on standard error).</summary>
    public required ICommandOutput Output { get; init; }

    /// <summary>Lazy GitHub token access honoring <c>--token &gt; GITHUB_TOKEN &gt; OS keyring</c>.</summary>
    public required ITokenAccessor Tokens { get; init; }

    /// <summary>Invocation-scoped GitHub.com or GHES endpoint contract.</summary>
    public required GitHubClientOptions GitHubOptions { get; init; }

    /// <summary>Cancelled on Ctrl+C or host shutdown; handlers must observe it.</summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>True when the invocation runs in plan mode. See the type docs for the contract.</summary>
    public bool IsDryRun => ExecutionMode == WorkflowExecutionMode.Plan;
}
