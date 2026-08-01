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
        IWorkflowClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        var originalSubmissions = new FileOriginalSubmissionStore();
        var preflight = new PreflightGateWorkflowAdapter(
            new PreflightGate(new InstallerDownloaderPreflightNetwork(downloader)));
        return new(
            new LocalManifestSnapshotSource(originalSubmissions),
            new RulePipelineWorkflowRunner(ProductionRuleComposer.Compose),
            preflight,
            new ProvenanceWorkflowFileTransaction(
                new AtomicWorkflowFileTransaction(),
                originalSubmissions),
            releaseSource,
            new InstallerWorkflowArtifactProcessor(downloader),
            clock);
    }

    public static GitHubLifecycleWorkflow CreateGitHubLifecycle(
        IGitHubRepositoryClient gitHub,
        InstallerDownloader downloader,
        IWorkflowClock? clock = null,
        RemoteOperationLockOptions? lockOptions = null)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentNullException.ThrowIfNull(downloader);
        var preflight = new PreflightGateWorkflowAdapter(
            new PreflightGate(new InstallerDownloaderPreflightNetwork(downloader)));
        return new(
            gitHub,
            preflight,
            new DownloaderFinalArtifactRevalidator(downloader),
            new FileRemoteOperationLockProvider(lockOptions, clock),
            clock: clock);
    }

    public static GitHubMaintenanceWorkflow CreateGitHubMaintenance(
        IGitHubRepositoryClient gitHub,
        IWorkflowClock? clock = null)
        => new(gitHub, clock);

    public static GitHubFeedbackWorkflow CreateGitHubFeedback(
        IGitHubRepositoryClient gitHub,
        GitHubLifecycleWorkflow lifecycle,
        IApprovedRepairPlanner repairs,
        IWorkflowClock? clock = null)
        => new(gitHub, lifecycle, repairs, clock);
}
