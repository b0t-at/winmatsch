using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Repository maintenance commands: <c>sync</c> (fork synchronization), <c>cleanup</c>
/// (stale tool branch inspection), <c>complete</c> (open tool PR lifecycle), and the hidden
/// <c>remove-dead-versions</c>. Every command plans before it applies, requires explicit
/// confirmation for mutation, never touches branches or pull requests it cannot prove are
/// tool-owned, and escalates to a human whenever remote state is uncertain.
/// </summary>
public sealed class MaintenanceCommandModule : ICommandModule
{
    private readonly Func<string, IGitHubRepositoryClient>? _clientFactory;
    private readonly Func<IGitHubRepositoryClient, IDeadVersionInspector> _inspectorFactory;
    private readonly Func<IGitHubRepositoryClient, string, IPullRequestFeedbackSource>? _sourceFactory;
    private readonly Func<IGitHubRepositoryClient, GitHubFeedbackWorkflow>? _feedbackFactory;
    private readonly IApprovedRepairPlannerFactory _repairPlannerFactory;
    private readonly IFeedbackStateStore _feedbackStateStore;
    private readonly IWorkflowClock _clock;
    private readonly ISubmissionJournalStore? _submissionJournals;

    public MaintenanceCommandModule(
        Func<string, IGitHubRepositoryClient>? clientFactory = null,
        Func<IGitHubRepositoryClient, IDeadVersionInspector>? inspectorFactory = null,
        Func<IGitHubRepositoryClient, string, IPullRequestFeedbackSource>? sourceFactory = null,
        Func<IGitHubRepositoryClient, GitHubFeedbackWorkflow>? feedbackFactory = null,
        IApprovedRepairPlannerFactory? repairPlannerFactory = null,
        IFeedbackStateStore? feedbackStateStore = null,
        IWorkflowClock? clock = null,
        ISubmissionJournalStore? submissionJournals = null)
    {
        _clientFactory = clientFactory;
        _inspectorFactory = inspectorFactory
            ?? (client => new GitHubDeadVersionInspector(client, new HttpInstallerUrlProber()));
        _sourceFactory = sourceFactory;
        _clock = clock ?? new SystemWorkflowClock();
        _feedbackFactory = feedbackFactory;
        _repairPlannerFactory = repairPlannerFactory
            ?? new CliApprovedRepairPlannerFactory(new ProductionMutationWorkflowFactory());
        _feedbackStateStore = feedbackStateStore ?? new NoOpFeedbackStateStore();
        _submissionJournals = submissionJournals;
    }

    public string Name => "maintenance";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RegisterSync(registry);
        RegisterCleanup(registry);
        RegisterComplete(registry);
        RegisterSubmissions(registry);
        RegisterRemoveDeadVersions(registry);
    }

    private void RegisterSubmissions(ICommandRegistry registry)
    {
        var command = new Command(
            "submissions",
            "List pending local-to-GitHub submission recovery journals.");
        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            ImmutableArray<SubmissionJournalEntry> entries;
            try
            {
                ISubmissionJournalStore journals = _submissionJournals
                    ?? WorkflowProductionComposition.CreateSubmissionJournal(
                        context.Configuration.OverrideStoreDirectory is null
                            ? null
                            : new OverridePackStoreOptions
                            {
                                RootDirectory = context.Configuration.OverrideStoreDirectory,
                            });
                entries = await journals.ListPendingAsync(context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CliOperationException(
                    $"Listing pending submissions failed: {exception.Message}",
                    exception);
            }

            context.Output.WriteFormatted(
                writer =>
                {
                    writer.WriteLine("Pending submissions:");
                    if (entries.IsEmpty)
                    {
                        writer.WriteLine("  (none)");
                    }

                    foreach (SubmissionJournalEntry entry in entries)
                    {
                        writer.WriteLine(
                            $"  {entry.Id} r{entry.Revision} [{entry.State}] "
                            + $"{entry.LocalPlan.PackageIdentifier.Value} "
                            + $"{entry.LocalPlan.PackageVersion.Value}"
                            + (entry.RemoteState.PullRequestNumber is { } number
                                ? $" PR #{number}"
                                : ""));
                    }
                },
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("pendingSubmissions");
                    foreach (SubmissionJournalEntry entry in entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", entry.Id);
                        writer.WriteNumber("revision", entry.Revision);
                        writer.WriteString(
                            "state",
                            MaintenanceCommandHelpers.ToCamelCase(entry.State));
                        writer.WriteString(
                            "packageIdentifier",
                            entry.LocalPlan.PackageIdentifier.Value);
                        writer.WriteString(
                            "packageVersion",
                            entry.LocalPlan.PackageVersion.Value);
                        writer.WriteString(
                            "upstreamRepository",
                            entry.RemoteRequest.UpstreamRepository.ToString());
                        if (entry.RemoteState.PullRequestNumber is { } number)
                        {
                            writer.WriteNumber("pullRequestNumber", number);
                        }

                        writer.WriteBoolean(
                            "remoteOutcomeUncertain",
                            entry.RemoteState.RemoteOutcomeUncertain);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            return ExitCodes.Success;
        });
    }

    private void RegisterSync(ICommandRegistry registry)
    {
        var fork = CreateForkOption();
        var yes = CreateYesOption();
        var command = new Command(
            "sync",
            "Synchronize the fork's default branch with the upstream repository. "
            + "Plans first; applying requires confirmation and never force-updates user commits.")
        {
            Options = { fork, yes },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            RepositoryCoordinates upstream = context.Configuration.Repository;
            using IGitHubRepositoryClient client = await CreateClientAsync(context).ConfigureAwait(false);
            RepositoryCoordinates forkRepository = await ResolveForkAsync(context, client, fork, upstream)
                .ConfigureAwait(false);
            BranchState upstreamBranch = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Fork synchronization failed",
                () => client.GetDefaultBranchAsync(upstream, context.CancellationToken))
                .ConfigureAwait(false);
            BranchState forkBranch = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Fork synchronization failed",
                () => client.GetDefaultBranchAsync(forkRepository, context.CancellationToken))
                .ConfigureAwait(false);

            var workflow = new GitHubMaintenanceWorkflow(client, _clock);
            string idempotencyKey = $"cli:sync:{upstream}:{forkRepository}";
            GitHubMaintenanceResult result = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Fork synchronization failed",
                () => workflow.SyncAsync(
                    new GitHubSyncRequest(upstream, forkRepository, WorkflowExecutionMode.Plan, idempotencyKey),
                    context.CancellationToken))
                .ConfigureAwait(false);

            if (!context.IsDryRun && result.Code == GitHubLifecycleResultCode.Planned)
            {
                bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                    context,
                    context.ParseResult.GetValue(yes),
                    $"Merge upstream {upstream} into the default branch of fork {forkRepository}?")
                    .ConfigureAwait(false);
                if (!confirmed)
                {
                    context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was changed.");
                    return ExitCodes.OperationFailed;
                }

                result = await MaintenanceCommandHelpers.RunRemoteAsync(
                    context,
                    "Fork synchronization failed",
                    () => workflow.SyncAsync(
                        new GitHubSyncRequest(
                            upstream,
                            forkRepository,
                            WorkflowExecutionMode.Apply,
                            idempotencyKey),
                        context.CancellationToken))
                    .ConfigureAwait(false);
            }

            WriteMaintenanceResult(
                context,
                "sync",
                upstream,
                forkRepository,
                upstreamBranch,
                forkBranch,
                result);
            return MaintenanceCommandHelpers.MapResultCode(
                result.Code,
                context.CancellationToken);
        });
    }

    private void RegisterCleanup(ICommandRegistry registry)
    {
        var fork = CreateForkOption();
        var yes = CreateYesOption();
        var branchPrefix = new Option<string>("--branch-prefix")
        {
            Description = "Tool branch prefix considered for cleanup (default: winmatsch/). "
                + "Branches outside this prefix are never candidates.",
            HelpName = "prefix",
            DefaultValueFactory = _ => ToolPullRequestObservationSource.ToolBranchPrefix,
        };
        var command = new Command(
            "cleanup",
            "Inspect stale tool-created branches whose pull requests are closed. GitHub offers "
            + "no atomic expected-SHA branch delete, so candidates are rendered for manual "
            + "escalation; unknown or user branches are never touched.")
        {
            Options = { fork, branchPrefix, yes },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            RepositoryCoordinates upstream = context.Configuration.Repository;
            using IGitHubRepositoryClient client = await CreateClientAsync(context).ConfigureAwait(false);
            RepositoryCoordinates forkRepository = await ResolveForkAsync(context, client, fork, upstream)
                .ConfigureAwait(false);
            string prefix = context.ParseResult.GetValue(branchPrefix)
                ?? ToolPullRequestObservationSource.ToolBranchPrefix;
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new CliUsageException("--branch-prefix must not be empty.");
            }

            var workflow = new GitHubMaintenanceWorkflow(client, _clock);
            string idempotencyKey = $"cli:cleanup:{upstream}:{forkRepository}";
            GitHubMaintenanceResult result = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Branch cleanup inspection failed",
                () => workflow.CleanupAsync(
                    new GitHubCleanupRequest(
                        upstream,
                        forkRepository,
                        WorkflowExecutionMode.Plan,
                        idempotencyKey,
                        prefix),
                    context.CancellationToken))
                .ConfigureAwait(false);

            if (!context.IsDryRun && result.Code == GitHubLifecycleResultCode.Planned)
            {
                bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                    context,
                    context.ParseResult.GetValue(yes),
                    $"Evaluate cleanup of {result.Plan.Operations.Length} stale tool branch(es) on {forkRepository}?")
                    .ConfigureAwait(false);
                if (!confirmed)
                {
                    context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was changed.");
                    return ExitCodes.OperationFailed;
                }

                result = await MaintenanceCommandHelpers.RunRemoteAsync(
                    context,
                    "Branch cleanup failed",
                    () => workflow.CleanupAsync(
                        new GitHubCleanupRequest(
                            upstream,
                            forkRepository,
                            WorkflowExecutionMode.Apply,
                            idempotencyKey,
                            prefix),
                        context.CancellationToken))
                    .ConfigureAwait(false);
            }

            WriteMaintenanceResult(context, "cleanup", upstream, forkRepository, null, null, result);
            if (result.Code == GitHubLifecycleResultCode.HumanEscalationRequired)
            {
                context.Output.WriteError(
                    "Cleanup requires human escalation: delete the listed branches manually after "
                    + "verifying no new commits were pushed. Nothing was deleted.");
            }

            return MaintenanceCommandHelpers.MapResultCode(
                result.Code,
                context.CancellationToken);
        });
    }

    private void RegisterComplete(ICommandRegistry registry)
    {
        var fork = CreateForkOption();
        var yes = CreateYesOption();
        var applySafe = new Option<bool>("--apply-safe")
        {
            Description = "After inspection, apply only known-safe responses (fixed keep-alive "
                + "comments for transient infrastructure failures). Requires confirmation; "
                + "never posts arbitrary comments and never repairs manifests.",
        };
        var schedulePending = new Option<bool>("--schedule-pending")
        {
            Description = "Persist the classified retry schedule without remote writes. "
                + "Requires confirmation and never runs a repair.",
        };
        var replayPending = new Option<bool>("--replay-pending")
        {
            Description = "Explicitly replay only due durable retry entries. Requires "
                + "--apply-safe and confirmation.",
        };
        var approvedRepair = new Option<string[]>("--approved-repair")
        {
            Description = "Allowlisted repair input as PR_NUMBER=MANIFEST_DIRECTORY. May be "
                + "repeated; only duplicate/hash classifications can consume it.",
        };
        var command = new Command(
            "complete",
            "Inspect the lifecycle of open tool-created pull requests and report the "
            + "recommended action for each.")
        {
            Options =
            {
                fork,
                applySafe,
                schedulePending,
                replayPending,
                approvedRepair,
                yes,
            },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            bool apply = context.ParseResult.GetValue(applySafe);
            bool schedule = context.ParseResult.GetValue(schedulePending);
            bool replay = context.ParseResult.GetValue(replayPending);
            Dictionary<long, string> approvedRepairs = ParseApprovedRepairs(
                context.ParseResult.GetValue(approvedRepair));
            if (schedule && replay)
            {
                throw new CliUsageException(
                    "--schedule-pending and --replay-pending are mutually exclusive.");
            }

            if (replay && !apply)
            {
                throw new CliUsageException("--replay-pending requires --apply-safe.");
            }

            if (approvedRepairs.Count > 0 && !apply)
            {
                throw new CliUsageException("--approved-repair requires --apply-safe.");
            }

            RepositoryCoordinates upstream = context.Configuration.Repository;
            using IGitHubRepositoryClient client = await CreateClientAsync(context).ConfigureAwait(false);
            RepositoryCoordinates forkRepository = await ResolveForkAsync(context, client, fork, upstream)
                .ConfigureAwait(false);
            IPullRequestFeedbackSource source;
            if (_sourceFactory is not null)
            {
                source = _sourceFactory(client, forkRepository.Owner);
            }
            else
            {
                ResolvedToken token = await context.Tokens
                    .RequireAsync(context.CancellationToken)
                    .ConfigureAwait(false);
                source = new ToolPullRequestObservationSource(
                    client,
                    forkRepository.Owner,
                    new GitHubPullRequestMetadataSource(
                        context.GitHubOptions,
                        token.Token.RevealValue()));
            }

            using IDisposable? sourceLease = source as IDisposable;
            ImmutableArray<PullRequestObservation> observations = await MaintenanceCommandHelpers
                .RunRemoteAsync(
                    context,
                    "Pull request inspection failed",
                    () => source.GetOpenToolPullRequestsAsync(upstream, context.CancellationToken))
                .ConfigureAwait(false);
            ImmutableArray<FeedbackWorkItem> pending = await MaintenanceCommandHelpers
                .RunRemoteAsync(
                    context,
                    "Feedback state read failed",
                    () => _feedbackStateStore.GetPendingAsync(
                        upstream.ToString(),
                        _clock.UtcNow,
                        context.CancellationToken))
                .ConfigureAwait(false);
            using InstallerDownloader? feedbackDownloader = _feedbackFactory is null
                ? new InstallerDownloader(new DownloaderOptions
                {
                    CacheDirectory = context.Configuration.CacheEnabled
                        ? context.Configuration.CacheDirectory
                        : null,
                })
                : null;
            IApprovedRepairPlanner planner = _repairPlannerFactory.Create(
                context,
                context.IsDryRun || !apply || schedule
                    ? new Dictionary<long, string>()
                    : approvedRepairs);
            using IDisposable? plannerLease = planner as IDisposable;
            IFeedbackStateStore stateForRun = replay && context.IsDryRun
                ? new ReadOnlyFeedbackStateStore(_feedbackStateStore)
                : schedule && !context.IsDryRun || apply && !context.IsDryRun
                    ? _feedbackStateStore
                    : new NoOpFeedbackStateStore();
            GitHubFeedbackWorkflow feedback = _feedbackFactory?.Invoke(client)
                ?? CreateDefaultFeedbackWorkflow(
                    client,
                    planner,
                    feedbackDownloader!,
                    stateForRun);
            if (!apply || context.IsDryRun || schedule)
            {
                if (schedule && !context.IsDryRun)
                {
                    bool confirmedSchedule = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                        context,
                        context.ParseResult.GetValue(yes),
                        "Classify open tool pull requests and persist their native retry state?")
                        .ConfigureAwait(false);
                    if (!confirmedSchedule)
                    {
                        context.Output.WriteDiagnostic(
                            "Aborted: confirmation declined; retry state was unchanged.");
                        return ExitCodes.OperationFailed;
                    }
                }

                FeedbackResult inspection = await MaintenanceCommandHelpers.RunRemoteAsync(
                    context,
                    "Pull request inspection failed",
                    () => replay && context.IsDryRun
                        ? feedback.ReplayPendingAsync(
                            upstream,
                            source,
                            new FeedbackPolicy { ApplyKnownSafeResponses = false },
                            context.CancellationToken)
                        : feedback.ProcessAsync(
                            upstream,
                            observations,
                            new FeedbackPolicy { ApplyKnownSafeResponses = false },
                            context.CancellationToken))
                    .ConfigureAwait(false);
                if (schedule && !context.IsDryRun)
                {
                    pending = await MaintenanceCommandHelpers.RunRemoteAsync(
                        context,
                        "Feedback state read failed",
                        () => _feedbackStateStore.GetPendingAsync(
                            upstream.ToString(),
                            DateTimeOffset.MaxValue,
                            context.CancellationToken)).ConfigureAwait(false);
                }

                ImmutableArray<FeedbackWorkItem> displayedPending = ProjectPending(
                    upstream,
                    pending,
                    inspection);

                GitHubCompleteResult lifecycle = GitHubMaintenanceWorkflow.Complete(observations);
                Dictionary<long, PullRequestLifecycleStatus> classified =
                    inspection.Statuses.ToDictionary(
                        static status => status.PullRequestNumber);
                ImmutableArray<PullRequestLifecycleStatus> statuses =
                [
                    .. lifecycle.PullRequests.Select(status =>
                        classified.GetValueOrDefault(status.PullRequestNumber) ?? status),
                ];
                WriteCompleteResult(
                    context,
                    upstream,
                    forkRepository,
                    statuses,
                    [.. lifecycle.Diagnostics, .. inspection.Diagnostics],
                    applied: false,
                    pending: displayedPending);
                return schedule
                    && inspection.Diagnostics.Any(static diagnostic =>
                        diagnostic.Code == "GH3208")
                    ? ExitCodes.OperationFailed
                    : ExitCodes.Success;
            }

            bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                context,
                context.ParseResult.GetValue(yes),
                replay
                    ? $"Replay due durable feedback entries on {upstream}?"
                    : $"Apply known-safe responses to open tool pull requests on {upstream}?")
                .ConfigureAwait(false);
            if (!confirmed)
            {
                context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was changed.");
                return ExitCodes.OperationFailed;
            }

            FeedbackResult result;
            if (replay)
            {
                result = await MaintenanceCommandHelpers.RunRemoteAsync(
                    context,
                    "Pending feedback replay failed",
                    () => feedback.ReplayPendingAsync(
                        upstream,
                        source,
                        new FeedbackPolicy { ApplyKnownSafeResponses = true },
                        context.CancellationToken)).ConfigureAwait(false);
            }
            else
            {
                result = await MaintenanceCommandHelpers.RunRemoteAsync(
                    context,
                    "Known-safe pull request completion failed",
                    () => feedback.ProcessAsync(
                        upstream,
                        observations,
                        new FeedbackPolicy { ApplyKnownSafeResponses = true },
                        context.CancellationToken))
                    .ConfigureAwait(false);
            }
            pending = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Feedback state read failed",
                () => _feedbackStateStore.GetPendingAsync(
                    upstream.ToString(),
                    DateTimeOffset.MaxValue,
                    context.CancellationToken)).ConfigureAwait(false);
            ImmutableArray<FeedbackWorkItem> displayedAfterApply = ProjectPending(
                upstream,
                pending,
                result);
            bool appliedKnownSafeResponse = result.Statuses.Any(static status =>
                    status.RecommendedAction == PullRequestLifecycleAction.RerunChecks)
                || result.Statuses.Any(status =>
                    status.RecommendedAction == PullRequestLifecycleAction.RepairManifest
                    && result.RemoteStates.Any(remote =>
                        remote.PullRequestNumber == status.PullRequestNumber
                        && !remote.State.RemoteOutcomeUncertain));
            WriteCompleteResult(
                context,
                upstream,
                forkRepository,
                result.Statuses,
                result.Diagnostics,
                applied: appliedKnownSafeResponse,
                pending: displayedAfterApply);
            if (result.RemoteStates.Any(static state => state.State.RemoteOutcomeUncertain))
            {
                context.Output.WriteError(
                    "Warning: at least one remote outcome is uncertain; verify the listed "
                    + "pull requests manually before retrying.");
            }

            if (context.CancellationToken.IsCancellationRequested)
            {
                context.Output.WriteError("The operation was cancelled during remote processing.");
                return ExitCodes.Cancelled;
            }

            return result.Diagnostics.IsEmpty ? ExitCodes.Success : ExitCodes.OperationFailed;
        });
    }

    private void RegisterRemoveDeadVersions(ICommandRegistry registry)
    {
        var package = new Argument<string>("package")
        {
            Description = "Exact package identifier, including repository casing.",
        };
        var version = new Argument<string>("version")
        {
            Description = "One exact package version. Repository policy requires one removal "
                + "pull request per invocation.",
        };
        var yes = CreateYesOption();
        var command = new Command(
            "remove-dead-versions",
            "Plan removal of package versions whose installers are permanently gone. Transient "
            + "or blocked download failures escalate instead of counting as dead.")
        {
            Arguments = { package, version },
            Options = { yes },
            Hidden = true,
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            PackageIdentifier packageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package));
            ImmutableArray<(PackageIdentifier, PackageVersion)> requested =
            [
                (packageIdentifier, ParseVersion(context.ParseResult.GetValue(version))),
            ];

            RepositoryCoordinates upstream = context.Configuration.Repository;
            using IGitHubRepositoryClient client = await CreateClientAsync(context).ConfigureAwait(false);
            using IDeadVersionInspector inspector = _inspectorFactory(client);
            var workflow = new RemoveDeadVersionsWorkflow(inspector);
            var request = new RemoveDeadVersionsRequest(upstream, requested);
            ImmutableArray<RemoveDeadVersionPlan> plans = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Dead version inspection failed",
                () => workflow.PlanAsync(request, context.CancellationToken))
                .ConfigureAwait(false);

            bool allRemovable = plans.All(static plan => plan.CanRemove);
            if (context.IsDryRun || !allRemovable)
            {
                bool indeterminate = plans.Any(static plan =>
                    plan.Diagnostics.Any(static diagnostic => diagnostic.Code == "GH3103"));
                WriteRemoveDeadVersionsResult(
                    context,
                    upstream,
                    plans,
                    escalated: indeterminate);
                return allRemovable ? ExitCodes.Success : ExitCodes.OperationFailed;
            }

            bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                context,
                context.ParseResult.GetValue(yes),
                $"Prepare removal of {plans.Length} dead version(s) of {packageIdentifier.Value} from {upstream}?")
                .ConfigureAwait(false);
            if (!confirmed)
            {
                context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was changed.");
                return ExitCodes.OperationFailed;
            }

            // Revalidate immediately before hand-off so the removal decision rests on the
            // freshest upstream and origin state.
            plans = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Dead version revalidation failed",
                () => workflow.PlanAsync(request, context.CancellationToken))
                .ConfigureAwait(false);
            bool stillRemovable = plans.All(static plan => plan.CanRemove);
            bool revalidationIndeterminate = plans.Any(static plan =>
                plan.Diagnostics.Any(static diagnostic => diagnostic.Code == "GH3103"));
            WriteRemoveDeadVersionsResult(
                context,
                upstream,
                plans,
                escalated: stillRemovable || revalidationIndeterminate);
            if (!stillRemovable)
            {
                return ExitCodes.OperationFailed;
            }

            context.Output.WriteError(
                "Human escalation required: automated dead-version removal submission is not "
                + "wired up yet. Submit one removal pull request per listed version manually.");
            return ExitCodes.OperationFailed;
        });
    }

    private async Task<IGitHubRepositoryClient> CreateClientAsync(CommandContext context)
    {
        ResolvedToken token = await context.Tokens
            .RequireAsync(context.CancellationToken)
            .ConfigureAwait(false);
        return _clientFactory is not null
            ? _clientFactory(token.Token.RevealValue())
            : new RedactingGitHubRepositoryClient(new GitHubRepositoryClient(
                new HttpClient(),
                token.Token.RevealValue(),
                context.GitHubOptions));
    }

    private static async Task<RepositoryCoordinates> ResolveForkAsync(
        CommandContext context,
        IGitHubRepositoryClient client,
        Option<string?> forkOption,
        RepositoryCoordinates upstream)
    {
        string? forkValue = context.ParseResult.GetValue(forkOption);
        if (forkValue is not null)
        {
            return MaintenanceCommandHelpers.ParseRepository(forkValue, "--fork");
        }

        GitHubUser user = await MaintenanceCommandHelpers.RunRemoteAsync(
            context,
            "Fork resolution failed",
            () => client.GetAuthenticatedUserAsync(context.CancellationToken))
            .ConfigureAwait(false);
        return new RepositoryCoordinates(user.Login, upstream.Name);
    }

    private static Option<string?> CreateForkOption() => new("--fork")
    {
        Description = "Fork repository in owner/name form (default: <authenticated user>/<upstream name>).",
        HelpName = "owner/name",
    };

    private static Option<bool> CreateYesOption() => new("--yes")
    {
        Description = "Confirm mutating actions without prompting. Required in non-interactive "
            + "and JSON sessions; confirmation never defaults to yes.",
    };

    private GitHubFeedbackWorkflow CreateDefaultFeedbackWorkflow(
        IGitHubRepositoryClient client,
        IApprovedRepairPlanner planner,
        InstallerDownloader downloader,
        IFeedbackStateStore stateStore)
        => new(
            client,
            WorkflowProductionComposition.CreateGitHubLifecycle(
                client,
                downloader,
                _clock),
            planner,
            _clock,
            stateStore);

    private ImmutableArray<FeedbackWorkItem> ProjectPending(
        RepositoryCoordinates repository,
        ImmutableArray<FeedbackWorkItem> stored,
        FeedbackResult result)
    {
        HashSet<long> completedRepairs =
        [
            .. result.RemoteStates
                .Where(static remote => !remote.State.RemoteOutcomeUncertain)
                .Select(static remote => remote.PullRequestNumber),
        ];
        HashSet<long> terminal =
        [
            .. result.Statuses
                .Where(static status =>
                    status.RecommendedAction == PullRequestLifecycleAction.EscalateToHuman)
                .Select(static status => status.PullRequestNumber),
            .. completedRepairs,
        ];
        Dictionary<long, FeedbackWorkItem> projected = stored
            .Where(item => !terminal.Contains(item.PullRequestNumber))
            .ToDictionary(static item => item.PullRequestNumber);
        foreach (FeedbackRetryMetadata retry in result.RetryMetadata.Where(item =>
                     !terminal.Contains(item.PullRequestNumber)))
        {
            projected[retry.PullRequestNumber] = new(
                repository.ToString(),
                retry.PullRequestNumber,
                retry.Classification,
                retry.Classification is FeedbackClassification.DuplicateEntry
                    or FeedbackClassification.HashMismatch
                        ? FeedbackWorkState.AwaitingApprovedRepair
                        : FeedbackWorkState.RetryScheduled,
                _clock.UtcNow,
                retry.RetryAfter,
                retry.LearnedOverrideSignal,
                "Pending retry or approved repair.");
        }

        return
        [
            .. projected.Values.OrderBy(static item => item.PullRequestNumber),
        ];
    }

    private static Dictionary<long, string> ParseApprovedRepairs(
        string[]? values)
    {
        var result = new Dictionary<long, string>();
        foreach (string value in values ?? [])
        {
            int separator = value.IndexOf('=');
            if (separator <= 0
                || separator == value.Length - 1
                || !long.TryParse(
                    value[..separator],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long pullRequestNumber)
                || pullRequestNumber <= 0)
            {
                throw new CliUsageException(
                    "--approved-repair must use PR_NUMBER=MANIFEST_DIRECTORY syntax.");
            }

            if (!result.TryAdd(pullRequestNumber, value[(separator + 1)..]))
            {
                throw new CliUsageException(
                    $"--approved-repair specifies PR #{pullRequestNumber} more than once.");
            }
        }

        return result;
    }

    private static PackageIdentifier ParseIdentifier(string? value)
    {
        try
        {
            return new PackageIdentifier(
                value ?? throw new ArgumentException("A package identifier is required."));
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException($"Invalid package identifier: {exception.Message}", exception);
        }
    }

    private static PackageVersion ParseVersion(string? value)
    {
        try
        {
            return new PackageVersion(
                value ?? throw new ArgumentException("A package version is required."));
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException($"Invalid package version: {exception.Message}", exception);
        }
    }

    private static void WriteMaintenanceResult(
        CommandContext context,
        string operation,
        RepositoryCoordinates upstream,
        RepositoryCoordinates fork,
        BranchState? upstreamBranch,
        BranchState? forkBranch,
        GitHubMaintenanceResult result)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Operation: {operation}");
                writer.WriteLine($"Upstream: {upstream}"
                    + (upstreamBranch is null ? "" : $" ({upstreamBranch.Name} @ {upstreamBranch.HeadSha})"));
                writer.WriteLine($"Fork: {fork}"
                    + (forkBranch is null ? "" : $" ({forkBranch.Name} @ {forkBranch.HeadSha})"));
                writer.WriteLine($"Result: {MaintenanceCommandHelpers.ToCamelCase(result.Code)}");
                MaintenanceCommandHelpers.WritePlanText(writer, result.Plan, result.Diagnostics);
                foreach (GitHubLifecycleAuditEntry entry in result.Audit)
                {
                    writer.WriteLine(
                        $"Audit {entry.Code} at {MaintenanceCommandHelpers.FormatTimestamp(entry.Timestamp)}: {entry.Message}");
                }

                if (result.RemoteState.RemoteOutcomeUncertain)
                {
                    writer.WriteLine("Warning: the remote outcome is uncertain; verify manually before retrying.");
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("operation", operation);
                writer.WriteString("upstream", upstream.ToString());
                writer.WriteString("fork", fork.ToString());
                if (upstreamBranch is not null)
                {
                    writer.WriteStartObject("upstreamDefaultBranch");
                    writer.WriteString("name", upstreamBranch.Name);
                    writer.WriteString("headSha", upstreamBranch.HeadSha);
                    writer.WriteEndObject();
                }

                if (forkBranch is not null)
                {
                    writer.WriteStartObject("forkDefaultBranch");
                    writer.WriteString("name", forkBranch.Name);
                    writer.WriteString("headSha", forkBranch.HeadSha);
                    writer.WriteEndObject();
                }

                CliJson.WriteEnum(writer, "result", result.Code);
                MaintenanceCommandHelpers.WritePlanJson(writer, result.Plan, result.Diagnostics);
                writer.WriteBoolean("remoteOutcomeUncertain", result.RemoteState.RemoteOutcomeUncertain);
                writer.WriteEndObject();
            });

    private static void WriteCompleteResult(
        CommandContext context,
        RepositoryCoordinates upstream,
        RepositoryCoordinates fork,
        ImmutableArray<PullRequestLifecycleStatus> statuses,
        ImmutableArray<GitHubLifecycleDiagnostic> diagnostics,
        bool applied,
        ImmutableArray<FeedbackWorkItem> pending)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Operation: complete{(applied ? " (known-safe responses applied)" : "")}");
                writer.WriteLine($"Upstream: {upstream}");
                writer.WriteLine($"Fork: {fork}");
                writer.WriteLine("Pull requests:");
                if (statuses.IsEmpty)
                {
                    writer.WriteLine("  (none)");
                }

                foreach (PullRequestLifecycleStatus status in statuses)
                {
                    writer.WriteLine(
                        $"  #{status.PullRequestNumber} [{status.Status}] "
                        + $"{MaintenanceCommandHelpers.ToCamelCase(status.RecommendedAction)}: {status.Reason}");
                }

                writer.WriteLine("Pending retries:");
                if (pending.IsEmpty)
                {
                    writer.WriteLine("  (none)");
                }

                foreach (FeedbackWorkItem item in pending)
                {
                    writer.WriteLine(
                        $"  #{item.PullRequestNumber} "
                        + $"{MaintenanceCommandHelpers.ToCamelCase(item.Classification)} "
                        + $"{MaintenanceCommandHelpers.ToCamelCase(item.State)} "
                        + (item.RetryAfter is { } retryAfter
                            ? $"after {MaintenanceCommandHelpers.FormatTimestamp(retryAfter)}"
                            : "(unscheduled)")
                        + (item.LearnedOverrideSignal is null
                            ? ""
                            : $" learned={item.LearnedOverrideSignal}"));
                }

                MaintenanceCommandHelpers.WriteDiagnosticsText(writer, diagnostics);
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("operation", "complete");
                writer.WriteBoolean("appliedKnownSafeResponses", applied);
                writer.WriteString("upstream", upstream.ToString());
                writer.WriteString("fork", fork.ToString());
                writer.WriteStartArray("pullRequests");
                foreach (PullRequestLifecycleStatus status in statuses)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("number", status.PullRequestNumber);
                    writer.WriteString("status", status.Status);
                    CliJson.WriteEnum(
                        writer,
                        "recommendedAction",
                        status.RecommendedAction);
                    writer.WriteString("reason", status.Reason);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("pendingRetries");
                foreach (FeedbackWorkItem item in pending)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("pullRequestNumber", item.PullRequestNumber);
                    CliJson.WriteEnum(writer, "classification", item.Classification);
                    CliJson.WriteEnum(writer, "state", item.State);
                    if (item.RetryAfter is { } retryAfter)
                    {
                        writer.WriteString("retryAfter", retryAfter);
                    }
                    else
                    {
                        writer.WriteNull("retryAfter");
                    }

                    writer.WriteString("reason", item.Reason);
                    if (item.LearnedOverrideSignal is null)
                    {
                        writer.WriteNull("learnedOverrideSignal");
                    }
                    else
                    {
                        writer.WriteString(
                            "learnedOverrideSignal",
                            item.LearnedOverrideSignal);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                MaintenanceCommandHelpers.WriteDiagnosticsJson(writer, diagnostics);
                writer.WriteEndObject();
            });

    private static void WriteRemoveDeadVersionsResult(
        CommandContext context,
        RepositoryCoordinates upstream,
        ImmutableArray<RemoveDeadVersionPlan> plans,
        bool escalated)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine("Operation: remove-dead-versions");
                writer.WriteLine($"Upstream: {upstream}");
                writer.WriteLine("Versions:");
                foreach (RemoveDeadVersionPlan plan in plans)
                {
                    writer.WriteLine(
                        $"  {plan.PackageIdentifier.Value} {plan.PackageVersion.Value}: "
                        + (plan.CanRemove ? "removable" : "not removable"));
                    foreach (GitHubLifecycleDiagnostic diagnostic in plan.Diagnostics)
                    {
                        writer.WriteLine($"    {diagnostic.Code}: {diagnostic.Message}");
                    }
                }

                if (escalated)
                {
                    writer.WriteLine(
                        "Escalation: human review is required for indeterminate checks or "
                        + "manual removal submission.");
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("operation", "remove-dead-versions");
                writer.WriteString("upstream", upstream.ToString());
                writer.WriteStartArray("versions");
                foreach (RemoveDeadVersionPlan plan in plans)
                {
                    writer.WriteStartObject();
                    writer.WriteString("packageIdentifier", plan.PackageIdentifier.Value);
                    writer.WriteString("packageVersion", plan.PackageVersion.Value);
                    writer.WriteBoolean("canRemove", plan.CanRemove);
                    MaintenanceCommandHelpers.WriteDiagnosticsJson(writer, plan.Diagnostics);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteBoolean("humanEscalationRequired", escalated);
                writer.WriteEndObject();
            });

}
