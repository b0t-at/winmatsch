using System.Collections.Immutable;
using System.Text.Json;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubLifecycleWorkflowTests
{
    [Fact]
    public async Task Dry_run_lists_all_remote_operations_without_mutation()
    {
        var client = new FakeGitHubClient();

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Equal(GitHubLifecycleResultCode.Planned, result.Code);
        Assert.Equal(
            [
                RemoteOperationKind.EnsureFork,
                RemoteOperationKind.CreateBranch,
                RemoteOperationKind.CreateCommit,
                RemoteOperationKind.CreatePullRequest,
            ],
            result.Plan.Operations.Select(static operation => operation.Kind));
        Assert.Empty(client.Mutations);
    }

    [Theory]
    [InlineData("other.txt", "GH1003")]
    [InlineData("manifests/e/example/App/2.0.0/Example.App.yaml", "GH1004")]
    [InlineData("manifests/e/Example/Other/2.0.0/Other.yaml", "GH1004")]
    public void Diff_guard_rejects_off_target_and_casing_drift(string path, string code)
    {
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            new WorkflowFileChange(PlannedChangeKind.Add, path, "x"u8, ExpectedFileState.Absent),
        ]);
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with { LocalPlan = local };

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);

        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Replace_allows_only_the_explicit_prior_version_deletion()
    {
        PackageIdentifier package = new("Example.App");
        string priorPath =
            $"{ManifestPaths.GetVersionDirectory(package, new PackageVersion("1.0.0"))}/Example.App.yaml";
        byte[] priorContent = "old"u8.ToArray();
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            new WorkflowFileChange(
                PlannedChangeKind.Add,
                $"{ManifestPaths.GetVersionDirectory(package, new PackageVersion("2.0.0"))}/Example.App.yaml",
                "x"u8,
                ExpectedFileState.Absent),
            new WorkflowFileChange(
                PlannedChangeKind.Delete,
                priorPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(priorContent)),
        ]) with
        {
            BeforeDocuments = [new RawManifestDocument(priorPath, priorContent)],
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = local,
            Operation = GitHubManifestOperation.Replace,
            Policy = new()
            {
                ReplacePreviousVersion = true,
                PreviousVersion = new PackageVersion("1.0.0"),
            },
        };

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);

        Assert.True(plan.CanApply);
    }

    [Fact]
    public void Diff_guard_rejects_content_not_bound_to_validated_documents()
    {
        LocalOperationPlan original = GitHubLifecycleTestSupport.Plan();
        WorkflowFileChange change = original.FileChanges[0];
        LocalOperationPlan tampered = original with
        {
            FileChanges =
            [
                new(
                    change.Kind,
                    change.RepositoryPath,
                    "different bytes"u8,
                    change.ExpectedState,
                    change.ExpectedSha256),
            ],
        };

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request() with { LocalPlan = tampered });

        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GH1012");
    }

    [Fact]
    public async Task Duplicate_pull_request_from_any_author_prevents_branch_mutation()
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7, author: "other-author"));

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Pull_request_is_rechecked_after_commit_to_close_self_race()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 2)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(8, author: "racer"));
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.True(result.RemoteState.CommitCreated);
        Assert.False(result.RemoteState.PullRequestCreated);
        Assert.Equal(["branch", "commit"], client.Mutations);
    }

    [Fact]
    public async Task Apply_creates_fresh_branch_commit_and_pull_request()
    {
        var client = new FakeGitHubClient();
        var artifacts = new FakeArtifactRevalidator();
        var preflight = new FakePreflight();

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                preflight,
                artifacts)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(["branch", "commit", "pull-request"], client.Mutations);
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.Equal(1, preflight.BoundaryCalls);
        Assert.Equal(1, artifacts.Calls);
    }

    [Fact]
    public async Task Missing_fork_requires_explicit_consent()
    {
        var client = new FakeGitHubClient(includeFork: false);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.ConsentRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Explicit_consent_provisions_fork_before_branch()
    {
        var client = new FakeGitHubClient(includeFork: false);
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(
            policy: new GitHubSubmissionPolicy { ForkConsent = ForkConsentPolicy.AllowCreate });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(["fork", "branch", "commit", "pull-request"], client.Mutations);
        Assert.True(result.RemoteState.ForkCreated);
    }

    [Fact]
    public async Task Upstream_movement_after_branch_creation_prevents_commit()
    {
        var client = new FakeGitHubClient
        {
            MoveUpstreamBeforeCommitTo = "cccccccccccccccccccccccccccccccccccccccc",
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.True(result.RemoteState.BranchCreated);
        Assert.False(result.RemoteState.CommitCreated);
        Assert.Equal(["branch"], client.Mutations);
    }

    [Fact]
    public async Task Commit_conflict_returns_recoverable_branch_state_not_success()
    {
        var client = new FakeGitHubClient { FailMutation = "commit" };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.True(result.RemoteState.BranchCreated);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Failed_final_artifact_revalidation_never_commits()
    {
        var client = new FakeGitHubClient();
        var artifacts = new FakeArtifactRevalidator
        {
            Result = new(false, [new("GH_TEST_HASH", "Hash changed.")]),
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                artifacts: artifacts)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.ValidationFailed, result.Code);
        Assert.Equal(["branch"], client.Mutations);
    }

    [Theory]
    [InlineData("branch", false, false)]
    [InlineData("commit", true, false)]
    [InlineData("pull-request", true, true)]
    public async Task Cancellation_or_failure_at_each_mutation_boundary_exposes_partial_state(
        string boundary,
        bool branchCreated,
        bool commitCreated)
    {
        var client = new FakeGitHubClient { FailMutation = boundary };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(
            boundary == "commit"
                ? GitHubLifecycleResultCode.Conflict
                : GitHubLifecycleResultCode.RemoteFailure,
            result.Code);
        Assert.Equal(branchCreated, result.RemoteState.BranchCreated);
        Assert.Equal(commitCreated, result.RemoteState.CommitCreated);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.False(result.Applied);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("commit")]
    [InlineData("pull-request")]
    public async Task Cancellation_at_each_mutation_boundary_is_not_reported_as_success(string boundary)
    {
        var client = new FakeGitHubClient { CancelMutation = boundary };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Cancelled, result.Code);
        Assert.Equal(
            Enum.Parse<RemoteOperationKind>(
                boundary switch
                {
                    "branch" => nameof(RemoteOperationKind.CreateBranch),
                    "commit" => nameof(RemoteOperationKind.CreateCommit),
                    _ => nameof(RemoteOperationKind.CreatePullRequest),
                }),
            result.RemoteState.LastAttemptedOperation);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.False(result.Applied);
    }

    [Fact]
    public void Duplicate_hash_evidence_requires_annotated_override()
    {
        string hash = new string('A', 64);
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan() with
        {
            Preflight = GitHubLifecycleTestSupport.Plan().Preflight with
            {
                InstallerArtifacts =
                [
                    new("https://example.invalid/app.exe", new WinMatsch.Downloads.DownloadResult
                    {
                        FilePath = "app.exe",
                        FileName = "app.exe",
                        Sha256 = new Sha256Hash(hash),
                        SizeInBytes = 1,
                        RetrievedAt = DateTimeOffset.UtcNow,
                        InitialUrl = "https://example.invalid/app.exe",
                        FinalUrl = "https://example.invalid/app.exe",
                    }),
                ],
            },
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = local,
            RepositoryEvidence =
            [
                new(
                    new PackageIdentifier("Retired.App"),
                    new PackageVersion("1.0.0"),
                    hash,
                    "manifests/r/Retired/App/1.0.0/Retired.App.installer.yaml",
                    true),
            ],
        };

        Assert.Contains(
            GitHubLifecycleWorkflow.Plan(request).Diagnostics,
            diagnostic => diagnostic.Code == "GH1011");

        GitHubSubmissionPlan overridden = GitHubLifecycleWorkflow.Plan(request with
        {
            Policy = new()
            {
                DuplicateHashes = new()
                {
                    AllowedSha256 = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, hash),
                    OverrideAnnotation = "Repository steward approved shared vendor payload.",
                },
            },
        });
        Assert.DoesNotContain(overridden.Diagnostics, diagnostic => diagnostic.Code == "GH1011");
    }

    [Fact]
    public void Title_body_include_evidence_and_redact_secrets()
    {
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan) with
        {
            Resolves = "token=secret-value",
            CustomTitle = "Update Example.App password=hunter2",
        };

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);

        Assert.Equal(
            "Update: Example.App version 2.0.0 - Update Example.App password=[REDACTED]",
            plan.PullRequestTitle);
        Assert.Contains("Created with: winmatsch tests", plan.PullRequestBody, StringComparison.Ordinal);
        Assert.Contains("Rules", plan.PullRequestBody, StringComparison.Ordinal);
        Assert.Contains("Validation", plan.PullRequestBody, StringComparison.Ordinal);
        Assert.Contains("Resolves: token=[REDACTED]", plan.PullRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", plan.PullRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_result_output_uses_source_generated_serialization_without_secrets()
    {
        var client = new FakeGitHubClient();
        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        string json = JsonSerializer.Serialize(
            GitHubLifecycleOutput.FromResult(result),
            GitHubWorkflowJsonContext.Default.GitHubLifecycleOutput);

        Assert.Contains("\"code\": \"Planned\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }
}
