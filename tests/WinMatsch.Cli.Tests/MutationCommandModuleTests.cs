using System.Collections.Immutable;
using System.Text;
using WinMatsch.Analysis;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Rules;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class MutationCommandModuleTests
{
    public static TheoryData<string> Commands =>
        new()
        {
            "new Example.App --version 2.0 --urls https://example.test/app.exe --publisher Example --package-name App --license MIT --short-description App",
            "update Example.App 1.0 --version 2.0 --urls https://example.test/app.exe",
            "remove Example.App 1.0 --yes",
            "submit input",
            "new-locale Example.App 1.0 de-DE --publisher Beispiel",
            "update-locale Example.App 1.0 de-DE --publisher Beispiel",
        };

    [Fact]
    public async Task Help_exports_all_mutation_commands_and_documented_options()
    {
        CliHarness harness = CreateHarness(new FakeMutationWorkflow());

        CliRunResult root = await harness.RunAsync(["--help"]);
        CliRunResult command = await harness.RunAsync(["new", "--help"]);

        Assert.Equal(ExitCodes.Success, root.ExitCode);
        Assert.Contains("new-locale", root.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("update-locale", root.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--urls", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--url", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--submit", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--dry-run", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--output", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--concurrent-downloads", command.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--override-pack", command.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Every_command_plans_then_applies(string commandLine)
    {
        var workflow = new FakeMutationWorkflow();
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(commandLine.Split(' '));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(2, workflow.Requests.Count);
        Assert.Equal(WorkflowExecutionMode.Plan, workflow.Requests[0].ExecutionMode);
        Assert.Equal(WorkflowExecutionMode.Apply, workflow.Requests[1].ExecutionMode);
        Assert.Contains("Applied: true", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Dry_run_never_applies_or_submits(string commandLine)
    {
        var workflow = new FakeMutationWorkflow();
        var submission = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submission);
        string[] invocation = [.. commandLine.Split(' '), "--dry-run", "--submit", "--yes"];

        CliRunResult result = await harness.RunAsync(invocation);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Single(workflow.Requests);
        Assert.Equal(WorkflowExecutionMode.Plan, workflow.Requests[0].ExecutionMode);
        Assert.Single(submission.Requests);
        Assert.Equal(WorkflowExecutionMode.Plan, submission.Requests[0].ExecutionMode);
    }

    [Fact]
    public async Task Submission_uses_the_resolved_release_freshness_delay()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request =>
            {
                WorkflowOperationResult result = FakeMutationWorkflow.Result(request);
                return result with
                {
                    Plan = result.Plan with
                    {
                        Release = new(
                            new RepositoryCoordinates("vendor", "app"),
                            42,
                            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
                    },
                };
            },
        };
        var submission = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submission);
        harness.EnvironmentVariables["WINMATSCH_FRESHNESS_DELAY"] = "00:42:00";

        CliRunResult result = await harness.RunAsync(
            [
                "new",
                "Example.App",
                "--version",
                "2.0",
                "--urls",
                "https://example.test/app.exe",
                "--publisher",
                "Example",
                "--package-name",
                "App",
                "--license",
                "MIT",
                "--short-description",
                "App",
                "--dry-run",
                "--submit",
                "--yes",
            ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        GitHubSubmissionRequest request = Assert.Single(submission.Requests);
        Assert.Equal(TimeSpan.FromMinutes(42), request.Policy.MinimumReleaseFreshness);
        Assert.Equal(new RepositoryCoordinates("vendor", "app"), request.ReleaseRepository);
        Assert.Equal(42, request.ReleaseId);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), request.ReleaseUpdatedAt);
    }

    [Fact]
    public async Task Non_release_submission_does_not_apply_release_freshness()
    {
        var workflow = new FakeMutationWorkflow();
        var submission = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submission);
        harness.EnvironmentVariables["WINMATSCH_FRESHNESS_DELAY"] = "00:42:00";

        CliRunResult result = await harness.RunAsync(
            ["remove", "Example.App", "1.0", "--yes", "--dry-run", "--submit"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        GitHubSubmissionRequest request = Assert.Single(submission.Requests);
        Assert.Equal(TimeSpan.Zero, request.Policy.MinimumReleaseFreshness);
        Assert.Null(request.ReleaseRepository);
        Assert.Null(request.ReleaseId);
        Assert.Null(request.ReleaseUpdatedAt);
    }

    [Fact]
    public async Task Editing_preserves_release_freshness_provenance_for_submission()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request =>
            {
                WorkflowOperationResult result = FakeMutationWorkflow.Result(request);
                return request is SubmitOperationRequest
                    ? result
                    : result with
                    {
                        Plan = result.Plan with
                        {
                            Release = new(
                                new RepositoryCoordinates("vendor", "app"),
                                42,
                                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
                        },
                    };
            },
        };
        var submission = new FakeSubmissionWorkflow();
        var editor = new FakeEditorRunner();
        CliHarness harness = CreateHarness(workflow, submission, editor);
        harness.EnvironmentVariables["WINMATSCH_FRESHNESS_DELAY"] = "00:42:00";

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--edit", "--submit", "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        GitHubSubmissionRequest request = Assert.Single(submission.Requests);
        Assert.Equal(TimeSpan.FromMinutes(42), request.Policy.MinimumReleaseFreshness);
        Assert.Equal(new RepositoryCoordinates("vendor", "app"), request.ReleaseRepository);
        Assert.Equal(42, request.ReleaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_provenance_failure_is_visible_and_blocks_remote_submission(bool json)
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request =>
            {
                WorkflowOperationResult result = FakeMutationWorkflow.Result(request);
                return request.ExecutionMode == WorkflowExecutionMode.Apply
                    ? result with
                    {
                        Applied = true,
                        ErrorMessage = "Committed, but provenance failed for https://example.test/a?sig=SECRET.",
                    }
                    : result;
            },
        };
        var submission = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submission);
        string[] args =
        [
            "update",
            "Example.App",
            "1.0",
            "--submit",
            "--yes",
            .. json ? new[] { "--format", "json" } : Array.Empty<string>(),
        ];

        CliRunResult result = await harness.RunAsync(args);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Empty(submission.Requests);
        Assert.DoesNotContain("SECRET", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(json ? "\"warning\"" : "Warning:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_question_never_prompts_and_returns_missing_input()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => Question(request),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(
            ["new", "Example.App", "--version", "2.0", "--format", "json"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("update")]
    [InlineData("remove")]
    [InlineData("submit")]
    [InlineData("new-locale")]
    [InlineData("update-locale")]
    public async Task Noninteractive_missing_required_argument_returns_missing_input(string command)
    {
        CliHarness harness = CreateHarness(new FakeMutationWorkflow());
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync([command, "--format", "json"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Interactive_question_creates_new_request_and_reruns_plan()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => request is NewOperationRequest create
                && create.Locale.Publisher is null
                    ? Question(request)
                    : FakeMutationWorkflow.Result(request),
        };
        CliHarness harness = CreateHarness(workflow);
        harness.Interaction.EnqueueText("Example Publisher");

        CliRunResult result = await harness.RunAsync(
            ["new", "Example.App", "--version", "2.0"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(3, workflow.Requests.Count);
        var first = Assert.IsType<NewOperationRequest>(workflow.Requests[0]);
        var rerun = Assert.IsType<NewOperationRequest>(workflow.Requests[1]);
        Assert.Null(first.Locale.Publisher);
        Assert.Equal("Example Publisher", rerun.Locale.Publisher);
        Assert.Single(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Destructive_noninteractive_requires_yes()
    {
        var workflow = new FakeMutationWorkflow();
        CliHarness harness = CreateHarness(workflow);
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(["remove", "Example.App", "1.0"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Single(workflow.Requests);
        Assert.Equal(WorkflowExecutionMode.Plan, workflow.Requests[0].ExecutionMode);
    }

    [Fact]
    public async Task Review_requires_explicit_approval_and_reruns_plan()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => request.ApproveReview
                ? FakeMutationWorkflow.Result(request)
                : Review(request),
        };
        CliHarness harness = CreateHarness(workflow);
        harness.Interaction.EnqueueConfirm(true);

        CliRunResult result = await harness.RunAsync(["update", "Example.App", "1.0"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(3, workflow.Requests.Count);
        Assert.False(workflow.Requests[0].ApproveReview);
        Assert.True(workflow.Requests[1].ApproveReview);
    }

    [Fact]
    public async Task Interactive_submit_approval_includes_fork_creation_consent()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submissions);
        harness.Interaction.EnqueueConfirm(true);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--submit"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            ForkConsentPolicy.AllowCreate,
            Assert.Single(submissions.Requests).Policy.ForkConsent);
    }

    [Fact]
    public async Task Editor_result_is_revalidated_as_raw_submit_before_apply()
    {
        var workflow = new FakeMutationWorkflow();
        var editor = new FakeEditorRunner();
        CliHarness harness = CreateHarness(workflow, editor: editor);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--edit"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(3, workflow.Requests.Count);
        var edited = Assert.IsType<SubmitOperationRequest>(workflow.Requests[1]);
        Assert.Contains(
            "edited: true",
            Encoding.UTF8.GetString(edited.Documents[0].Content.AsSpan()),
            StringComparison.Ordinal);
        Assert.Equal(WorkflowExecutionMode.Apply, workflow.Requests[2].ExecutionMode);
    }

    [Fact]
    public async Task Locale_editor_receives_only_target_locale_and_preserves_other_manifests()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => request is NewLocaleOperationRequest
                ? LocaleResult(request)
                : FakeMutationWorkflow.Result(request),
        };
        var editor = new FakeEditorRunner();
        CliHarness harness = CreateHarness(workflow, editor: editor);

        CliRunResult result = await harness.RunAsync(
            ["new-locale", "Example.App", "1.0", "de-DE", "--edit"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        ImmutableArray<RawManifestDocument> editorInput = Assert.Single(editor.Inputs);
        Assert.Single(editorInput);
        Assert.EndsWith(".locale.de-DE.yaml", editorInput[0].RepositoryPath, StringComparison.Ordinal);
        SubmitOperationRequest submit = Assert.IsType<SubmitOperationRequest>(workflow.Requests[1]);
        Assert.Equal(3, submit.Documents.Length);
        Assert.Contains(
            submit.Documents,
            document => document.RepositoryPath.EndsWith(
                ".installer.yaml",
                StringComparison.Ordinal)
                && Encoding.UTF8.GetString(document.Content.AsSpan()) == "installer: unchanged\n");
    }

    [Fact]
    public async Task Editor_cancellation_never_applies()
    {
        var workflow = new FakeMutationWorkflow();
        var editor = new FakeEditorRunner { Accept = false };
        CliHarness harness = CreateHarness(workflow, editor: editor);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--edit"]);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Single(workflow.Requests);
    }

    [Fact]
    public async Task Process_editor_uses_no_shell_and_keeps_manifest_path_one_argument()
    {
        var processes = new FakeEditorProcessRunner();
        var editor = new ProcessEditorRunner(
            name => name == "VISUAL" ? "\"C:\\Program Files\\Editor\\editor.exe\" --wait" : null,
            processes);
        var document = new RawManifestDocument(
            "manifests/e/Example/App/1.0/file;--execute.yaml",
            Encoding.UTF8.GetBytes("safe: true\n"));

        EditorResult result = await editor.EditAsync([document]);

        Assert.True(result.Accepted);
        Assert.Equal(@"C:\Program Files\Editor\editor.exe", processes.Executable);
        Assert.Equal("--wait", processes.Arguments[0]);
        Assert.Single(processes.Arguments.Skip(1));
        Assert.EndsWith(
            "file;--execute.yaml",
            processes.Arguments[1],
            StringComparison.Ordinal);
        Assert.Equal("safe: true\n", Encoding.UTF8.GetString(result.Documents[0].Content.AsSpan()));
    }

    [Fact]
    public async Task File_loader_infers_repository_path_from_external_raw_version_manifest()
    {
        string input = Directory.CreateTempSubdirectory("winmatsch-loader-input-").FullName;
        string output = Directory.CreateTempSubdirectory("winmatsch-loader-output-").FullName;
        try
        {
            string file = Path.Combine(input, "Example.App.yaml");
            await File.WriteAllTextAsync(
                file,
                "PackageIdentifier: Example.App\n"
                + "PackageVersion: 2.0.0\n"
                + "DefaultLocale: en-US\n"
                + "ManifestType: version\n"
                + "ManifestVersion: 1.9.0\n");
            var loader = new FileSystemRawManifestSetLoader();

            ImmutableArray<RawManifestDocument> documents =
                await loader.LoadAsync(input, output);

            Assert.Equal(
                "manifests/e/Example/App/2.0.0/Example.App.yaml",
                Assert.Single(documents).RepositoryPath);
        }
        finally
        {
            Directory.Delete(input, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_failure_blocks_apply()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => FakeMutationWorkflow.Result(
                request,
                WorkflowResultCode.ValidationFailed,
                new ValidationReport(
                [
                    new("VLD1", ValidationSeverity.Error, "blocked"),
                ])),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(["update", "Example.App", "1.0"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Single(workflow.Requests);
        Assert.Contains("VLD1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_remote_state_is_rendered_and_fails_without_retry()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FakeSubmissionWorkflow
        {
            Handler = request => Remote(
                request,
                GitHubLifecycleResultCode.HumanEscalationRequired,
                new RemoteMutationState
                {
                    BranchName = "winmatsch/submissions/example",
                    BranchCreated = true,
                    LastAttemptedOperation = RemoteOperationKind.CreateCommit,
                    RemoteOutcomeUncertain = true,
                }),
        };
        CliHarness harness = CreateHarness(workflow, submissions);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--submit", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Single(submissions.Requests);
        Assert.Contains("\"outcomeUncertain\":true", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "\"branch\":\"winmatsch/submissions/example\"",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_io_failure_is_an_operation_failure()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = _ => throw new IOException("disk unavailable"),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(["update", "Example.App", "1.0"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Local mutation failed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_failure_is_an_operation_failure()
    {
        var workflow = new FakeMutationWorkflow
        {
            Handler = _ => throw new DownloadHttpException(
                System.Net.HttpStatusCode.NotFound,
                "https://example.test/missing.exe"),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(["update", "Example.App", "1.0"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("Local mutation failed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_version_must_match_update_source_before_any_mutation()
    {
        var workflow = new FakeMutationWorkflow();
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--replace", "0.9", "--yes"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Empty(workflow.Requests);
        Assert.Contains("must match", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replacement_uses_one_authoritative_previous_version_locally_and_remotely()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(workflow, submissions);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--replace", "1.0", "--submit", "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.All(
            workflow.Requests.OfType<UpdateOperationRequest>(),
            request =>
            {
                Assert.True(request.ReplacePreviousVersion);
                Assert.Equal("1.0", request.PreviousVersion.Value);
            });
        GitHubSubmissionRequest remote = Assert.Single(submissions.Requests);
        Assert.True(remote.Policy.ReplacePreviousVersion);
        Assert.Equal("1.0", remote.Policy.PreviousVersion?.Value);
    }

    [Fact]
    public async Task Remote_timeout_result_without_cancelled_token_is_operation_failure()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FakeSubmissionWorkflow
        {
            Handler = request => Remote(
                request,
                GitHubLifecycleResultCode.Cancelled,
                new RemoteMutationState
                {
                    BranchName = "winmatsch/submissions/example",
                    BranchCreated = true,
                }),
        };
        CliHarness harness = CreateHarness(workflow, submissions);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--submit", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains(
            "\"branch\":\"winmatsch/submissions/example\"",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_failure_after_created_pr_is_diagnostic_only()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FakeSubmissionWorkflow();
        var launcher = new FakeUrlLauncher
        {
            Failure = new System.ComponentModel.Win32Exception("no browser"),
        };
        var harness = new CliHarness();
        harness.Modules.Add(new MutationCommandModule(
            workflow,
            submissions,
            new FakeEditorRunner(),
            new FakeManifestLoader(),
            launcher));

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--submit", "--yes", "--open-pr"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("pull request URL", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("browser could not be opened", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invocation_factory_observes_token_precedence_and_concurrency()
    {
        var workflow = new FakeMutationWorkflow();
        var factory = new CapturingMutationWorkflowFactory(workflow);
        var harness = new CliHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "environment-token";
        harness.Modules.Add(new MutationCommandModule(
            factory,
            editor: new FakeEditorRunner(),
            manifestLoader: new FakeManifestLoader(),
            urlLauncher: new FakeUrlLauncher()));

        CliRunResult result = await harness.RunAsync(
        [
            "update",
            "Example.App",
            "1.0",
            "--token",
            "command-token",
            "--concurrent-downloads",
            "7",
            "--dry-run",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(TokenSource.ExplicitOption, factory.TokenSource);
        Assert.Equal(7, factory.ConcurrentDownloads);
    }

    [Fact]
    public async Task Submission_factory_failure_occurs_before_local_apply()
    {
        var workflow = new FakeMutationWorkflow();
        var submissions = new FailingSubmissionWorkflowFactory();
        var harness = new CliHarness();
        harness.Modules.Add(new MutationCommandModule(
            new FixedMutationWorkflowFactory(workflow),
            submissions,
            new FakeEditorRunner(),
            new FakeManifestLoader(),
            new FakeUrlLauncher()));

        CliRunResult result = await harness.RunAsync(
            ["remove", "Example.App", "1.0", "--submit", "--yes"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Single(workflow.Requests);
        Assert.Equal(WorkflowExecutionMode.Plan, workflow.Requests[0].ExecutionMode);
    }

    [Fact]
    public async Task Missing_override_pack_is_a_usage_error_not_unexpected()
    {
        CliHarness harness = CreateHarness(new FakeMutationWorkflow());

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--override-pack", "missing-pack.yaml"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Override-pack input failed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_editor_configuration_returns_missing_input_not_cancellation()
    {
        var editor = new FakeEditorRunner
        {
            Accept = false,
            RejectionCode = EditorResultCode.MissingConfiguration,
        };
        CliHarness harness = CreateHarness(new FakeMutationWorkflow(), editor: editor);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--edit"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
    }

    [Fact]
    public async Task Url_overrides_rule_modes_and_locale_metadata_bind_without_guessing()
    {
        var workflow = new FakeMutationWorkflow();
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(
        [
            "new",
            "Example.App",
            "--version",
            "2.0",
            "--url",
            "https://example.test/app.exe|x64|machine|2.0.0",
            "--default-rule-mode",
            "log-only",
            "--rule-mode",
            "META-1=disabled",
            "--locale",
            "en-GB",
            "--publisher",
            "Example",
            "--package-name",
            "App",
            "--license",
            "MIT",
            "--short-description",
            "Example app",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var request = Assert.IsType<NewOperationRequest>(workflow.Requests[0]);
        Assert.Equal("en-GB", request.Locale.PackageLocale.Value);
        Assert.Equal(RuleMode.LogOnly, request.RuleRuntime.DefaultMode);
        Assert.Equal(RuleMode.Disabled, request.RuleRuntime.CommandOverrides["META-1"]);
        Assert.Equal(Architecture.X64, Assert.Single(request.UrlOverrides).Architecture);
        Assert.Equal(Scope.Machine, request.UrlOverrides[0].Scope);
        Assert.Equal("2.0.0", request.UrlOverrides[0].DisplayVersion);
    }

    [Fact]
    public async Task Output_redacts_tokens_in_preview_and_diagnostics()
    {
        const string token = "github_pat_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => FakeMutationWorkflow.Result(
                request,
                validation: new ValidationReport(
                [
                    new(
                        "SECRET",
                        ValidationSeverity.Warning,
                        $"token={token}; password: hunter2; client_secret: oauth-secret; "
                        + "access_token=access-secret; refresh-token: refresh-secret; "
                        + "Authorization: Bearer bearer-secret"),
                ]),
                content: $"value: {token}\n"
                    + "InstallerUrl: https://user:password@example.test/app.exe?sig=secret"
                    + "&X-Amz-Signature=signature\n"),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(
            ["update", "Example.App", "1.0", "--dry-run", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(token, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("user:password@", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_composes_real_local_workflow_for_golden_new_apply()
    {
        var transaction = new GoldenTransaction();
        var engine = new LocalWorkflowEngine(
            new EmptySnapshotSource(),
            new GoldenRuleRunner(),
            new GoldenPreflight(),
            transaction,
            releases: new GoldenReleaseSource());
        var harness = new CliHarness();
        harness.Modules.Add(MutationCommandModuleFactory.Create(
            engine,
            editor: new FakeEditorRunner(),
            manifestLoader: new FakeManifestLoader(),
            urlLauncher: new FakeUrlLauncher()));

        CliRunResult result = await harness.RunAsync(
        [
            "new",
            "Example.App",
            "--version",
            "2.0.0",
            "--publisher",
            "Example",
            "--package-name",
            "App",
            "--license",
            "MIT",
            "--short-description",
            "Example application",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(1, transaction.Calls);
        Assert.Equal(3, transaction.Changes.Length);
        Assert.Contains(
            "manifests/e/Example/App/2.0.0/Example.App.installer.yaml",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    private static CliHarness CreateHarness(
        FakeMutationWorkflow workflow,
        FakeSubmissionWorkflow? submissions = null,
        FakeEditorRunner? editor = null)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new MutationCommandModule(
            workflow,
            submissions,
            editor ?? new FakeEditorRunner(),
            new FakeManifestLoader(),
            new FakeUrlLauncher()));
        return harness;
    }

    private static WorkflowOperationResult Question(WorkflowOperationRequest request)
        => FakeMutationWorkflow.Result(
            request,
            WorkflowResultCode.QuestionsRequired,
            questions:
            [
                new("METADATA_PUBLISHER", "Provide required locale metadata 'Publisher'.", []),
            ]);

    private static WorkflowOperationResult Review(WorkflowOperationRequest request)
        => FakeMutationWorkflow.Result(
            request,
            WorkflowResultCode.ReviewRequired,
            reviews:
            [
                new("manifest.yaml", "Publisher", "bot", "human", "bot"),
            ]);

    private static WorkflowOperationResult LocaleResult(WorkflowOperationRequest request)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion("1.0");
        string directory = ManifestPaths.GetVersionDirectory(identifier, version);
        ImmutableArray<RawManifestDocument> documents =
        [
            new($"{directory}/Example.App.installer.yaml", Encoding.UTF8.GetBytes("installer: unchanged\n")),
            new($"{directory}/Example.App.yaml", Encoding.UTF8.GetBytes("version: unchanged\n")),
            new($"{directory}/Example.App.locale.de-DE.yaml", Encoding.UTF8.GetBytes("locale: original\n")),
        ];
        RawManifestDocument locale = documents[2];
        var change = new WorkflowFileChange(
            PlannedChangeKind.Add,
            locale.RepositoryPath,
            locale.Content.AsSpan());
        var plan = new LocalOperationPlan
        {
            Operation = "new-locale",
            PackageIdentifier = identifier,
            PackageVersion = version,
            OutputDirectory = request.OutputDirectory,
            FileChanges = [change],
            BeforeDocuments = [],
            AfterDocuments = documents,
            Validation = new(),
            Preflight = new()
            {
                BeforeDocuments = [],
                AfterDocuments = documents,
                Changes = [change],
            },
            Rules = RuleRunSummary.Empty,
        };
        return new()
        {
            Code = WorkflowResultCode.Succeeded,
            Plan = plan,
        };
    }

    private static GitHubLifecycleResult Remote(
        GitHubSubmissionRequest request,
        GitHubLifecycleResultCode code,
        RemoteMutationState state)
    {
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);
        return new()
        {
            Code = code,
            Plan = plan,
            RemoteState = state,
            Diagnostics = [new("REMOTE", "Manual recovery required.")],
        };
    }
}

internal sealed class FakeMutationWorkflow : IMutationWorkflow
{
    public List<WorkflowOperationRequest> Requests { get; } = [];

    public Func<WorkflowOperationRequest, WorkflowOperationResult>? Handler { get; init; }

    public Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Handler?.Invoke(request) ?? Result(request));
    }

    public static WorkflowOperationResult Result(
        WorkflowOperationRequest request,
        WorkflowResultCode code = WorkflowResultCode.Succeeded,
        ValidationReport? validation = null,
        ImmutableArray<WorkflowQuestion> questions = default,
        ImmutableArray<HumanCorrectionReview> reviews = default,
        string content = "PackageIdentifier: Example.App\nPackageVersion: 1.0\n")
    {
        (PackageIdentifier identifier, PackageVersion version) = Identity(request);
        var document = new RawManifestDocument(
            $"manifests/e/Example/App/{version.Value}/Example.App.yaml",
            Encoding.UTF8.GetBytes(content));
        var change = new WorkflowFileChange(
            PlannedChangeKind.Add,
            document.RepositoryPath,
            document.Content.AsSpan());
        var report = validation ?? new ValidationReport();
        var plan = new LocalOperationPlan
        {
            Operation = request.GetType().Name.Replace("OperationRequest", "", StringComparison.Ordinal),
            PackageIdentifier = identifier,
            PackageVersion = version,
            OutputDirectory = request.OutputDirectory,
            FileChanges = [change],
            BeforeDocuments = [],
            AfterDocuments = [document],
            Validation = report,
            Preflight = new()
            {
                BeforeDocuments = [],
                AfterDocuments = [document],
                Changes = [change],
            },
            Rules = new([], [], [], reviews.IsDefault ? [] : reviews, []),
            Questions = questions.IsDefault ? [] : questions,
            ReviewApproved = request.ApproveReview,
        };
        return new()
        {
            Code = code,
            Plan = plan,
            Applied = request.ExecutionMode == WorkflowExecutionMode.Apply
                && code == WorkflowResultCode.Succeeded,
        };
    }

    private static (PackageIdentifier Identifier, PackageVersion Version) Identity(
        WorkflowOperationRequest request)
        => request switch
        {
            NewOperationRequest value => (
                value.PackageIdentifier,
                new PackageVersion(value.PackageVersion ?? "1.0")),
            UpdateOperationRequest value => (
                value.PackageIdentifier,
                new PackageVersion(value.PackageVersion ?? value.PreviousVersion.Value)),
            RemoveOperationRequest value => (value.PackageIdentifier, value.PackageVersion),
            NewLocaleOperationRequest value => (value.PackageIdentifier, value.PackageVersion),
            UpdateLocaleOperationRequest value => (value.PackageIdentifier, value.PackageVersion),
            SubmitOperationRequest => (
                new PackageIdentifier("Example.App"),
                new PackageVersion("1.0")),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
}

internal sealed class FakeSubmissionWorkflow : ISubmissionWorkflow
{
    public List<GitHubSubmissionRequest> Requests { get; } = [];

    public Func<GitHubSubmissionRequest, GitHubLifecycleResult>? Handler { get; init; }

    public Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Handler is not null)
        {
            return Task.FromResult(Handler(request));
        }

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(request);
        return Task.FromResult(new GitHubLifecycleResult
        {
            Code = request.ExecutionMode == WorkflowExecutionMode.Plan
                ? GitHubLifecycleResultCode.Planned
                : GitHubLifecycleResultCode.Succeeded,
            Plan = plan,
            RemoteState = request.ExecutionMode == WorkflowExecutionMode.Plan
                ? new()
                : new()
                {
                    PullRequestCreated = true,
                    PullRequestNumber = 42,
                    PullRequestUri = new Uri("https://example.test/pull/42"),
                },
        });
    }
}

internal sealed class FakeManifestLoader : IRawManifestSetLoader
{
    public Task<ImmutableArray<RawManifestDocument>> LoadAsync(
        string path,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ImmutableArray<RawManifestDocument>>(
        [
            new(
                "manifests/e/Example/App/1.0/Example.App.yaml",
                Encoding.UTF8.GetBytes("PackageIdentifier: Example.App\n")),
        ]);
}

internal sealed class FakeEditorRunner : IEditorRunner
{
    public bool Accept { get; init; } = true;

    public EditorResultCode RejectionCode { get; init; } = EditorResultCode.Cancelled;

    public List<ImmutableArray<RawManifestDocument>> Inputs { get; } = [];

    public Task<EditorResult> EditAsync(
        ImmutableArray<RawManifestDocument> documents,
        CancellationToken cancellationToken = default)
    {
        Inputs.Add(documents);
        if (!Accept)
        {
            return Task.FromResult(new EditorResult(
                RejectionCode,
                documents,
                "cancelled"));
        }

        return Task.FromResult(new EditorResult(
            EditorResultCode.Accepted,
            [
                new(
                    documents[0].RepositoryPath,
                    Encoding.UTF8.GetBytes("edited: true\n")),
            ]));
    }
}

internal sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<Uri> Opened { get; } = [];

    public Exception? Failure { get; init; }

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Opened.Add(uri);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingMutationWorkflowFactory(IMutationWorkflow workflow)
    : IMutationWorkflowFactory
{
    public TokenSource? TokenSource { get; private set; }

    public int? ConcurrentDownloads { get; private set; }

    public async Task<IMutationWorkflow> CreateAsync(
        WinMatsch.Cli.Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ResolvedToken token = await context.Tokens.RequireAsync(cancellationToken);
        TokenSource = token.Source;
        ConcurrentDownloads = context.Configuration.ConcurrentDownloads;
        return workflow;
    }
}

internal sealed class FailingSubmissionWorkflowFactory : ISubmissionWorkflowFactory
{
    public Task<ISubmissionWorkflow> CreateAsync(
        WinMatsch.Cli.Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
        => Task.FromException<ISubmissionWorkflow>(
            new MissingInputException("A GitHub token is required."));
}

internal sealed class FakeEditorProcessRunner : IEditorProcessRunner
{
    public string? Executable { get; private set; }

    public IReadOnlyList<string> Arguments { get; private set; } = [];

    public Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        Executable = executable;
        Arguments = arguments;
        return Task.FromResult(0);
    }
}

internal sealed class EmptySnapshotSource : IManifestSnapshotSource
{
    public Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
        => Task.FromResult<PackageSnapshot?>(null);

    public Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<PackageSnapshot>.Empty);
}

internal sealed class GoldenReleaseSource : IWorkflowReleaseSource
{
    public Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var identity = new DownloadContentIdentity(
            new Sha256Hash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
            42);
        return Task.FromResult<ImmutableArray<DiscoveredAsset>>(
        [
            new()
                {
                    ReleaseId = 1,
                    ReleaseTag = "v2.0.0",
                    ReleaseName = "2.0.0",
                    ReleaseUri = new Uri("https://example.test/releases/2.0.0"),
                    IsPrerelease = false,
                    AssetId = 2,
                    AssetName = "app-x64.exe",
                    DownloadUri = new Uri("https://example.test/app-x64.exe"),
                    DeclaredContentType = "application/octet-stream",
                    DeclaredSize = 42,
                    AssetCreatedAt = DateTimeOffset.UnixEpoch,
                    Content = new(
                        identity,
                        "https://example.test/app-x64.exe",
                        "https://example.test/app-x64.exe",
                        "application/octet-stream",
                        DateTimeOffset.UnixEpoch),
                    Analysis = new AssetAnalysisEvidence
                    {
                        Format = DetectedInstallerFormat.GenericInstallerExe,
                        AnalyzedContentIdentity = identity,
                        AnalyzedUrl = "https://example.test/app-x64.exe",
                        ProductVersion = "2.0.0",
                        IsProductVersionTrustworthy = true,
                        InstallerShapes =
                        [
                            new()
                            {
                                Architecture = Architecture.X64,
                                InstallerType = InstallerType.Exe,
                            },
                        ],
                    },
                },
            ]);
    }
}

internal sealed class GoldenRuleRunner : IWorkflowRuleRunner
{
    public WorkflowRuleResult Run(WorkflowRuleRequest request)
        => new(request.Manifests, RuleRunSummary.Empty);
}

internal sealed class GoldenPreflight : IWorkflowPreflight
{
    public Task<ValidationReport> ValidateAsync(
        WorkflowPreflightRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(new ValidationReport());

    public async Task<ValidationReport> ExecuteAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        await boundary(cancellationToken);
        return new ValidationReport();
    }
}

internal sealed class GoldenTransaction : IWorkflowFileTransaction
{
    public int Calls { get; private set; }

    public ImmutableArray<WorkflowFileChange> Changes { get; private set; } = [];

    public Task ApplyAsync(
        string outputDirectory,
        string operationLockKey,
        ImmutableArray<WorkflowFileChange> changes,
        CancellationToken cancellationToken)
    {
        Calls++;
        Changes = changes;
        return Task.CompletedTask;
    }
}
