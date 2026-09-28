using WinMatsch.Core;
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

    [Theory]
    [InlineData("Error-Hash-Mismatch", FeedbackClassification.HashMismatch)]
    [InlineData("Validation-Hash-Verification-Failed", FeedbackClassification.HashMismatch)]
    [InlineData("Possible-Duplicate", FeedbackClassification.DuplicateEntry)]
    [InlineData("Resolution-Duplicate", FeedbackClassification.DuplicateEntry)]
    [InlineData("Validation-Defender-Error", FeedbackClassification.ScannerBlocked)]
    [InlineData("Binary-Validation-Error", FeedbackClassification.ScannerBlocked)]
    [InlineData("Validation-Certificate-Root", FeedbackClassification.UntrustedCertificate)]
    [InlineData("URL-Validation-Error", FeedbackClassification.UrlValidationError)]
    [InlineData("Error-Installer-Availability", FeedbackClassification.InstallerUnavailable)]
    [InlineData("Validation-Unattended-Failed", FeedbackClassification.InstallationFailure)]
    [InlineData("Validation-Shell-Execute", FeedbackClassification.InstallationFailure)]
    [InlineData("DriverInstall", FeedbackClassification.InstallationFailure)]
    [InlineData("Internal-Error-Dynamic-Scan", FeedbackClassification.TransientInternalError)]
    [InlineData("Retry-1", FeedbackClassification.TransientInternalError)]
    [InlineData("Validation-Executable-Error", FeedbackClassification.AwaitingManualValidation)]
    [InlineData("Validation-No-Executables", FeedbackClassification.AwaitingManualValidation)]
    public void Upstream_validator_labels_are_classified(string label, FeedbackClassification expected)
    {
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Labels = [label, "New-Manifest", "Validation-Guide"],
        };

        FeedbackClassification classification = GitHubFeedbackWorkflow.Classify(observation);

        Assert.Equal(expected, classification);
    }

    [Fact]
    public void Blocking_verdict_outranks_manual_validation_marker()
    {
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Labels = ["Azure-Pipeline-Passed", "Validation-Executable-Error", "Validation-Defender-Error"],
        };

        Assert.Equal(FeedbackClassification.ScannerBlocked, GitHubFeedbackWorkflow.Classify(observation));
    }

    [Theory]
    [InlineData(
        "Possible duplicate package entry. Similar installer SHA256 hash found in manifest manifests/s/StacksLabs/Clarinet/3.23.1",
        FeedbackClassification.DuplicateEntry)]
    [InlineData(
        "Url Validation Error\n- manifests/y/yhay81/sqrail/0.3.4\n  - https://sqrails.yhay81.com\n    - No such host is known.",
        FeedbackClassification.UrlValidationError)]
    [InlineData(
        "One or more ESRP Scan Blocking detections found: Installer: k0sctl-win-amd64.exe | Detection Engine: AVAST | Detection Description: Win64:Malware-gen",
        FeedbackClassification.ScannerBlocked)]
    public void Validator_bot_comments_are_trusted_signatures(string text, FeedbackClassification expected)
    {
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Comments =
            [
                new("wingetvalidator-prod", text, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        Assert.Equal(expected, GitHubFeedbackWorkflow.Classify(observation));
    }

    [Fact]
    public async Task Manual_validation_marker_waits_without_persisting_work()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Labels = ["Azure-Pipeline-Passed", "Validation-Executable-Error"],
        };

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.Wait, result.Statuses[0].RecommendedAction);
        Assert.Contains("do not supersede", result.Statuses[0].Reason, StringComparison.Ordinal);
        Assert.Empty(store.Items);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Blocking_upstream_verdict_escalates_and_persists_the_class()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Labels = ["New-Manifest", "Validation-Certificate-Root"],
        };

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "GH3211");
        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackClassification.UntrustedCertificate, item.Classification);
        Assert.Equal(FeedbackWorkState.Escalated, item.State);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Blocking_url_verdict_persists_package_identity_and_rejected_urls()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);
        PullRequestObservation observation = Observation("Validation pipeline passed.") with
        {
            Labels = ["URL-Validation-Error", "Needs-Author-Feedback"],
            Comments =
            [
                new(
                    "wingetvalidator-prod",
                    "Url Validation Error\n- manifests/y/yhay81/sqrail/0.3.4\n  - https://sqrails.yhay81.com\n    - No such host is known.",
                    new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackClassification.UrlValidationError, item.Classification);
        Assert.Equal("Example.App", item.PackageIdentifier);
        Assert.Equal("2.0.0", item.PackageVersion);
        Assert.Equal("https://sqrails.yhay81.com", Assert.Single(item.Evidence!));
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
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Reviewer asks an unknown question")],
            new FeedbackPolicy { StaleEscalationWindow = TimeSpan.FromDays(30) });

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains("before", result.Statuses[0].Reason, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);

        // A fresh unknown signal is not recorded: the store treats Escalated as terminal, and
        // the validator's real verdict usually arrives later.
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Stale_unknown_feedback_is_recorded()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);

        await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Reviewer asks an unknown question")],
            new FeedbackPolicy { StaleEscalationWindow = TimeSpan.Zero });

        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackClassification.Unknown, item.Classification);
        Assert.Equal(FeedbackWorkState.Escalated, item.State);
    }

    [Fact]
    public void Healthy_pipeline_labels_are_not_unknown_feedback()
    {
        // Every PR carries routine policy-service comments ("Validation Pipeline Badge",
        // "Validation has completed"); with passing labels they must not read as unknown.
        PullRequestObservation observation = Observation("Validation has completed.") with
        {
            Labels = ["Azure-Pipeline-Passed", "Validation-Completed", "New-Manifest"],
        };

        Assert.Equal(FeedbackClassification.None, GitHubFeedbackWorkflow.Classify(observation));
    }

    [Fact]
    public void Passing_labels_do_not_hide_a_request_for_author_changes()
    {
        PullRequestObservation observation = Observation("Please update the license URL.") with
        {
            Labels = ["Azure-Pipeline-Passed", "Validation-Completed", "Needs-Author-Feedback"],
        };

        Assert.Equal(FeedbackClassification.Unknown, GitHubFeedbackWorkflow.Classify(observation));
    }

    [Fact]
    public async Task Keep_alive_comments_are_never_posted_on_branch_prefix_pull_requests()
    {
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            new FakeFeedbackStateStore());
        PullRequestObservation observation = Observation("Internal error, please rerun.") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(20) with
            {
                Title = "Update version: Example.App version 2.0.0",
                Body = "Update Example.App to version 2.0.0.",
                HeadBranch = "winget-autosubmit/example.app-2.0.0-0123456789abcdef",
            },
            Labels = ["Internal-Error-Dynamic-Scan"],
            AssociatedPackageIdentifier = "Example.App",
            AssociatedPackageVersion = "2.0.0",
        };

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation],
            new FeedbackPolicy { ApplyKnownSafeResponses = true });

        Assert.Equal(PullRequestLifecycleAction.RerunChecks, result.Statuses[0].RecommendedAction);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Unreadable_rejected_manifest_still_records_the_installation_verdict()
    {
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);
        var headRepository = new RepositoryCoordinates("damn-good-b0t", "winget-pkgs");
        const string installerPath =
            "manifests/e/Example/App/2.0.0/Example.App.installer.yaml";
        client.SetContent(headRepository, installerPath, "head-sha", "PackageIdentifier: [unterminated"u8);
        PullRequestObservation observation = Observation("Installation failed.") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(20) with
            {
                HeadSha = "head-sha",
                HeadRepository = headRepository,
            },
            Labels = ["Validation-Unattended-Failed"],
            ChangedFiles = [new(installerPath)],
        };

        await workflow.ProcessAsync(GitHubLifecycleTestSupport.Upstream, [observation]);

        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackClassification.InstallationFailure, item.Classification);
        Assert.Equal(FeedbackWorkState.Escalated, item.State);
        Assert.Null(item.InstallerTraits);
    }

    [Fact]
    public async Task Installation_failure_on_another_automations_pull_request_records_identity_and_traits()
    {
        // Pipeline ForkBranch PRs carry no winmatsch body marker; the observation source supplies
        // the package from the conventional title, and the rejected installer traits are read
        // from the PR head because the rejected version is never merged (DiRoots.ProSheets).
        var client = new FakeGitHubClient();
        var store = new FakeFeedbackStateStore();
        var headRepository = new RepositoryCoordinates("damn-good-b0t", "winget-pkgs");
        const string installerPath =
            "manifests/d/DiRoots/ProSheets/2.4.1/DiRoots.ProSheets.installer.yaml";
        var rejected = new InstallerManifest
        {
            PackageIdentifier = new PackageIdentifier("DiRoots.ProSheets"),
            PackageVersion = new PackageVersion("2.4.1"),
            InstallerType = InstallerType.Exe,
            Scope = Scope.Machine,
            InstallerSwitches = new InstallerSwitches { Silent = "/i // /qn accept_eula=1" },
            Installers =
            [
                new Installer
                {
                    Architecture = Architecture.X64,
                    InstallerUrl = "https://example.test/ProSheets-2.4.1.exe",
                    InstallerSha256 = new Sha256Hash(new string('A', 64)),
                },
            ],
        };
        client.SetContent(
            headRepository,
            installerPath,
            "head-sha",
            System.Text.Encoding.UTF8.GetBytes(WinMatsch.Core.Yaml.ManifestYamlWriter.Serialize(rejected)));
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock(),
            store);
        PullRequestObservation observation = new()
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(420154) with
            {
                Title = "Update version: DiRoots.ProSheets version 2.4.1",
                Body = "Update DiRoots.ProSheets to version 2.4.1.",
                HeadBranch = "winget-autosubmit/diroots.prosheets-2.4.1-0123456789abcdef",
                HeadSha = "head-sha",
                HeadRepository = headRepository,
            },
            Author = "damn-good-b0t",
            ToolOwned = true,
            Labels = ["Validation-Unattended-Failed", "Needs-Author-Feedback"],
            ChangedFiles = [new(installerPath)],
            AssociatedPackageIdentifier = "DiRoots.ProSheets",
            AssociatedPackageVersion = "2.4.1",
        };

        await workflow.ProcessAsync(GitHubLifecycleTestSupport.Upstream, [observation]);

        FeedbackWorkItem item = Assert.Single(store.Items);
        Assert.Equal(FeedbackClassification.InstallationFailure, item.Classification);
        Assert.Equal("DiRoots.ProSheets", item.PackageIdentifier);
        Assert.Equal("2.4.1", item.PackageVersion);
        Assert.Equal(UpstreamVerdictGate.InstallerTraits(rejected), item.InstallerTraits);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task File_feedback_store_upgrades_a_non_blocking_terminal_item_to_a_blocking_verdict()
    {
        string root = Path.Combine(Path.GetTempPath(), $"winmatsch-feedback-{Guid.NewGuid():N}");
        var store = new FileFeedbackStateStore(root);
        var recordedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await store.PersistAsync(
                new("microsoft/winget-pkgs", 7, FeedbackClassification.Unknown, FeedbackWorkState.Escalated,
                    recordedAt, null, null, "stale", "Example.App", "1.0.0"),
                CancellationToken.None);
            await store.PersistAsync(
                new("microsoft/winget-pkgs", 7, FeedbackClassification.ScannerBlocked, FeedbackWorkState.Escalated,
                    recordedAt.AddDays(1), null, null, "defender", "Example.App", "1.0.0")
                {
                    InstallerTraits = "type=exe",
                },
                CancellationToken.None);
            await store.PersistAsync(
                new("microsoft/winget-pkgs", 7, FeedbackClassification.Unknown, FeedbackWorkState.Escalated,
                    recordedAt.AddDays(2), null, null, "later unknown", "Example.App", "1.0.0"),
                CancellationToken.None);

            FeedbackWorkItem item = Assert.Single(await store.GetByPackageAsync(
                "microsoft/winget-pkgs",
                new PackageIdentifier("Example.App"),
                CancellationToken.None));
            Assert.Equal(FeedbackClassification.ScannerBlocked, item.Classification);
            Assert.Equal("type=exe", item.InstallerTraits);
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
