using System.Collections.Immutable;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Hosting;
using WinMatsch.Core;
using WinMatsch.Workflows;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>Non-persisting state adapter used by read-only feedback inspection.</summary>
public sealed class NoOpFeedbackStateStore : IFeedbackStateStore
{
    public Task PersistAsync(
        FeedbackWorkItem item,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// Read-only adapter over the native store: replay can inspect the exact pending queue without
/// mutating it during dry-run.
/// </summary>
public sealed class ReadOnlyFeedbackStateStore(IFeedbackStateStore inner) : IFeedbackStateStore
{
    private readonly IFeedbackStateStore _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    public Task PersistAsync(
        FeedbackWorkItem item,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<ImmutableArray<FeedbackWorkItem>> GetPendingAsync(
        string repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _inner.GetPendingAsync(repository, now, cancellationToken);
}

public interface IApprovedRepairPlannerFactory
{
    public IApprovedRepairPlanner Create(
        CommandContext context,
        IReadOnlyDictionary<long, string> approvedManifestDirectories);
}

public sealed class CliApprovedRepairPlannerFactory(
    IMutationWorkflowFactory workflows,
    IRawManifestSetLoader? loader = null) : IApprovedRepairPlannerFactory
{
    private readonly IMutationWorkflowFactory _workflows =
        workflows ?? throw new ArgumentNullException(nameof(workflows));
    private readonly IRawManifestSetLoader _loader =
        loader ?? new FileSystemRawManifestSetLoader();

    public IApprovedRepairPlanner Create(
        CommandContext context,
        IReadOnlyDictionary<long, string> approvedManifestDirectories)
        => new AllowlistedApprovedRepairPlanner(
            context,
            approvedManifestDirectories,
            _workflows,
            _loader);
}

public sealed class AllowlistedApprovedRepairPlanner(
    CommandContext context,
    IReadOnlyDictionary<long, string> approvedManifestDirectories,
    IMutationWorkflowFactory workflows,
    IRawManifestSetLoader loader) : IApprovedRepairPlanner
{
    public async Task<GitHubSubmissionRequest?> PlanApprovedRepairAsync(
        PullRequestObservation pullRequest,
        FeedbackClassification classification,
        CancellationToken cancellationToken)
    {
        if (classification is not (
                FeedbackClassification.DuplicateEntry
                or FeedbackClassification.HashMismatch)
            || !approvedManifestDirectories.TryGetValue(
                pullRequest.PullRequest.Number,
                out string? directory))
        {
            return null;
        }

        (PackageIdentifier identifier, PackageVersion version, GitHubManifestOperation operation) =
            ParseAssociation(
            pullRequest.PullRequest.Body);
        ImmutableArray<RawManifestDocument> documents = await loader.LoadAsync(
            directory,
            context.Configuration.OutputDirectory ?? Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);
        IMutationWorkflow workflow = await workflows.CreateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        WorkflowOperationResult local = await workflow.ExecuteAsync(
            new SubmitOperationRequest
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                OutputDirectory = context.Configuration.OutputDirectory
                    ?? Environment.CurrentDirectory,
                Documents = documents,
                CreatedWith = "winmatsch approved repair",
                ApproveReview = false,
            },
            cancellationToken).ConfigureAwait(false);
        if (local.Code != WorkflowResultCode.Succeeded
            || local.Plan.RequiresReview
            || !local.Plan.Questions.IsEmpty
            || local.Plan.PackageIdentifier != identifier
            || local.Plan.PackageVersion != version)
        {
            throw new CliOperationException(
                $"Approved repair for PR #{pullRequest.PullRequest.Number} did not pass "
                + "full local preflight or match the pull request package identity.");
        }

        return new GitHubSubmissionRequest
        {
            LocalPlan = local.Plan,
            UpstreamRepository = context.Configuration.Repository,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Operation = operation,
            Policy = new()
            {
                ForkConsent = ForkConsentPolicy.ExistingOnly,
            },
            CreatedWith = "winmatsch approved repair",
            SupersedesPullRequestNumber = pullRequest.PullRequest.Number,
            IdempotencyKey = $"feedback-repair:{pullRequest.PullRequest.Number}:"
                + classification.ToString(),
        };
    }

    private static (
        PackageIdentifier Identifier,
        PackageVersion Version,
        GitHubManifestOperation Operation) ParseAssociation(
        string? body)
    {
        const string marker = "<!-- winmatsch:package=";
        int start = body?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        int end = start < 0 ? -1 : body!.IndexOf(" -->", start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new CliOperationException(
                "Approved repair requires the exact winmatsch package association marker.");
        }

        string content = body![(start + marker.Length)..end];
        string[] parts = content.Split(';', StringSplitOptions.TrimEntries);
        string package = parts[0];
        string? version = parts.FirstOrDefault(static part =>
            part.StartsWith("version=", StringComparison.Ordinal))?["version=".Length..];
        GitHubManifestOperation operation = GitHubManifestOperation.Update;
        string? operationLine = body!.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(static line =>
                line.StartsWith("Operation:", StringComparison.OrdinalIgnoreCase));
        if (operationLine is not null
            && !Enum.TryParse(
                operationLine["Operation:".Length..].Trim(),
                ignoreCase: true,
                out operation))
        {
            throw new CliOperationException(
                "Approved repair pull request contains an unknown operation.");
        }

        return (
            new PackageIdentifier(package),
            new PackageVersion(version
                ?? throw new CliOperationException(
                    "Approved repair association marker is missing the version.")),
            operation);
    }
}
