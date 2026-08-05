using System.Collections.Immutable;
using System.Net;
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
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = local,
            Operation = GitHubManifestOperation.Replace,
            Policy = new()
            {
                ReplacePreviousVersion = true,
                PreviousVersion = new PackageVersion("1.0.0"),
                MinimumReleaseFreshness = TimeSpan.Zero,
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
    public void Diff_guard_rejects_preflight_documents_that_differ_from_the_commit_plan()
    {
        LocalOperationPlan original = GitHubLifecycleTestSupport.Plan();
        WorkflowFileChange change = original.FileChanges[0];
        LocalOperationPlan tampered = original with
        {
            Preflight = original.Preflight with
            {
                AfterDocuments =
                [
                    new RawManifestDocument(change.RepositoryPath, "different bytes"u8),
                ],
            },
        };

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request() with { LocalPlan = tampered });

        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GH1020");
        Assert.False(plan.CanApply);
    }

    [Fact]
    public async Task Malformed_repository_evidence_requires_human_escalation_before_mutation()
    {
        var client = new FakeGitHubClient();
        var evidence = new FakeRepositorySubmissionEvidenceProvider
        {
            Failure = new RepositorySubmissionEvidenceException(
                "Pinned repository submission evidence is malformed or exceeds a safety limit.",
                new InvalidDataException()),
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                repositoryEvidence: evidence)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2040");
        Assert.Empty(client.Mutations);
    }

    [Theory]
    [InlineData("komac-bot", "Automated submission from Komac.")]
    [InlineData("yamlcreate-bot", "Generated by YamlCreate.")]
    [InlineData("human-maintainer", "Hand-authored update with no tool markers.")]
    public async Task Canonical_duplicate_from_any_author_and_body_prevents_branch_mutation(
        string author,
        string body)
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7, author: author) with
        {
            Body = body,
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                pullRequestEvidence: new FakePullRequestManifestEvidenceProvider(
                    new(true, true)))
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Canonical_title_from_different_author_proves_duplicate_without_file_match()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: "different-author"));

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Equal(0, client.PullRequestHeadContentCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Manifest_path_evidence_detects_noncanonical_human_duplicate()
    {
        var client = new FakeGitHubClient();
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        client.SetPullRequestChangedFiles(7, change.RepositoryPath);
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: "human-maintainer") with
        {
            Title = "Please update Example.App",
            Body = "Hand-authored update.",
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Equal(0, client.PullRequestHeadContentCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Matching_manifest_content_detects_noncanonical_duplicate_without_body_marker()
    {
        var client = new FakeGitHubClient();
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            change.RepositoryPath,
            GitHubLifecycleTestSupport.CommitSha,
            change.Content.AsSpan());
        client.SetPullRequestChangedFiles(7, "unrelated/readme.txt");
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Package refresh",
            Body = "No generated metadata.",
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
        Assert.Equal(1, client.PullRequestHeadContentCalls);
    }

    [Fact]
    public async Task Deleted_fork_evidence_fails_closed_before_remote_mutation()
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
            HeadRepository = null,
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Private_fork_evidence_fails_closed_before_remote_mutation()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.SetRepositoryPrivate(candidate.HeadRepository!, isPrivate: true);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Unavailable_changed_file_evidence_fails_closed_before_remote_mutation()
    {
        var client = new FakeGitHubClient
        {
            PullRequestChangedFilesUnsupported = true,
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Graphql_rate_limit_evidence_failure_preserves_classification_without_mutation()
    {
        var rateLimit = new RateLimitInfo(
            "graphql",
            5000,
            0,
            5000,
            new DateTimeOffset(2026, 12, 1, 1, 0, 0, TimeSpan.Zero));
        var client = new FakeGitHubClient
        {
            PullRequestChangedFilesFailure = new GitHubApiException(
                "API rate limit exceeded",
                statusCode: null,
                requestId: null,
                errorKind: GitHubApiErrorKind.RateLimited,
                rateLimit: rateLimit,
                retryAfter: TimeSpan.FromMinutes(1)),
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        });
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => provider.GetCandidatesAsync(
                plan,
                client.PullRequests,
                CancellationToken.None));
        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubApiErrorKind.RateLimited, exception.ErrorKind);
        Assert.Same(rateLimit, exception.RateLimit);
        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2013");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Renamed_fork_evidence_fails_closed_when_head_coordinates_change()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.OnGetPullRequest = static (fake, number) =>
        {
            fake.UpdatePullRequest(number, pullRequest => pullRequest with
            {
                HeadRepository = new RepositoryCoordinates("renamed-owner", "renamed-repository"),
            });
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Head_movement_evidence_fails_closed_when_pinned_sha_changes()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.OnGetPullRequest = static (fake, number) =>
        {
            fake.UpdatePullRequest(number, pullRequest => pullRequest with
            {
                HeadSha = "dddddddddddddddddddddddddddddddddddddddd",
            });
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Successful_evidence_is_cached_by_pull_request_head_identity()
    {
        var client = new FakeGitHubClient();
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.SetPullRequestChangedFiles(candidate.Number, change.RepositoryPath);
        client.SetContent(
            candidate.HeadRepository!,
            change.RepositoryPath,
            candidate.HeadSha,
            change.Content.AsSpan());
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        _ = await provider.GetEvidenceAsync(plan, candidate, CancellationToken.None);
        _ = await provider.GetEvidenceAsync(plan, candidate, CancellationToken.None);

        PullRequestInfo moved = candidate with
        {
            HeadSha = "dddddddddddddddddddddddddddddddddddddddd",
        };
        client.UpdatePullRequest(candidate.Number, _ => moved);
        client.SetContent(
            moved.HeadRepository!,
            change.RepositoryPath,
            moved.HeadSha,
            change.Content.AsSpan());
        _ = await provider.GetEvidenceAsync(plan, moved, CancellationToken.None);

        Assert.Equal(2, client.PullRequestFileBatchCalls);
        Assert.Equal([1, 1], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Production_scale_candidate_cache_refetches_only_changed_head_identity()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int number = 1; number <= 400; number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo[] firstSnapshot = [.. client.PullRequests];
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            firstSnapshot,
            CancellationToken.None));
        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            firstSnapshot,
            CancellationToken.None));
        PullRequestInfo[] movedSnapshot =
        [
            .. firstSnapshot.Select(pullRequest =>
                pullRequest.Number == 200
                    ? pullRequest with
                    {
                        HeadSha = "dddddddddddddddddddddddddddddddddddddddd",
                    }
                    : pullRequest),
        ];
        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            movedSnapshot,
            CancellationToken.None));

        Assert.Equal([400, 1], client.PullRequestFileBatchSizes);
        Assert.Equal(2, client.PullRequestFileBatchCalls);
        Assert.Equal(0, client.PullRequestFilesCalls);
    }

    [Fact]
    public async Task Cross_page_duplicate_pull_request_observations_are_normalized()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        client.AddPullRequest(client.PullRequests[6]);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            client.PullRequests,
            CancellationToken.None));
        Assert.Equal([65], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Closed_null_node_is_rescreened_after_reopen_at_same_identity()
    {
        const int rescreenPullRequestNumber = 10_007;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.PullRequestRescreenNumbers.Add(rescreenPullRequestNumber);
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo[] snapshot = [.. client.PullRequests];
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            snapshot,
            CancellationToken.None));
        client.PullRequestRescreenNumbers.Clear();
        client.SetPullRequestChangedFiles(
            rescreenPullRequestNumber,
            plan.Request.LocalPlan.FileChanges[0].RepositoryPath);

        PullRequestInfo candidate = Assert.Single(await provider.GetCandidatesAsync(
            plan,
            snapshot,
            CancellationToken.None));

        Assert.Equal(rescreenPullRequestNumber, candidate.Number);
        Assert.Equal([65, 1], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Closed_nonnull_snapshot_is_rescreened_after_reopen_at_same_identity()
    {
        const int reopenedPullRequestNumber = 10_007;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.PullRequestScreeningStateOverrides[reopenedPullRequestNumber] =
            PullRequestState.Closed;
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo[] snapshot = [.. client.PullRequests];
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            snapshot,
            CancellationToken.None));
        client.PullRequestScreeningStateOverrides.Clear();
        client.SetPullRequestChangedFiles(
            reopenedPullRequestNumber,
            plan.Request.LocalPlan.FileChanges[0].RepositoryPath);

        PullRequestInfo candidate = Assert.Single(await provider.GetCandidatesAsync(
            plan,
            snapshot,
            CancellationToken.None));

        Assert.Equal(reopenedPullRequestNumber, candidate.Number);
        Assert.Equal([65, 1], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Reverted_head_identity_is_rescreened_after_intermediate_force_push()
    {
        const int revertedPullRequestNumber = 10_007;
        const string intermediateHead = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.PullRequestScreeningHeadOverrides[revertedPullRequestNumber] =
            intermediateHead;
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo[] originalSnapshot = [.. client.PullRequests];
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            originalSnapshot,
            CancellationToken.None));
        client.PullRequestScreeningHeadOverrides.Clear();
        client.SetPullRequestChangedFiles(
            revertedPullRequestNumber,
            plan.Request.LocalPlan.FileChanges[0].RepositoryPath);

        PullRequestInfo candidate = Assert.Single(await provider.GetCandidatesAsync(
            plan,
            originalSnapshot,
            CancellationToken.None));

        Assert.Equal(revertedPullRequestNumber, candidate.Number);
        Assert.Equal([65, 1], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Canonical_title_observed_during_screening_is_promoted_to_candidate()
    {
        const int promotedPullRequestNumber = 10_007;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.PullRequestScreeningTitleOverrides[promotedPullRequestNumber] =
            "Update version: Example.App version 2.0.0";
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        PullRequestInfo candidate = Assert.Single(await provider.GetCandidatesAsync(
            plan,
            client.PullRequests,
            CancellationToken.None));

        Assert.Equal(promotedPullRequestNumber, candidate.Number);
    }

    [Fact]
    public async Task Retargeted_snapshot_is_not_promoted_or_reused_as_candidate()
    {
        const int retargetedPullRequestNumber = 10_007;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.PullRequestScreeningTitleOverrides[retargetedPullRequestNumber] =
            "Update version: Example.App version 2.0.0";
        client.PullRequestScreeningBaseOverrides[retargetedPullRequestNumber] =
            "release";
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        client.SetPullRequestChangedFiles(
            retargetedPullRequestNumber,
            GitHubLifecycleTestSupport.Plan().FileChanges[0].RepositoryPath);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            client.PullRequests,
            CancellationToken.None));
    }

    [Fact]
    public async Task Canonical_only_evidence_is_recomputed_after_noncanonical_retitle()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7);
        client.AddPullRequest(candidate);
        client.SetPullRequestChangedFiles(
            candidate.Number,
            GitHubLifecycleTestSupport.Plan().FileChanges[0].RepositoryPath);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        PullRequestManifestEvidence canonical = await provider.GetEvidenceAsync(
            plan,
            candidate,
            CancellationToken.None);
        client.UpdatePullRequest(
            candidate.Number,
            pullRequest => pullRequest with { Title = "Hand-authored manifest update" });
        PullRequestManifestEvidence retitled = await provider.GetEvidenceAsync(
            plan,
            candidate with { Title = "Hand-authored manifest update" },
            CancellationToken.None);

        Assert.True(canonical.HasCanonicalTitle);
        Assert.False(retitled.HasCanonicalTitle);
        Assert.True(retitled.HasManifestPath);
        Assert.True(retitled.IsAssociated);
    }

    [Fact]
    public async Task Retained_candidate_overflow_fails_with_evidence_limit()
    {
        const int cachedPathPullRequestNumber = 20_000;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            cachedPathPullRequestNumber) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        });
        client.SetPullRequestChangedFiles(
            cachedPathPullRequestNumber,
            GitHubLifecycleTestSupport.Plan().FileChanges[0].RepositoryPath);
        for (int index = 1; index <= 64; index++)
        {
            int number = 20_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));
        _ = await provider.GetCandidatesAsync(
            plan,
            client.PullRequests,
            CancellationToken.None);
        foreach (PullRequestInfo pullRequest in client.PullRequests.Where(
                     pullRequest => pullRequest.Number != cachedPathPullRequestNumber).ToArray())
        {
            client.UpdatePullRequest(
                pullRequest.Number,
                current => current with
                {
                    Title = "Update version: Example.App version 2.0.0",
                });
        }

        await Assert.ThrowsAsync<PullRequestEvidenceLimitException>(
            () => provider.GetCandidatesAsync(
                plan,
                client.PullRequests,
                CancellationToken.None));
    }

    [Fact]
    public async Task Cached_evidence_revalidates_the_live_pull_request_head()
    {
        var client = new FakeGitHubClient();
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.SetPullRequestChangedFiles(candidate.Number, change.RepositoryPath);
        client.SetContent(
            candidate.HeadRepository!,
            change.RepositoryPath,
            candidate.HeadSha,
            change.Content.AsSpan());
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        _ = await provider.GetEvidenceAsync(plan, candidate, CancellationToken.None);
        client.UpdatePullRequest(candidate.Number, pullRequest => pullRequest with
        {
            HeadSha = "dddddddddddddddddddddddddddddddddddddddd",
        });

        await Assert.ThrowsAsync<PullRequestEvidenceLimitException>(
            () => provider.GetEvidenceAsync(plan, candidate, CancellationToken.None));
        Assert.Equal(1, client.PullRequestFileBatchCalls);
    }

    [Fact]
    public async Task Different_base_branch_does_not_block_duplicate_detection()
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            BaseBranch = "release",
            Title = "Update version: Example.App version 2.0.0",
            Body = "Hand-authored update.",
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
    }

    [Fact]
    public async Task Large_replace_screening_ignores_prior_version_only_pull_requests()
    {
        PackageIdentifier package = new("Example.App");
        string priorPath =
            $"{ManifestPaths.GetVersionDirectory(package, new PackageVersion("1.0.0"))}/Example.App.yaml";
        byte[] priorContent = "old"u8.ToArray();
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            GitHubLifecycleTestSupport.Plan().FileChanges[0],
            new WorkflowFileChange(
                PlannedChangeKind.Delete,
                priorPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(priorContent)),
        ]);
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int index = 1; index <= 65; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Prior version maintenance {number}",
                Body = null,
            });
            client.SetPullRequestChangedFiles(number, priorPath);
        }

        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(
            WorkflowExecutionMode.Plan) with
        {
            LocalPlan = local,
            Operation = GitHubManifestOperation.Replace,
            Policy = new()
            {
                ReplacePreviousVersion = true,
                PreviousVersion = new PackageVersion("1.0.0"),
                MinimumReleaseFreshness = TimeSpan.Zero,
            },
        };
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);

        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            client.PullRequests,
            CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_evidence_is_not_cached()
    {
        var client = new FakeGitHubClient();
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        PullRequestInfo candidate = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        };
        client.AddPullRequest(candidate);
        client.SetPullRequestChangedFiles(candidate.Number, change.RepositoryPath);
        client.SetContent(
            candidate.HeadRepository!,
            change.RepositoryPath,
            candidate.HeadSha,
            change.Content.AsSpan());
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetEvidenceAsync(plan, candidate, cancellation.Token));
        PullRequestManifestEvidence evidence = await provider.GetEvidenceAsync(
            plan,
            candidate,
            CancellationToken.None);

        Assert.True(evidence.IsAssociated);
        Assert.Equal(1, client.PullRequestFileBatchCalls);
    }

    [Fact]
    public void Planning_release_freshness_uses_the_injected_time_provider()
    {
        DateTimeOffset releaseUpdatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan) with
        {
            ReleaseUpdatedAt = releaseUpdatedAt,
            ReleaseRepository = GitHubLifecycleTestSupport.Upstream,
            ReleaseId = 42,
            Policy = new()
            {
                MinimumReleaseFreshness = TimeSpan.FromHours(1),
            },
        };

        GitHubSubmissionPlan early = GitHubLifecycleWorkflow.Plan(
            request,
            new FixedTimeProvider(releaseUpdatedAt.AddMinutes(59)));
        GitHubSubmissionPlan ready = GitHubLifecycleWorkflow.Plan(
            request,
            new FixedTimeProvider(releaseUpdatedAt.AddHours(1)));

        Assert.Contains(early.Diagnostics, diagnostic => diagnostic.Code == "GH1015");
        Assert.DoesNotContain(ready.Diagnostics, diagnostic => diagnostic.Code == "GH1015");
    }

    [Fact]
    public async Task Similar_package_title_without_manifest_evidence_is_not_a_duplicate()
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Update version: Example.Application version 2.0.0",
            Body = "Different package.",
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(1, client.PullRequestHeadContentCalls);
    }

    [Fact]
    public async Task Replace_previous_version_deletion_is_not_duplicate_evidence_for_new_version()
    {
        PackageIdentifier package = new("Example.App");
        string priorPath =
            $"{ManifestPaths.GetVersionDirectory(package, new PackageVersion("1.0.0"))}/Example.App.yaml";
        byte[] priorContent = "old"u8.ToArray();
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            GitHubLifecycleTestSupport.Plan().FileChanges[0],
            new WorkflowFileChange(
                PlannedChangeKind.Delete,
                priorPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(priorContent)),
        ]) with
        {
            BeforeDocuments = [new RawManifestDocument(priorPath, priorContent)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Example.App maintenance",
            Body = null,
        });
        client.SetPullRequestChangedFiles(7, priorPath);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            priorPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            priorContent);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with
            {
                LocalPlan = local,
                Operation = GitHubManifestOperation.Replace,
                Policy = new()
                {
                    ReplacePreviousVersion = true,
                    PreviousVersion = new PackageVersion("1.0.0"),
                    MinimumReleaseFreshness = TimeSpan.Zero,
                },
            });

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
    }

    [Fact]
    public async Task Changed_files_provider_can_supply_existing_target_path_evidence()
    {
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] expected = "expected upstream"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "our update"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(expected));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, expected)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Human fix",
            Body = null,
        });
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            path,
            GitHubLifecycleTestSupport.CommitSha,
            "their update"u8);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                pullRequestEvidence: new FakePullRequestManifestEvidenceProvider(
                    new(true, false)))
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Pinned_head_content_detects_target_change_despite_empty_changed_file_snapshot()
    {
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] expected = "expected upstream"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "our update"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(expected));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, expected)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Example.App stale branch",
            Body = null,
        });
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            path,
            GitHubLifecycleTestSupport.CommitSha,
            "stale branch content"u8);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            expected);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Base_only_target_change_does_not_make_stale_pr_a_duplicate()
    {
        const string mergeBaseSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] previous = "previous upstream"u8.ToArray();
        byte[] current = "current upstream"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "our update"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(current));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, current)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient
        {
            PullRequestMergeBaseSha = mergeBaseSha,
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Unrelated stale maintenance",
            Body = null,
        });
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            mergeBaseSha,
            previous);
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            path,
            GitHubLifecycleTestSupport.CommitSha,
            previous);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            current);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
    }

    [Fact]
    public async Task Renamed_target_path_is_detected_at_pinned_head_identity()
    {
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] expected = "expected upstream"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "our update"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(expected));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, expected)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Rename old manifest",
            Body = null,
        });
        client.SetPullRequestChangedFiles(
            7,
            new PullRequestChangedFile(
                "manifests/e/Example/App/2.0.0/Renamed.yaml",
                path));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            expected);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Transient_pr_evidence_failure_is_evicted_and_retried()
    {
        var client = new FakeGitHubClient
        {
            FailNextPullRequestContentCalls = 1,
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Example.App transient maintenance",
            Body = null,
        });
        client.SetPullRequestChangedFiles(
            7,
            "unrelated/readme.txt");
        client.SetContent(
            new RepositoryCoordinates("someone", GitHubLifecycleTestSupport.Upstream.Name),
            GitHubLifecycleTestSupport.Plan().FileChanges[0].RepositoryPath,
            GitHubLifecycleTestSupport.CommitSha,
            "different manifest content"u8);
        GitHubLifecycleWorkflow workflow = GitHubLifecycleTestSupport.Workflow(client);

        GitHubLifecycleResult failed = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request());
        GitHubLifecycleResult retried = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request() with { IdempotencyKey = "operation-retry" });

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, failed.Code);
        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, retried.Code);
        Assert.True(client.PullRequestHeadContentCalls >= 2);
    }

    [Fact]
    public async Task Unrelated_open_pull_requests_do_not_exhaust_manifest_content_evidence_budget()
    {
        var client = new FakeGitHubClient();
        for (int index = 0; index <= PullRequestManifestEvidenceLimits.MaximumCandidates; index++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(100 + index) with
            {
                Title = $"Example.App maintenance {index}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(client.PullRequestHeadContentCalls <= 1);
        Assert.Equal(0, client.PullRequestFilesCalls);
        Assert.Equal(
            [PullRequestManifestEvidenceLimits.MaximumCandidates + 1],
            client.PullRequestFileBatchSizes);
        Assert.Equal(1, client.PullRequestFileBatchCalls);
    }

    [Fact]
    public async Task More_than_sixty_four_relevant_candidates_fail_closed_before_mutation()
    {
        var client = new FakeGitHubClient();
        string path = GitHubLifecycleTestSupport.Plan().FileChanges[0].RepositoryPath;
        for (int index = 0; index <= PullRequestManifestEvidenceLimits.MaximumCandidates; index++)
        {
            int number = 200 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Relevant maintenance {index}",
                Body = null,
            });
            client.SetPullRequestChangedFiles(number, path);
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Large_upstream_without_duplicate_completes_all_association_checks()
    {
        const int openPullRequestCount = 1_201;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int number = 1; number <= openPullRequestCount; number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(100 + number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.Equal(3, client.SearchCalls);
        Assert.Equal(2, client.TextSearchCalls);
        Assert.Equal(
            [
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests,
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests,
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests + 1,
            ],
            client.PullRequestSearches.Select(static search => search.MaximumResults));
        Assert.Contains(openPullRequestCount, client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Large_upstream_duplicate_is_found_by_narrowed_search_before_mutation()
    {
        const int unrelatedPullRequestCount = 1_201;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int number = 1; number <= unrelatedPullRequestCount; number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(100 + number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7));

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2002");
        Assert.Equal(1, client.TextSearchCalls);
        Assert.Equal(0, client.SearchCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Large_upstream_pre_creation_recheck_detects_duplicate_race()
    {
        const int openPullRequestCount = 1_201;
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            OnSearch = static (fake, call) =>
            {
                if (call == 2)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(8, author: "racer"));
                }
            },
        };
        for (int number = 1; number <= openPullRequestCount; number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(100 + number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2008");
        Assert.True(result.RemoteState.CommitCreated);
        Assert.False(result.RemoteState.PullRequestCreated);
        Assert.Equal(["branch", "commit"], client.Mutations);
    }

    [Fact]
    public async Task Large_upstream_unavailable_diff_uses_pinned_base_content_fallback()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            PullRequestMergeBaseFailure = new GitHubApiException(
                "Synthetic diff generation timeout.",
                HttpStatusCode.UnprocessableEntity,
                null),
        };
        const int fallbackPullRequestNumber = 10_007;
        client.PullRequestContentFallbackNumbers.Add(fallbackPullRequestNumber);
        for (int index = 1; index <= 1_201; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.Equal(["branch", "commit", "pull-request"], client.Mutations);
    }

    [Fact]
    public async Task Direct_evidence_rechecks_new_content_fallback_marker()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            PullRequestMergeBaseFailure = new GitHubApiException(
                "Synthetic diff generation timeout.",
                HttpStatusCode.UnprocessableEntity,
                null),
        };
        client.PullRequestContentFallbackNumbers.Add(7);
        PullRequestInfo pullRequest = GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Unrelated maintenance",
            Body = null,
        };
        client.AddPullRequest(pullRequest);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        PullRequestManifestEvidence evidence = await provider.GetEvidenceAsync(
            plan,
            pullRequest,
            CancellationToken.None);

        Assert.False(evidence.IsAssociated);
        Assert.Equal([1], client.PullRequestFileBatchSizes);
    }

    [Fact]
    public async Task Large_upstream_unavailable_diff_still_detects_target_content_change()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            PullRequestMergeBaseFailure = new GitHubApiException(
                "Synthetic diff generation timeout.",
                HttpStatusCode.UnprocessableEntity,
                null),
        };
        const int fallbackPullRequestNumber = 10_007;
        client.PullRequestContentFallbackNumbers.Add(fallbackPullRequestNumber);
        for (int index = 1; index <= 1_201; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo fallback = client.PullRequests.Single(
            pullRequest => pullRequest.Number == fallbackPullRequestNumber);
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        client.SetContent(
            fallback.HeadRepository!,
            change.RepositoryPath,
            fallback.HeadSha,
            change.Content.AsSpan());

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2002");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Unavailable_merge_base_escalates_ambiguous_base_only_difference()
    {
        const int fallbackPullRequestNumber = 10_007;
        string path = "manifests/e/Example/App/2.0.0/Example.App.yaml";
        byte[] current = "current upstream"u8.ToArray();
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            path,
            "our update"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(current));
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan([change]) with
        {
            BeforeDocuments = [new RawManifestDocument(path, current)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            PullRequestMergeBaseFailure = new GitHubApiException(
                "Synthetic diff generation timeout.",
                HttpStatusCode.UnprocessableEntity,
                null),
        };
        client.PullRequestContentFallbackNumbers.Add(fallbackPullRequestNumber);
        for (int index = 1; index <= 1_201; index++)
        {
            int number = 10_000 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        PullRequestInfo fallback = client.PullRequests.Single(
            pullRequest => pullRequest.Number == fallbackPullRequestNumber);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            current);
        client.SetContent(
            fallback.HeadRepository!,
            path,
            fallback.HeadSha,
            "previous upstream"u8);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "GH2002");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Text_search_failure_falls_back_to_exhaustive_duplicate_evidence()
    {
        var client = new FakeGitHubClient
        {
            PullRequestTextSearchFailure = new GitHubApiException(
                "GitHub pull-request text search returned incomplete results."),
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7));

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2002");
        Assert.Equal(1, client.TextSearchCalls);
        Assert.Equal(1, client.SearchCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Text_search_io_failure_falls_back_to_exhaustive_duplicate_evidence()
    {
        var client = new FakeGitHubClient
        {
            PullRequestTextSearchFailure = new IOException("Synthetic response stream failure."),
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7));

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2002");
        Assert.Equal(1, client.TextSearchCalls);
        Assert.Equal(1, client.SearchCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Text_search_rate_limit_stops_before_exhaustive_api_traffic()
    {
        var client = new FakeGitHubClient
        {
            PullRequestTextSearchFailure = new GitHubApiException(
                "secondary rate limit",
                HttpStatusCode.Forbidden,
                null,
                errorKind: GitHubApiErrorKind.RateLimited),
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.Equal(1, client.TextSearchCalls);
        Assert.Equal(0, client.SearchCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Open_pull_request_discovery_limit_fails_closed_before_evidence_or_mutation()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int number = 1;
             number <= PullRequestManifestEvidenceLimits.MaximumOpenPullRequests + 1;
             number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.Equal(
            PullRequestManifestEvidenceLimits.MaximumOpenPullRequests,
            Assert.Single(client.PullRequestSearches).MaximumResults);
        Assert.Equal(0, client.PullRequestFileBatchCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Workflow_enforces_candidate_limit_for_injected_evidence_providers()
    {
        var client = new FakeGitHubClient();
        for (int number = 1; number <= PullRequestManifestEvidenceLimits.MaximumCandidates + 1; number++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Example.App custom update {number}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                pullRequestEvidence: new FakePullRequestManifestEvidenceProvider(
                    PullRequestManifestEvidence.None))
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.Equal(0, client.PullRequestHeadContentCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Post_creation_reconciliation_excludes_the_new_pr_at_candidate_limit()
    {
        var client = new FakeGitHubClient();
        for (int index = 0; index < PullRequestManifestEvidenceLimits.MaximumCandidates; index++)
        {
            int number = 100 + index;
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(number) with
            {
                Title = $"Unrelated maintenance {number}",
                Body = null,
            });
            client.SetPullRequestChangedFiles(number, $"unrelated/{number}.txt");
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
    }

    [Fact]
    public async Task Post_creation_reconciliation_budgets_new_pr_above_open_limit()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
        };
        for (int index = 0; index < PullRequestManifestEvidenceLimits.MaximumOpenPullRequests; index++)
        {
            client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(10_000 + index) with
            {
                Title = $"Unrelated maintenance {index}",
                Body = null,
            });
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
        Assert.Equal(
            [
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests,
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests,
                PullRequestManifestEvidenceLimits.MaximumOpenPullRequests + 1,
            ],
            client.PullRequestSearches.Select(static search => search.MaximumResults));
    }

    [Fact]
    public async Task Workflow_rejects_injected_candidate_with_changed_repository_identity()
    {
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Hand-authored manifest update",
            Body = null,
        });
        var evidenceProvider = new FakePullRequestManifestEvidenceProvider(
            new(true, true))
        {
            CandidateSelector = candidates =>
            [
                candidates[0] with
                {
                    HeadRepository = new RepositoryCoordinates("other", "other"),
                },
            ],
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                pullRequestEvidence: evidenceProvider)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2034");
        Assert.Equal(0, evidenceProvider.EvidenceCalls);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Unrelated_noncanonical_pr_does_not_block_removal()
    {
        PackageIdentifier package = new("Example.App");
        PackageVersion version = new("2.0.0");
        string path =
            $"{ManifestPaths.GetVersionDirectory(package, version)}/Example.App.yaml";
        byte[] expected = "existing manifest"u8.ToArray();
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            new(
                PlannedChangeKind.Delete,
                path,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(expected)),
        ],
        operation: "Remove") with
        {
            BeforeDocuments = [new RawManifestDocument(path, expected)],
        };
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
        var client = new FakeGitHubClient();
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            7,
            author: GitHubLifecycleTestSupport.Fork.Owner) with
        {
            Title = "Example.App removal cleanup",
            Body = null,
        });
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            path,
            GitHubLifecycleTestSupport.UpstreamSha,
            expected);
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            path,
            GitHubLifecycleTestSupport.CommitSha,
            expected);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with
            {
                LocalPlan = local,
                Operation = GitHubManifestOperation.Remove,
            });

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.PullRequestCreated);
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
    public async Task Canonical_retitle_invalidates_cached_negative_title_evidence()
    {
        var client = new FakeGitHubClient
        {
            AutoConfigureCanonicalPullRequestEvidence = false,
            OnSearch = static (fake, call) =>
            {
                if (call == 2)
                {
                    fake.UpdatePullRequest(7, pullRequest => pullRequest with
                    {
                        Title = "Update version: Example.App version 2.0.0",
                    });
                }
            },
        };
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(7) with
        {
            Title = "Unrelated maintenance",
            Body = null,
        });

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, result.Code);
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
    public async Task Repository_side_branch_reservation_is_safely_adopted_after_crash()
    {
        var client = new FakeGitHubClient();
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            "winmatsch/update/example-app/2.0.0/test",
            GitHubLifecycleTestSupport.UpstreamSha);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.BranchAdopted);
        Assert.False(result.RemoteState.BranchCreated);
        Assert.Equal(["commit", "pull-request"], client.Mutations);
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
        Assert.Equal("winmatsch/submissions/update/example-app/2-0-build", first);
    }

    [Fact]
    public void Legacy_named_branch_context_arguments_remain_source_compatible()
    {
        var context = new GitHubBranchNameContext(
            PackageIdentifier: new PackageIdentifier("Example.App"),
            PackageVersion: new PackageVersion("2.0.0"),
            Operation: GitHubManifestOperation.Update,
            SupersedesPullRequestNumber: null);

        Assert.Equal("Example.App", context.PackageIdentifier.Value);
    }

    [Theory]
    [InlineData(GitHubManifestOperation.New, "New version:")]
    [InlineData(GitHubManifestOperation.Update, "Update version:")]
    [InlineData(GitHubManifestOperation.Replace, "Update version:")]
    [InlineData(GitHubManifestOperation.Add, "Add version:")]
    [InlineData(GitHubManifestOperation.Remove, "Remove version:")]
    public void Commit_and_pull_request_titles_use_canonical_winget_prefixes(
        GitHubManifestOperation operation,
        string prefix)
    {
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan) with
            {
                Operation = operation,
            });

        Assert.StartsWith(prefix, plan.CommitTitle, StringComparison.Ordinal);
        Assert.Equal(plan.CommitTitle, plan.PullRequestTitle);
    }

    [Fact]
    public async Task Conflicting_reservation_uses_a_bounded_unique_suffix()
    {
        var client = new FakeGitHubClient();
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            "winmatsch/update/example-app/2.0.0/test",
            "cccccccccccccccccccccccccccccccccccccccc");

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(
            "winmatsch/update/example-app/2.0.0/test-2",
            result.RemoteState.BranchName);
        Assert.False(result.RemoteState.BranchAdopted);
        Assert.Contains(
            ":branch:winmatsch/update/example-app/2.0.0/test-2:",
            client.LastBranchMutationKey,
            StringComparison.Ordinal);
        Assert.EndsWith(
            ":commit:winmatsch/update/example-app/2.0.0/test-2",
            client.LastCommitMutationKey,
            StringComparison.Ordinal);
        Assert.EndsWith(
            ":pull-request:winmatsch/update/example-app/2.0.0/test-2",
            client.LastPullRequestMutationKey,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exhausted_reservation_suffixes_return_structured_conflict()
    {
        var client = new FakeGitHubClient();
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            client.AddBranch(
                GitHubLifecycleTestSupport.Fork,
                attempt == 1
                    ? "winmatsch/update/example-app/2.0.0/test"
                    : $"winmatsch/update/example-app/2.0.0/test-{attempt}",
                $"{attempt:x40}");
        }

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH2012");
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
            policy: new GitHubSubmissionPolicy
            {
                ForkConsent = ForkConsentPolicy.AllowCreate,
                MinimumReleaseFreshness = TimeSpan.Zero,
            });

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
        local = GitHubLifecycleTestSupport.SynchronizePreflight(local);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Duplicate_hash_evidence_requires_annotated_override(int _)
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
    public async Task Production_evidence_provider_blocks_retired_identifier_before_mutation()
    {
        var evidence = new FakeRepositorySubmissionEvidenceProvider
        {
            Evidence = new()
            {
                InstallerEvidence =
                [
                    new(
                        new PackageIdentifier("Example.App"),
                        new PackageVersion("1.0.0"),
                        new string('A', 64),
                        "manifests/e/Example/App/1.0.0/Example.App.installer.yaml",
                        true),
                ],
            },
        };
        var client = new FakeGitHubClient();

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                repositoryEvidence: evidence)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.InvalidPlan, result.Code);
        Assert.Equal(1, evidence.Calls);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH1013");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Authoritative_retirement_evidence_overrides_stale_request_evidence()
    {
        string hash = new string('A', 64);
        var stale = new RepositoryInstallerEvidence(
            new PackageIdentifier("Example.App"),
            new PackageVersion("1.0.0"),
            hash,
            "manifests/e/Example/App/1.0.0/Example.App.installer.yaml",
            false);
        var evidence = new FakeRepositorySubmissionEvidenceProvider
        {
            Evidence = new()
            {
                InstallerEvidence = [stale with { RetiredIdentifier = true }],
            },
        };
        var client = new FakeGitHubClient();

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                repositoryEvidence: evidence)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with
            {
                RepositoryEvidence = [stale],
            });

        Assert.Equal(GitHubLifecycleResultCode.InvalidPlan, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH1013");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Production_evidence_provider_populates_duplicate_hash_policy_and_vanity_annotations()
    {
        string hash = new string('A', 64);
        LocalOperationPlan basePlan = GitHubLifecycleTestSupport.Plan();
        LocalOperationPlan local = basePlan with
        {
            Preflight = basePlan.Preflight with
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
        var blockedEvidence = new FakeRepositorySubmissionEvidenceProvider
        {
            Evidence = new()
            {
                DuplicateHashes = new()
                {
                    DeniedSha256 = ImmutableHashSet.Create(
                        StringComparer.OrdinalIgnoreCase,
                        hash),
                },
                VanityUrlAnnotations = ["Stable vendor URL revalidated."],
            },
        };
        var client = new FakeGitHubClient();

        GitHubLifecycleResult blocked = await GitHubLifecycleTestSupport.Workflow(
                client,
                repositoryEvidence: blockedEvidence)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.Equal(GitHubLifecycleResultCode.InvalidPlan, blocked.Code);
        Assert.Contains(blocked.Diagnostics, diagnostic => diagnostic.Code == "GH1010");
        Assert.Contains(
            "Stable vendor URL revalidated.",
            blocked.Plan.PullRequestBody,
            StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Nonblocking_revalidation_cleanup_diagnostic_preserves_success_and_recovery_state()
    {
        var client = new FakeGitHubClient();
        var artifacts = new FakeArtifactRevalidator
        {
            Result = new(true, [new("GH1021", "Temporary files require cleanup.")]),
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(
                client,
                artifacts: artifacts)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.True(result.RemoteState.RecoveryRequired);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH1021");
    }

    [Fact]
    public void Release_freshness_defaults_to_four_hours_and_zero_is_explicit_opt_out()
    {
        Assert.Equal(
            TimeSpan.FromHours(4),
            new GitHubSubmissionPolicy().MinimumReleaseFreshness);

        GitHubSubmissionPlan optedOut = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.DoesNotContain(
            optedOut.Diagnostics,
            diagnostic => diagnostic.Code is "GH1014" or "GH1015" or "GH1016");
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
            "Update version: Example.App version 2.0.0 - Update Example.App password=[REDACTED]",
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
    public async Task Later_duplicate_never_closes_the_earlier_tool_pr()
    {
        var client = new FakeGitHubClient
        {
            OnSearch = static (fake, call) =>
            {
                if (call == 3)
                {
                    fake.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
                        99,
                        author: "later-fork",
                        branch: "later-update"));
                }
            },
        };

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.False(result.RemoteState.PullRequestClosed);
        Assert.DoesNotContain("close", client.Mutations);
        Assert.Equal(PullRequestState.Open, client.PullRequests.Single(pr => pr.Number == 42).State);
        Assert.Equal(PullRequestState.Open, client.PullRequests.Single(pr => pr.Number == 99).State);
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
    public async Task Retry_after_commit_response_loss_adopts_only_the_exact_planned_commit()
    {
        var client = new FakeGitHubClient
        {
            FailMutation = "commit",
            CommitCreatedBeforeFailure = true,
            ForkAhead = true,
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request();

        GitHubLifecycleResult first = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, first.Code);
        Assert.True(first.RemoteState.BranchCreated);
        Assert.True(first.RemoteState.RemoteOutcomeUncertain);

        WorkflowFileChange change = request.LocalPlan.FileChanges[0];
        ConfigureExactPlannedCommit(client, change);
        client.FailMutation = null;

        var progress = new FakeSubmissionProgressSink();
        GitHubLifecycleResult retried = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteJournaledAsync(
                request with { ResumeFrom = first.RemoteState },
                progress);

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, retried.Code);
        Assert.True(retried.RemoteState.BranchAdopted);
        Assert.True(retried.RemoteState.CommitCreated);
        Assert.Equal(1, client.Mutations.Count(mutation => mutation == "commit"));
        Assert.Contains(SubmissionJournalState.CommitCreated, progress.States);
    }

    [Fact]
    public async Task Fresh_execution_never_adopts_an_existing_matching_commit()
    {
        var client = new FakeGitHubClient();
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request();
        WorkflowFileChange change = request.LocalPlan.FileChanges[0];
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            "winmatsch/update/example-app/2.0.0/test",
            GitHubLifecycleTestSupport.CommitSha);
        ConfigureExactPlannedCommit(client, change);

        GitHubLifecycleResult result = await GitHubLifecycleTestSupport.Workflow(client)
            .ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(
            "winmatsch/update/example-app/2.0.0/test-2",
            result.RemoteState.BranchName);
        Assert.False(result.RemoteState.BranchAdopted);
        Assert.Equal(1, client.Mutations.Count(mutation => mutation == "commit"));
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

    private static void ConfigureExactPlannedCommit(
        FakeGitHubClient client,
        WorkflowFileChange change)
    {
        client.SetContent(
            GitHubLifecycleTestSupport.Fork,
            change.RepositoryPath,
            GitHubLifecycleTestSupport.CommitSha,
            change.Content.AsSpan());
        client.SetTree(
            GitHubLifecycleTestSupport.Upstream,
            GitHubLifecycleTestSupport.UpstreamSha,
            recursive: true,
            new RepositoryTreeEntry(
                "manifests",
                "base-manifests-tree",
                RepositoryTreeEntryType.Tree,
                null),
            new RepositoryTreeEntry(
                "dependencies/fixture",
                "unchanged-gitlink",
                RepositoryTreeEntryType.Commit,
                null));
        client.SetTree(
            GitHubLifecycleTestSupport.Fork,
            GitHubLifecycleTestSupport.CommitSha,
            recursive: true,
            new RepositoryTreeEntry(
                "manifests",
                "candidate-manifests-tree",
                RepositoryTreeEntryType.Tree,
                null),
            new RepositoryTreeEntry(
                "dependencies/fixture",
                "unchanged-gitlink",
                RepositoryTreeEntryType.Commit,
                null),
            new RepositoryTreeEntry(
                change.RepositoryPath,
                "planned-blob",
                RepositoryTreeEntryType.Blob,
                change.Content.Length));
    }
}
