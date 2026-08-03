using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Rules;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;

namespace WinMatsch.Workflows.Operations;

/// <summary>Reflection-free production composition for local and GitHub workflows.</summary>
public static class WorkflowProductionComposition
{
    public static LocalWorkflowEngine CreateLocalEngine(
        InstallerDownloader downloader,
        IWorkflowReleaseSource? releaseSource = null,
        IWorkflowClock? clock = null,
        OverridePackStoreOptions? overridePackStoreOptions = null,
        IManifestSnapshotSource? fallbackManifestSource = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        var originalSubmissions = new FileOriginalSubmissionStore();
        var network = new DurableInstallerPreflightNetwork(downloader);
        var preflight = new PreflightGateWorkflowAdapter(
            new PreflightGate(network),
            network);
        IManifestSnapshotSource manifests = new LocalManifestSnapshotSource(originalSubmissions);
        if (fallbackManifestSource is not null)
        {
            manifests = new FallbackManifestSnapshotSource(manifests, fallbackManifestSource);
        }

        return new(
            manifests,
            new RulePipelineWorkflowRunner(ProductionRuleComposer.Compose),
            preflight,
            new AtomicWorkflowFileTransaction(originalSubmissions),
            releaseSource,
            new InstallerWorkflowArtifactProcessor(downloader),
            clock,
            new FileOverridePackStore(overridePackStoreOptions ?? OverridePackStoreOptions.CreateDefault()));
    }

    public static GitHubLifecycleWorkflow CreateGitHubLifecycle(
        IGitHubRepositoryClient gitHub,
        InstallerDownloader downloader,
        IWorkflowClock? clock = null,
        RemoteOperationLockOptions? lockOptions = null)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentNullException.ThrowIfNull(downloader);
        var network = new DurableInstallerPreflightNetwork(downloader);
        var preflight = new PreflightGateWorkflowAdapter(
            new PreflightGate(network),
            network);
        return new(
            gitHub,
            preflight,
            new DownloaderFinalArtifactRevalidator(downloader),
            new FileRemoteOperationLockProvider(lockOptions, clock),
            branchNames: null,
            clock: clock,
            repositoryEvidence: new GitHubRepositorySubmissionEvidenceProvider(gitHub),
            pullRequestEvidence: new GitHubPullRequestManifestEvidenceProvider(gitHub));
    }

    public static GitHubMaintenanceWorkflow CreateGitHubMaintenance(
        IGitHubRepositoryClient gitHub,
        IWorkflowClock? clock = null)
        => new(gitHub, clock);

    public static ISubmissionJournalStore CreateSubmissionJournal(
        OverridePackStoreOptions? overridePackStoreOptions = null,
        IWorkflowClock? clock = null)
        => new FileSubmissionJournalStore(
            new SubmissionJournalOptions
            {
                OverrideStoreDirectory =
                    (overridePackStoreOptions ?? OverridePackStoreOptions.CreateDefault())
                    .RootDirectory,
            },
            clock);

    public static GitHubFeedbackWorkflow CreateGitHubFeedback(
        IGitHubRepositoryClient gitHub,
        GitHubLifecycleWorkflow lifecycle,
        IApprovedRepairPlanner repairs,
        IWorkflowClock? clock = null)
        => new(gitHub, lifecycle, repairs, clock);
}
