using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Hosting;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Maintenance;

public sealed record DurableFeedbackRetry(
    long PullRequestNumber,
    FeedbackClassification Classification,
    DateTimeOffset RetryAfter,
    string? LearnedOverrideSignal);

public interface IFeedbackStateStore
{
    public Task<ImmutableArray<DurableFeedbackRetry>> LoadAsync(
        CancellationToken cancellationToken);

    public Task SaveAsync(
        ImmutableArray<DurableFeedbackRetry> pending,
        CancellationToken cancellationToken);
}

public sealed class NullFeedbackStateStore : IFeedbackStateStore
{
    public Task<ImmutableArray<DurableFeedbackRetry>> LoadAsync(
        CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<DurableFeedbackRetry>.Empty);

    public Task SaveAsync(
        ImmutableArray<DurableFeedbackRetry> pending,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class FileFeedbackStateStore(string path) : IFeedbackStateStore
{
    private readonly string _path =
        !string.IsNullOrWhiteSpace(path)
            ? Path.GetFullPath(path)
            : throw new ArgumentException("Feedback state path is required.", nameof(path));

    public async Task<ImmutableArray<DurableFeedbackRetry>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        byte[] content = await File.ReadAllBytesAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(content);
        var pending = ImmutableArray.CreateBuilder<DurableFeedbackRetry>();
        foreach (JsonElement item in document.RootElement.GetProperty("pending").EnumerateArray())
        {
            if (!Enum.TryParse(
                    item.GetProperty("classification").GetString(),
                    ignoreCase: true,
                    out FeedbackClassification classification))
            {
                throw new InvalidDataException("Feedback state contains an unknown classification.");
            }

            pending.Add(new(
                item.GetProperty("pullRequestNumber").GetInt64(),
                classification,
                item.GetProperty("retryAfter").GetDateTimeOffset(),
                item.TryGetProperty("learnedOverrideSignal", out JsonElement learned)
                    && learned.ValueKind == JsonValueKind.String
                        ? learned.GetString()
                        : null));
        }

        return pending
            .OrderBy(static item => item.PullRequestNumber)
            .ToImmutableArray();
    }

    public async Task SaveAsync(
        ImmutableArray<DurableFeedbackRetry> pending,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_path)
            ?? throw new IOException("Feedback state path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteStartArray("pending");
                foreach (DurableFeedbackRetry item in pending.OrderBy(
                             static item => item.PullRequestNumber))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("pullRequestNumber", item.PullRequestNumber);
                    writer.WriteString("classification", item.Classification.ToString());
                    writer.WriteString("retryAfter", item.RetryAfter);
                    if (item.LearnedOverrideSignal is null)
                    {
                        writer.WriteNull("learnedOverrideSignal");
                    }
                    else
                    {
                        writer.WriteString("learnedOverrideSignal", item.LearnedOverrideSignal);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public sealed class FeedbackReplayCoordinator(
    IFeedbackStateStore store,
    IWorkflowClock clock)
{
    private readonly IFeedbackStateStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IWorkflowClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    public Task<ImmutableArray<DurableFeedbackRetry>> LoadAsync(
        CancellationToken cancellationToken)
        => _store.LoadAsync(cancellationToken);

    public async Task<ImmutableArray<DurableFeedbackRetry>> ScheduleAsync(
        ImmutableArray<FeedbackRetryMetadata> retries,
        CancellationToken cancellationToken)
    {
        ImmutableArray<DurableFeedbackRetry> existing = await _store
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<DurableFeedbackRetry> updated = Merge(
            existing,
            retries,
            retries.Select(static item => item.PullRequestNumber).ToHashSet());
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<ImmutableArray<DurableFeedbackRetry>> RecordResultAsync(
        FeedbackResult result,
        IEnumerable<long> processedPullRequests,
        CancellationToken cancellationToken)
    {
        ImmutableArray<DurableFeedbackRetry> existing = await _store
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<DurableFeedbackRetry> updated = Merge(
            existing,
            result.RetryMetadata,
            processedPullRequests.ToHashSet());
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<(FeedbackResult Result, ImmutableArray<DurableFeedbackRetry> Pending)>
        ReplayPendingAsync(
        GitHubFeedbackWorkflow workflow,
        RepositoryCoordinates upstream,
        ImmutableArray<PullRequestObservation> observations,
        FeedbackPolicy policy,
        CancellationToken cancellationToken)
    {
        ImmutableArray<DurableFeedbackRetry> existing = await _store
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<long> due = existing
            .Where(item => item.RetryAfter <= _clock.UtcNow)
            .Select(static item => item.PullRequestNumber)
            .ToHashSet();
        ImmutableArray<PullRequestObservation> selected =
        [
            .. observations.Where(observation =>
                due.Contains(observation.PullRequest.Number)),
        ];
        FeedbackResult result = await workflow.ProcessAsync(
            upstream,
            selected,
            policy,
            cancellationToken).ConfigureAwait(false);
        HashSet<long> resolved = result.Statuses
            .Where(static status =>
                status.RecommendedAction == PullRequestLifecycleAction.RepairManifest)
            .Select(static status => status.PullRequestNumber)
            .ToHashSet();
        HashSet<long> processed = selected
            .Select(static item => item.PullRequest.Number)
            .ToHashSet();
        ImmutableArray<FeedbackRetryMetadata> remainingRetries =
        [
            .. result.RetryMetadata.Where(item =>
                !resolved.Contains(item.PullRequestNumber)),
        ];
        ImmutableArray<DurableFeedbackRetry> updated = Merge(
            existing,
            remainingRetries,
            processed);
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return (result, updated);
    }

    private static ImmutableArray<DurableFeedbackRetry> Merge(
        ImmutableArray<DurableFeedbackRetry> existing,
        ImmutableArray<FeedbackRetryMetadata> replacements,
        HashSet<long> replacedPullRequests)
        =>
        [
            .. existing
                .Where(item => !replacedPullRequests.Contains(item.PullRequestNumber))
                .Concat(replacements.Select(static item => new DurableFeedbackRetry(
                    item.PullRequestNumber,
                    item.Classification,
                    item.RetryAfter,
                    item.LearnedOverrideSignal)))
                .OrderBy(static item => item.PullRequestNumber),
        ];
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

        (PackageIdentifier identifier, PackageVersion version) = ParseAssociation(
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
            Operation = GitHubManifestOperation.Update,
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

    private static (PackageIdentifier Identifier, PackageVersion Version) ParseAssociation(
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
        string? package = parts.FirstOrDefault(static part =>
            part.StartsWith("package=", StringComparison.Ordinal))?["package=".Length..];
        // The marker prefix already consumed package=, so the first segment is the identifier.
        package ??= parts[0];
        string? version = parts.FirstOrDefault(static part =>
            part.StartsWith("version=", StringComparison.Ordinal))?["version=".Length..];
        return (
            new PackageIdentifier(package),
            new PackageVersion(version
                ?? throw new CliOperationException(
                    "Approved repair association marker is missing the version.")));
    }
}
