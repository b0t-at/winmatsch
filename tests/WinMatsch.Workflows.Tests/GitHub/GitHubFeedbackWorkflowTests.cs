using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubFeedbackWorkflowTests
{
    [Theory]
    [InlineData("Duplicate entry found", FeedbackClassification.DuplicateEntry)]
    [InlineData("Installer hash mismatch", FeedbackClassification.HashMismatch)]
    [InlineData("Dependency infrastructure unavailable", FeedbackClassification.DependencyInfrastructureOutage)]
    [InlineData("Transient internal error; please rerun", FeedbackClassification.TransientInternalError)]
    [InlineData("Unrecognized reviewer note", FeedbackClassification.Unknown)]
    public void Feedback_signatures_are_classified(string text, FeedbackClassification expected)
    {
        PullRequestObservation observation = Observation(text);

        FeedbackClassification classification = GitHubFeedbackWorkflow.Classify(observation);

        Assert.Equal(expected, classification);
    }

    [Fact]
    public void Untrusted_comment_cannot_trigger_automated_repair()
    {
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            Comments =
            [
                new("untrusted-contributor", "Installer hash mismatch", DateTimeOffset.UtcNow),
            ],
        };

        FeedbackClassification classification = GitHubFeedbackWorkflow.Classify(observation);

        Assert.Equal(FeedbackClassification.None, classification);
    }

    [Fact]
    public async Task Infrastructure_failure_queues_retry_and_never_mutates_manifests()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Dependency service unavailable")],
            new FeedbackPolicy { ApplyKnownSafeResponses = true });

        Assert.Equal(PullRequestLifecycleAction.RerunChecks, result.Statuses[0].RecommendedAction);
        Assert.Single(result.RetryMetadata);
        Assert.Equal(0, repairs.Calls);
        Assert.Equal(["comment"], client.Mutations);
        Assert.Equal(FeedbackWorkState.RetryScheduled, Assert.Single(store.Items).State);
    }

    [Fact]
    public async Task Infrastructure_response_failure_escalates_instead_of_throwing()
    {
        var client = new FakeGitHubClient { FailMutation = "comment" };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Dependency service unavailable")],
            new FeedbackPolicy { ApplyKnownSafeResponses = true });

        Assert.Equal(
            PullRequestLifecycleAction.EscalateToHuman,
            result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3207");
    }

    [Fact]
    public async Task Approved_repair_is_forced_through_submission_planning_and_preflight_contract()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                TargetRepository = null,
                SupersedesPullRequestNumber = 20,
            },
        };
        var preflight = new FakePreflight();
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client, preflight),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.RepairManifest, result.Statuses[0].RecommendedAction);
        Assert.Equal(1, repairs.Calls);
        Assert.Equal(1, preflight.BoundaryCalls);
        Assert.Equal(["branch", "commit", "pull-request", "comment", "close"], client.Mutations);
        Assert.Equal(2, result.RemoteStates.Length);
        Assert.True(result.RemoteStates[0].State.CommitCreated);
        Assert.True(result.RemoteStates[1].State.PullRequestClosed);
    }

    [Fact]
    public async Task Partial_supersession_propagates_recoverable_mutation_state()
    {
        var client = new FakeGitHubClient { FailMutation = "close" };
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                TargetRepository = null,
                SupersedesPullRequestNumber = 20,
            },
        };
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(
            PullRequestLifecycleAction.EscalateToHuman,
            result.Statuses[0].RecommendedAction);
        Assert.Equal(2, result.RemoteStates.Length);
        Assert.True(result.RemoteStates[1].State.CommentCreated);
        Assert.Equal(
            RemoteOperationKind.ClosePullRequest,
            result.RemoteStates[1].State.LastAttemptedOperation);
        Assert.True(result.RemoteStates[1].State.RemoteOutcomeUncertain);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3206");
    }

    [Fact]
    public async Task Supersession_read_failure_escalates_and_persists_without_throwing()
    {
        var client = new FakeGitHubClient
        {
            FailPullRequestReadNumber = 20,
        };
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                SupersedesPullRequestNumber = 20,
            },
        };
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3211");
        Assert.Equal(FeedbackWorkState.Escalated, store.Items[^1].State);
        Assert.DoesNotContain("close", client.Mutations);
    }

    [Fact]
    public async Task Concurrent_old_pr_head_change_prevents_automatic_supersession_close()
    {
        var client = new FakeGitHubClient
        {
            OnGetPullRequest = static (fake, number) =>
            {
                if (number == 20)
                {
                    fake.UpdatePullRequest(
                        number,
                        static pullRequest => pullRequest with
                        {
                            HeadSha = "cccccccccccccccccccccccccccccccccccccccc",
                        });
                }
            },
        };
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                SupersedesPullRequestNumber = 20,
            },
        };
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3205");
        Assert.DoesNotContain("close", client.Mutations);
    }

    [Fact]
    public async Task Replay_adopts_proven_existing_replacement_and_finishes_supersession()
    {
        var client = new FakeGitHubClient();
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        PullRequestInfo replacement = GitHubLifecycleTestSupport.PullRequest(
            42,
            author: GitHubLifecycleTestSupport.Fork.Owner,
            branch: "winmatsch/update/example-app/replacement") with
        {
            Body = GitHubLifecycleTestSupport.PullRequest(42).Body + "\nSupersedes: #20",
        };
        client.AddPullRequest(observation.PullRequest);
        client.AddPullRequest(replacement);
        var store = new FakeFeedbackStateStore();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                TargetRepository = null,
                SupersedesPullRequestNumber = 20,
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.RepairManifest, result.Statuses[0].RecommendedAction);
        Assert.Equal(["comment", "close"], client.Mutations);
        Assert.Equal(PullRequestState.Closed, client.PullRequests.Single(pr => pr.Number == 20).State);
        Assert.Equal(PullRequestState.Open, client.PullRequests.Single(pr => pr.Number == 42).State);
        Assert.Equal(FeedbackWorkState.Completed, store.Items[^1].State);
    }

    [Fact]
    public async Task Replay_rejects_existing_superseding_pr_for_a_different_operation()
    {
        var client = new FakeGitHubClient();
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        PullRequestInfo wrongOperation = GitHubLifecycleTestSupport.PullRequest(
            42,
            author: GitHubLifecycleTestSupport.Fork.Owner,
            branch: "winmatsch/remove/example-app/replacement") with
        {
            Title = "Remove version: Example.App version 2.0.0",
            Body = GitHubLifecycleTestSupport.PullRequest(42).Body!
                .Replace("operation=Update", "operation=Remove", StringComparison.Ordinal)
                + "\nSupersedes: #20",
        };
        client.AddPullRequest(observation.PullRequest);
        client.AddPullRequest(wrongOperation);
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner
            {
                Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
                {
                    SupersedesPullRequestNumber = 20,
                },
            },
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3212");
        Assert.DoesNotContain("close", client.Mutations);
    }

    [Fact]
    public async Task Unknown_feedback_escalates_before_stale_window_without_unsafe_action()
    {
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Reviewer asks an unknown question")],
            new FeedbackPolicy { StaleEscalationWindow = TimeSpan.FromDays(30) });

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains("before", result.Statuses[0].Reason, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Missing_planner_output_is_durably_queued_for_allowlisted_repair()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.RepairManifest, result.Statuses[0].RecommendedAction);
        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackWorkState.AwaitingApprovedRepair, item.State);
        Assert.Equal("hash-mismatch", item.LearnedOverrideSignal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Repair_for_different_package_is_rejected_before_any_mutation()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                LocalPlan = GitHubLifecycleTestSupport.Plan() with
                {
                    PackageIdentifier = new WinMatsch.Core.PackageIdentifier("Other.App"),
                },
                SupersedesPullRequestNumber = 20,
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3209");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Repair_for_different_upstream_is_rejected_before_any_mutation()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                UpstreamRepository = new RepositoryCoordinates("other", "repo"),
                SupersedesPullRequestNumber = 20,
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3209");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Replace_repair_cannot_expand_an_original_update_operation()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                Operation = GitHubManifestOperation.Replace,
                SupersedesPullRequestNumber = 20,
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3209");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Update_repair_cannot_hide_previous_version_deletion_in_policy()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                SupersedesPullRequestNumber = 20,
                Policy = new()
                {
                    ReplacePreviousVersion = true,
                    PreviousVersion = new WinMatsch.Core.PackageVersion("1.0.0"),
                    MinimumReleaseFreshness = TimeSpan.Zero,
                },
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3209");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Feedback_persistence_failure_escalates_instead_of_reporting_queued_success()
    {
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            new FakeFeedbackStateStore { Failure = new IOException("disk unavailable") });

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Installer hash mismatch")]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3208");
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task File_feedback_store_persists_atomic_executable_work_item()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-feedback-test-{Guid.NewGuid():N}");
        try
        {
            string? synchronizedDirectory = null;
            var store = new FileFeedbackStateStore(
                root,
                directory => synchronizedDirectory = directory);
            var item = new FeedbackWorkItem(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                20,
                FeedbackClassification.HashMismatch,
                FeedbackWorkState.AwaitingApprovedRepair,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                "hash-mismatch",
                "Awaiting approved repair.");

            await store.PersistAsync(item, CancellationToken.None);

            string file = Assert.Single(Directory.EnumerateFiles(root, "*.json"));
            string json = await File.ReadAllTextAsync(file);
            Assert.Contains("\"pullRequestNumber\": 20", json, StringComparison.Ordinal);
            Assert.Contains("\"state\": \"AwaitingApprovedRepair\"", json, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
            Assert.Equal(root, synchronizedDirectory);
            System.Collections.Immutable.ImmutableArray<FeedbackWorkItem> pending =
                await store.GetPendingAsync(
                    GitHubLifecycleTestSupport.Upstream.ToString(),
                    item.RetryAfter!.Value,
                    CancellationToken.None);
            Assert.Equal(item, Assert.Single(pending));
            await store.PersistAsync(
                item with
                {
                    State = FeedbackWorkState.Completed,
                    Reason = "Completed at the same wall-clock timestamp.",
                },
                CancellationToken.None);
            Assert.Empty(await store.GetPendingAsync(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                item.RetryAfter.Value,
                CancellationToken.None));
            await store.PersistAsync(
                item with
                {
                    RecordedAt = item.RecordedAt.AddDays(1),
                    Reason = "Stale pending writer completed late.",
                },
                CancellationToken.None);
            Assert.Empty(await store.GetPendingAsync(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                item.RetryAfter.Value.AddDays(2),
                CancellationToken.None));
            await store.PersistAsync(
                item with
                {
                    State = FeedbackWorkState.Escalated,
                    RecordedAt = item.RecordedAt.AddDays(2),
                    Reason = "A stale terminal writer completed late.",
                },
                CancellationToken.None);
            json = await File.ReadAllTextAsync(file);
            Assert.Contains("\"state\": \"Completed\"", json, StringComparison.Ordinal);
            Assert.Single(Directory.EnumerateFiles(root, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task File_feedback_store_serializes_concurrent_writers()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-feedback-concurrency-{Guid.NewGuid():N}");
        try
        {
            var store = new FileFeedbackStateStore(root);
            DateTimeOffset start = DateTimeOffset.UtcNow;
            Task[] writes =
            [
                .. Enumerable.Range(0, 20).Select(index => store.PersistAsync(
                    new(
                        GitHubLifecycleTestSupport.Upstream.ToString(),
                        20,
                        FeedbackClassification.HashMismatch,
                        FeedbackWorkState.AwaitingApprovedRepair,
                        start.AddTicks(index),
                        start,
                        "hash-mismatch",
                        $"Queued {index}."),
                    CancellationToken.None)),
            ];

            await Task.WhenAll(writes);

            Assert.Single(Directory.EnumerateFiles(root, "*.json"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task File_feedback_store_retries_directory_sync_after_post_rename_failure()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-feedback-retry-{Guid.NewGuid():N}");
        try
        {
            int synchronizationAttempts = 0;
            var store = new FileFeedbackStateStore(
                root,
                _ =>
                {
                    synchronizationAttempts++;
                    if (synchronizationAttempts == 1)
                    {
                        throw new IOException("directory sync failed");
                    }
                });
            var item = new FeedbackWorkItem(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                20,
                FeedbackClassification.HashMismatch,
                FeedbackWorkState.AwaitingApprovedRepair,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                "hash-mismatch",
                "Awaiting approved repair.");

            await Assert.ThrowsAsync<IOException>(() =>
                store.PersistAsync(item, CancellationToken.None));
            Assert.Single(Directory.EnumerateFiles(root, "*.json"));

            await store.PersistAsync(item, CancellationToken.None);

            Assert.Equal(2, synchronizationAttempts);
            Assert.Single(Directory.EnumerateFiles(root, "*.json"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Durable_pending_work_replays_through_full_submission_workflow()
    {
        var client = new FakeGitHubClient();
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var store = new FakeFeedbackStateStore();
        await store.PersistAsync(
            new(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                20,
                FeedbackClassification.HashMismatch,
                FeedbackWorkState.AwaitingApprovedRepair,
                new FakeClock().UtcNow.AddMinutes(-1),
                new FakeClock().UtcNow,
                "hash-mismatch",
                "Queued."),
            CancellationToken.None);
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                SupersedesPullRequestNumber = 20,
            },
        };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ReplayPendingAsync(
            GitHubLifecycleTestSupport.Upstream,
            new FakeFeedbackSource([observation]));

        Assert.Equal(PullRequestLifecycleAction.RepairManifest, result.Statuses[0].RecommendedAction);
        Assert.Equal(["branch", "commit", "pull-request", "comment", "close"], client.Mutations);
        Assert.Equal(1, repairs.Calls);
        Assert.Contains(store.Items, item => item.State == FeedbackWorkState.Completed);
    }

    [Fact]
    public async Task Replay_terminalizes_pending_work_for_a_vanished_pull_request()
    {
        var store = new FakeFeedbackStateStore();
        var clock = new FakeClock();
        await store.PersistAsync(
            new(
                GitHubLifecycleTestSupport.Upstream.ToString(),
                20,
                FeedbackClassification.HashMismatch,
                FeedbackWorkState.AwaitingApprovedRepair,
                clock.UtcNow.AddMinutes(-1),
                clock.UtcNow,
                "hash-mismatch",
                "Queued."),
            CancellationToken.None);
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            clock,
            store);

        FeedbackResult result = await workflow.ReplayPendingAsync(
            GitHubLifecycleTestSupport.Upstream,
            new FakeFeedbackSource([]));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3210");
        Assert.Equal(FeedbackWorkState.Escalated, store.Items[^1].State);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Replace_repair_must_delete_the_exact_original_previous_version()
    {
        string originalPath =
            "manifests/e/Example/App/1.0.0/Example.App.yaml";
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(20) with
            {
                Body = GitHubLifecycleTestSupport.PullRequest(20).Body!
                    .Replace("operation=Update", "operation=Replace", StringComparison.Ordinal),
            },
            ChangedFiles =
            [
                new(originalPath, Status: PullRequestFileStatus.Removed),
            ],
            EvidenceHeadSha = GitHubLifecycleTestSupport.CommitSha,
            EvidenceBaseSha = GitHubLifecycleTestSupport.UpstreamSha,
        };
        LocalOperationPlan basePlan = GitHubLifecycleTestSupport.Plan();
        LocalOperationPlan repairPlan = GitHubLifecycleTestSupport.Plan(
        [
            basePlan.FileChanges[0],
            new(
                PlannedChangeKind.Delete,
                originalPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash("old"u8)),
        ]);
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                LocalPlan = repairPlan,
                Operation = GitHubManifestOperation.Replace,
                SupersedesPullRequestNumber = 20,
                Policy = new()
                {
                    ReplacePreviousVersion = true,
                    PreviousVersion = new WinMatsch.Core.PackageVersion("0.9.0"),
                    MinimumReleaseFreshness = TimeSpan.Zero,
                },
            },
        };
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock(),
            new FakeFeedbackStateStore());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3209");
        Assert.Empty(client.Mutations);
    }

    private static PullRequestObservation Observation(string text)
        => new()
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(20),
            Author = "contributor",
            ToolOwned = true,
            Comments =
            [
                new("wingetbot", text, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            ],
        };
}

internal sealed class FakeRepairPlanner : IApprovedRepairPlanner
{
    public int Calls { get; private set; }

    public GitHubSubmissionRequest? Repair { get; init; }

    public Task<GitHubSubmissionRequest?> PlanApprovedRepairAsync(
        PullRequestObservation pullRequest,
        FeedbackClassification classification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Repair);
    }
}

internal sealed class FakeFeedbackStateStore : IFeedbackStateStore
{
    public List<FeedbackWorkItem> Items { get; } = [];

    public Exception? Failure { get; init; }

    public Task PersistAsync(
        FeedbackWorkItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Items.Add(item);
        return Task.CompletedTask;
    }

    public Task<System.Collections.Immutable.ImmutableArray<FeedbackWorkItem>> GetPendingAsync(
        string repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<System.Collections.Immutable.ImmutableArray<FeedbackWorkItem>>(
        [
            .. Items
                .Where(item => string.Equals(
                    item.Repository,
                    repository,
                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(static item => item.PullRequestNumber)
                .Select(static group => group.MaxBy(static item => item.RecordedAt)!)
                .Where(item => (item.State is FeedbackWorkState.AwaitingApprovedRepair
                    or FeedbackWorkState.RetryScheduled)
                    && item.RetryAfter.GetValueOrDefault(DateTimeOffset.MinValue) <= now),
        ]);
    }
}

internal sealed class FakeFeedbackSource(
    System.Collections.Immutable.ImmutableArray<PullRequestObservation> observations)
    : IPullRequestFeedbackSource
{
    public Task<System.Collections.Immutable.ImmutableArray<PullRequestObservation>>
        GetOpenToolPullRequestsAsync(
            RepositoryCoordinates upstream,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(observations);
    }
}
