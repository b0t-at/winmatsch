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

        RepairAssociation association = ParseAssociation(
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
            || local.Plan.PackageIdentifier != association.Identifier
            || local.Plan.PackageVersion != association.Version)
        {
            throw new CliOperationException(
                $"Approved repair for PR #{pullRequest.PullRequest.Number} did not pass "
                + "full local preflight or match the pull request package identity.");
        }

        LocalOperationPlan repairPlan = association.Operation == GitHubManifestOperation.Replace
            ? await AddReplacementDeletionsAsync(
                local.Plan,
                association,
                context.Configuration.OutputDirectory ?? Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false)
            : local.Plan;
        return new GitHubSubmissionRequest
        {
            LocalPlan = repairPlan,
            UpstreamRepository = context.Configuration.Repository,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Operation = association.Operation,
            Policy = new()
            {
                ForkConsent = ForkConsentPolicy.ExistingOnly,
                ReplacePreviousVersion =
                    association.Operation == GitHubManifestOperation.Replace,
                PreviousVersion = association.PreviousVersion,
            },
            CreatedWith = "winmatsch approved repair",
            SupersedesPullRequestNumber = pullRequest.PullRequest.Number,
            IdempotencyKey = $"feedback-repair:{pullRequest.PullRequest.Number}:"
                + classification.ToString(),
        };
    }

    private static RepairAssociation ParseAssociation(
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

        ImmutableArray<string> deletions =
        [
            .. body.Split('\n')
                .Select(static line => line.TrimEnd('\r'))
                .Where(static line => line.StartsWith(
                    "- Delete: `",
                    StringComparison.Ordinal))
                .Select(static line =>
                {
                    const string prefix = "- Delete: `";
                    int end = line.IndexOf('`', prefix.Length);
                    return end > prefix.Length
                        ? line[prefix.Length..end]
                        : "";
                })
                .Where(static path => path.Length > 0)
                .Distinct(StringComparer.Ordinal),
        ];
        PackageVersion? previousVersion = operation == GitHubManifestOperation.Replace
            ? InferPreviousVersion(new PackageIdentifier(package), deletions)
            : null;
        return new(
            new PackageIdentifier(package),
            new PackageVersion(version
                ?? throw new CliOperationException(
                    "Approved repair association marker is missing the version.")),
            operation,
            previousVersion,
            deletions);
    }

    private static PackageVersion InferPreviousVersion(
        PackageIdentifier identifier,
        ImmutableArray<string> deletions)
    {
        string packageDirectory = ManifestPaths.GetPackageDirectory(identifier) + "/";
        string[] versions =
        [
            .. deletions.Select(path =>
            {
                if (!path.StartsWith(packageDirectory, StringComparison.Ordinal))
                {
                    throw new CliOperationException(
                        "Replacement repair deletion path is outside the associated package.");
                }

                string remainder = path[packageDirectory.Length..];
                int separator = remainder.IndexOf('/');
                return separator > 0 ? remainder[..separator] : "";
            }).Where(static value => value.Length > 0)
                .Distinct(StringComparer.Ordinal),
        ];
        if (versions.Length != 1)
        {
            throw new CliOperationException(
                "Replacement repair requires deletion paths for exactly one previous version.");
        }

        return new PackageVersion(versions[0]);
    }

    private static async Task<LocalOperationPlan> AddReplacementDeletionsAsync(
        LocalOperationPlan plan,
        RepairAssociation association,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(outputDirectory);
        var before = plan.BeforeDocuments.ToBuilder();
        var changes = plan.FileChanges.ToBuilder();
        foreach (string repositoryPath in association.OriginalDeletions)
        {
            string fullPath = Path.GetFullPath(Path.Combine(
                root,
                repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                throw new CliOperationException(
                    $"Approved replacement deletion source '{repositoryPath}' is unavailable.");
            }

            byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            before.Add(new(repositoryPath, content));
            changes.Add(new(
                PlannedChangeKind.Delete,
                repositoryPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(content)));
        }

        ImmutableArray<RawManifestDocument> beforeDocuments =
        [
            .. before.DistinctBy(
                static document => document.RepositoryPath,
                StringComparer.Ordinal),
        ];
        ImmutableArray<WorkflowFileChange> fileChanges =
        [
            .. changes.DistinctBy(
                static change => change.RepositoryPath,
                StringComparer.Ordinal),
        ];
        return plan with
        {
            BeforeDocuments = beforeDocuments,
            FileChanges = fileChanges,
            Preflight = plan.Preflight with
            {
                BeforeDocuments = beforeDocuments,
                Changes = fileChanges,
            },
        };
    }

    private sealed record RepairAssociation(
        PackageIdentifier Identifier,
        PackageVersion Version,
        GitHubManifestOperation Operation,
        PackageVersion? PreviousVersion,
        ImmutableArray<string> OriginalDeletions);
}
