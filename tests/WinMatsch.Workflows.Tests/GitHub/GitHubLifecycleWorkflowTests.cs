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
    public async Task Pull_request_with_wrong_head_is_never_reported_as_success()
    {
        var client = new FakeGitHubClient
        {
            PullRequestHeadSha = "cccccccccccccccccccccccccccccccccccccccc",
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.False(result.Applied);
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
        Assert.Equal(2, artifacts.Calls);
    }

    [Fact]
    public async Task Repository_side_branch_reservation_blocks_cross_process_duplicate()
    {
        var client = new FakeGitHubClient();
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            "winmatsch/update/example-app/2.0.0/test",
            GitHubLifecycleTestSupport.UpstreamSha);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public void Production_branch_identity_is_stable_for_the_same_package_version()
    {
        var generator = new DefaultGitHubBranchNameGenerator();
        var context = new GitHubBranchNameContext(
            new PackageIdentifier("Example.App"),
            new PackageVersion("2.0+build"),
            GitHubManifestOperation.Update,
            null);

        string first = generator.Create(context);
        string second = generator.Create(context);

        Assert.Equal(first, second);
        Assert.Equal("winmatsch/submissions/example-app/2-0-build", first);
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
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Warning_policy_blocks_fake_preflight_boundary_like_production()
    {
        var client = new FakeGitHubClient();
        var preflight = new FakePreflight
        {
            Report = new ValidationReport(
            [
                new("GH_WARNING", ValidationSeverity.Warning, "Synthetic warning."),
            ]),
        };
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan() with
        {
            Preflight = GitHubLifecycleTestSupport.Plan().Preflight with
            {
                Options = new PreflightOptions
                {
                    NetworkMode = NetworkValidationMode.Skip,
                    WarningPolicy = WarningPolicy.TreatAsErrors,
                },
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client, preflight)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.ValidationFailed, result.Code);
        Assert.Equal(0, preflight.BoundaryCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Remote_existing_path_precondition_is_checked_at_pinned_upstream_sha()
    {
        var client = new FakeGitHubClient();
        LocalOperationPlan original = GitHubLifecycleTestSupport.Plan();
        WorkflowFileChange add = original.FileChanges[0];
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            add.RepositoryPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            "newer upstream content"u8);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
        Assert.False(result.RemoteState.CommitCreated);
    }

    [Fact]
    public async Task Remote_update_hash_must_match_local_before_document()
    {
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] expected = "expected"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "updated"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(expected));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, expected)],
        };
        var client = new FakeGitHubClient();
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            "changed remotely"u8);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
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
        Assert.Equal(boundary != "commit", result.RemoteState.RemoteOutcomeUncertain);
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
    public void Signed_url_query_values_are_redacted_from_public_text()
    {
        string redacted = GitHubSubmissionFormatter.Redact(
            "https://example.invalid/file?sv=1&sig=TOPSECRET&x-amz-signature=AWSSECRET");

        Assert.DoesNotContain("TOPSECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AWSSECRET", redacted, StringComparison.Ordinal);
        Assert.Contains("sig=[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_freshness_delay_requires_evidence_and_blocks_early_submission()
    {
        GitHubSubmissionRequest missing = GitHubLifecycleTestSupport.Request() with
        {
            Policy = new() { MinimumReleaseFreshness = TimeSpan.FromHours(1) },
        };
        GitHubSubmissionRequest early = missing with
        {
            ReleaseUpdatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Contains(
            GitHubLifecycleWorkflow.Plan(missing).Diagnostics,
            diagnostic => diagnostic.Code == "GH1014");
        Assert.Contains(
            GitHubLifecycleWorkflow.Plan(early).Diagnostics,
            diagnostic => diagnostic.Code == "GH1015");
    }

    [Fact]
    public async Task Live_release_update_overrides_stale_planning_evidence()
    {
        var clock = new FakeClock();
        var client = new FakeGitHubClient
        {
            Releases =
            [
                new(
                    100,
                    "v2.0.0",
                    "2.0.0",
                    null,
                    new Uri("https://github.invalid/releases/100"),
                    false,
                    false,
                    clock.UtcNow.AddDays(-1),
                    [],
                    clock.UtcNow),
            ],
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            Policy = new() { MinimumReleaseFreshness = TimeSpan.FromHours(1) },
            ReleaseUpdatedAt = clock.UtcNow.AddHours(-2),
            ReleaseRepository = new RepositoryCoordinates("vendor", "app"),
            ReleaseId = 100,
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.ValidationFailed, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH1018");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Upstream_move_after_commit_prevents_pull_request_creation()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 2)
                {
                    fake.MoveUpstream("cccccccccccccccccccccccccccccccccccccccc");
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Equal(["branch", "commit"], client.Mutations);
        Assert.False(result.RemoteState.PullRequestCreated);
    }

    [Fact]
    public async Task Final_branch_read_detects_push_after_post_creation_duplicate_check()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.AddBranch(
                        GitHubLifecycleTestSupport.Fork,
                        "winmatsch/update/example-app/2.0.0/test",
                        "cccccccccccccccccccccccccccccccccccccccc");
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.True(result.RemoteState.PullRequestCreated);
    }

    [Fact]
    public async Task Final_pull_request_read_rejects_concurrent_close()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.UpdatePullRequest(
                        42,
                        static pullRequest => pullRequest with { State = PullRequestState.Closed });
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Duplicate_winner_is_refreshed_before_loser_self_close()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
                        1,
                        author: "other-fork",
                        branch: "winmatsch/submissions/example-app/2-0-0"));
                }
            },
            OnGetPullRequest = static (fake, number) =>
            {
                if (number == 1)
                {
                    fake.UpdatePullRequest(
                        number,
                        static pullRequest => pullRequest with { State = PullRequestState.Closed });
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.False(result.RemoteState.PullRequestClosed);
        Assert.DoesNotContain("close", client.Mutations);
    }

    [Fact]
    public async Task Cross_fork_duplicate_loser_closes_only_its_own_new_pr()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
                        1,
                        author: "other-fork",
                        branch: "winmatsch/submissions/example-app/2-0-0"));
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.True(result.RemoteState.PullRequestClosed);
        Assert.Equal(
            ["branch", "commit", "pull-request", "comment", "close"],
            client.Mutations);
        Assert.Equal(PullRequestState.Closed, client.PullRequests.Single(pr => pr.Number == 42).State);
        Assert.Equal(PullRequestState.Open, client.PullRequests.Single(pr => pr.Number == 1).State);
    }

    [Fact]
    public async Task Cross_fork_duplicate_close_failure_keeps_confirmed_comment_state()
    {
        var client = new FakeGitHubClient
        {
            FailMutation = "close",
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
                        1,
                        author: "other-fork",
                        branch: "winmatsch/submissions/example-app/2-0-0"));
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.True(result.RemoteState.CommentCreated);
        Assert.Equal(
            RemoteOperationKind.ClosePullRequest,
            result.RemoteState.LastAttemptedOperation);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
    }

    [Fact]
    public async Task Live_release_is_rechecked_as_last_success_gate()
    {
        var clock = new FakeClock();
        GitHubRelease safeRelease = new(
            200,
            "v2.0.0",
            "2.0.0",
            null,
            new Uri("https://github.invalid/releases/200"),
            false,
            false,
            clock.UtcNow.AddDays(-2),
            [],
            clock.UtcNow.AddHours(-2));
        var client = new FakeGitHubClient
        {
            Releases = [safeRelease],
            OnGetReleases = (fake, call) =>
            {
                if (call == 4)
                {
                    fake.Releases = [safeRelease with { UpdatedAt = clock.UtcNow }];
                }
            },
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            Policy = new() { MinimumReleaseFreshness = TimeSpan.FromHours(1) },
            ReleaseUpdatedAt = clock.UtcNow.AddHours(-2),
            ReleaseRepository = new RepositoryCoordinates("vendor", "app"),
            ReleaseId = 200,
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.ValidationFailed, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.True(result.RemoteState.PullRequestClosed);
        Assert.Equal(4, client.GetReleasesCalls);
        Assert.Equal(
            ["branch", "commit", "pull-request", "comment", "close"],
            client.Mutations);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task Final_freshness_cleanup_failure_preserves_uncertain_close_state()
    {
        var clock = new FakeClock();
        GitHubRelease safeRelease = new(
            201,
            "v2.0.0",
            "2.0.0",
            null,
            new Uri("https://github.invalid/releases/201"),
            false,
            false,
            clock.UtcNow.AddDays(-2),
            [],
            clock.UtcNow.AddHours(-2));
        var client = new FakeGitHubClient
        {
            Releases = [safeRelease],
            FailMutation = "close",
            OnGetReleases = (fake, call) =>
            {
                if (call == 4)
                {
                    fake.Releases = [safeRelease with { UpdatedAt = clock.UtcNow }];
                }
            },
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            Policy = new() { MinimumReleaseFreshness = TimeSpan.FromHours(1) },
            ReleaseUpdatedAt = clock.UtcNow.AddHours(-2),
            ReleaseRepository = new RepositoryCoordinates("vendor", "app"),
            ReleaseId = 201,
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.True(result.RemoteState.CommentCreated);
        Assert.Equal(
            RemoteOperationKind.ClosePullRequest,
            result.RemoteState.LastAttemptedOperation);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2030");
    }

    [Fact]
    public async Task Uncertain_branch_creation_reports_and_reconciles_deterministic_identity()
    {
        var client = new FakeGitHubClient
        {
            FailMutation = "branch",
            BranchCreatedBeforeFailure = true,
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.Equal("winmatsch/update/example-app/2.0.0/test", result.RemoteState.BranchName);
        Assert.True(result.RemoteState.BranchCreated);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
    }

    [Fact]
    public async Task Public_result_output_uses_source_generated_serialization_without_secrets()
    {
        var client = new FakeGitHubClient();
        var artifacts = new FakeArtifactRevalidator
        {
            Result = new(
                false,
                [new("GH_SECRET", "Failed https://example.invalid/file?sig=TOPSECRET")]),
        };
        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                artifacts: artifacts)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        string json = JsonSerializer.Serialize(
            GitHubLifecycleOutput.FromResult(result),
            GitHubWorkflowJsonContext.Default.GitHubLifecycleOutput);

        Assert.Contains("\"code\": \"ValidationFailed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", json, StringComparison.Ordinal);
        Assert.Contains("sig=[REDACTED]", json, StringComparison.Ordinal);
    }
}
