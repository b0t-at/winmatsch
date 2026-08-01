using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;
using YamlDotNet.Core;

namespace WinMatsch.Cli.Commands.Mutations;

public static class MutationCommandModuleFactory
{
    public static ICommandModule Create(
        LocalWorkflowEngine localWorkflow,
        GitHubLifecycleWorkflow? submissionWorkflow = null,
        IEditorRunner? editor = null,
        IRawManifestSetLoader? manifestLoader = null,
        IUrlLauncher? urlLauncher = null)
        => new MutationCommandModule(
            new LocalMutationWorkflow(localWorkflow),
            submissionWorkflow is null ? null : new LifecycleSubmissionWorkflow(submissionWorkflow),
            editor,
            manifestLoader,
            urlLauncher);

    public static ICommandModule Create(
        IMutationWorkflowFactory localWorkflows,
        ISubmissionWorkflowFactory? submissionWorkflows = null,
        IEditorRunner? editor = null,
        IRawManifestSetLoader? manifestLoader = null,
        IUrlLauncher? urlLauncher = null)
        => new MutationCommandModule(
            localWorkflows,
            submissionWorkflows,
            editor,
            manifestLoader,
            urlLauncher);
}

public sealed class MutationCommandModule : ICommandModule
{
    private readonly IMutationWorkflowFactory _workflowFactory;
    private readonly ISubmissionWorkflowFactory? _submissionFactory;
    private readonly IEditorRunner _editor;
    private readonly IRawManifestSetLoader _manifestLoader;
    private readonly IUrlLauncher _urlLauncher;

    public MutationCommandModule(
        IMutationWorkflow workflow,
        ISubmissionWorkflow? submissions = null,
        IEditorRunner? editor = null,
        IRawManifestSetLoader? manifestLoader = null,
        IUrlLauncher? urlLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflowFactory = new FixedMutationWorkflowFactory(workflow);
        _submissionFactory = submissions is null
            ? null
            : new FixedSubmissionWorkflowFactory(submissions);
        _editor = editor ?? new ProcessEditorRunner();
        _manifestLoader = manifestLoader ?? new FileSystemRawManifestSetLoader();
        _urlLauncher = urlLauncher ?? new ProcessUrlLauncher();
    }

    public MutationCommandModule(
        IMutationWorkflowFactory workflowFactory,
        ISubmissionWorkflowFactory? submissionFactory = null,
        IEditorRunner? editor = null,
        IRawManifestSetLoader? manifestLoader = null,
        IUrlLauncher? urlLauncher = null)
    {
        _workflowFactory = workflowFactory
            ?? throw new ArgumentNullException(nameof(workflowFactory));
        _submissionFactory = submissionFactory;
        _editor = editor ?? new ProcessEditorRunner();
        _manifestLoader = manifestLoader ?? new FileSystemRawManifestSetLoader();
        _urlLauncher = urlLauncher ?? new ProcessUrlLauncher();
    }

    public string Name => "mutations";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RegisterNew(registry);
        RegisterUpdate(registry);
        RegisterRemove(registry);
        RegisterSubmit(registry);
        RegisterLocale(registry, update: false);
        RegisterLocale(registry, update: true);
    }

    private void RegisterNew(ICommandRegistry registry)
    {
        var package = PackageArgument();
        var options = new MutationOptions(
            includeRelease: true,
            includeMetadata: true,
            includeReplace: false);
        var command = new Command("new", "Create and validate a new package version.")
        {
            Arguments = { package },
        };
        options.AddTo(command);
        registry.AddCommand(command);
        registry.SetHandler(command, context => ExecuteAsync(
            context,
            options,
            GitHubManifestOperation.New,
            () => new NewOperationRequest
            {
                OutputDirectory = GetOutputDirectory(context),
                PackageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package)),
                PackageVersion = context.ParseResult.GetValue(options.Version),
                Release = ParseRelease(context.ParseResult, options),
                UrlOverrides = ParseUrlOverrides(context.ParseResult, options),
                Locale = BindLocale(context.ParseResult, options),
                AllowSharedContentAcrossUrls =
                    context.ParseResult.GetValue(options.AllowSharedContent),
            }));
    }

    private void RegisterUpdate(ICommandRegistry registry)
    {
        var package = PackageArgument();
        var previousVersion = VersionArgument();
        var options = new MutationOptions(
            includeRelease: true,
            includeMetadata: false,
            includeReplace: true);
        var command = new Command("update", "Update an existing exact package version.")
        {
            Arguments = { package, previousVersion },
        };
        options.AddTo(command);
        registry.AddCommand(command);
        registry.SetHandler(command, context => ExecuteAsync(
            context,
            options,
            GitHubManifestOperation.Update,
            () => new UpdateOperationRequest
            {
                OutputDirectory = GetOutputDirectory(context),
                PackageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package)),
                PreviousVersion = ParseVersion(context.ParseResult.GetValue(previousVersion)),
                PackageVersion = context.ParseResult.GetValue(options.Version),
                Release = ParseRelease(context.ParseResult, options),
                UrlOverrides = ParseUrlOverrides(context.ParseResult, options),
                ReplacePreviousVersion = context.ParseResult.GetResult(options.Replace) is not null,
                AllowStructuralRewrite =
                    context.ParseResult.GetValue(options.AllowStructuralRewrite),
                AllowStableUrlContentChange =
                    context.ParseResult.GetValue(options.AllowStableUrlChange),
                AllowSharedContentAcrossUrls =
                    context.ParseResult.GetValue(options.AllowSharedContent),
            }));
    }

    private void RegisterRemove(ICommandRegistry registry)
    {
        var package = PackageArgument();
        var version = VersionArgument();
        var options = new MutationOptions(
            includeRelease: false,
            includeMetadata: false,
            includeReplace: false);
        var command = new Command("remove", "Remove one exact package version.")
        {
            Arguments = { package, version },
        };
        options.AddTo(command);
        registry.AddCommand(command);
        registry.SetHandler(command, context => ExecuteAsync(
            context,
            options,
            GitHubManifestOperation.Remove,
            () => new RemoveOperationRequest
            {
                OutputDirectory = GetOutputDirectory(context),
                PackageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package)),
                PackageVersion = ParseVersion(context.ParseResult.GetValue(version)),
            }));
    }

    private void RegisterSubmit(ICommandRegistry registry)
    {
        var path = new Argument<string>("path")
        {
            Description = "Directory containing one complete raw multi-file manifest set.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var options = new MutationOptions(
            includeRelease: false,
            includeMetadata: false,
            includeReplace: false);
        var normalize = new Option<bool>("--normalize")
        {
            Description = "Explicitly normalize the raw manifest set before submission.",
        };
        var command = new Command("submit", "Validate and optionally submit existing raw manifests.")
        {
            Arguments = { path },
            Options = { normalize },
        };
        options.AddTo(command);
        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            string input = Require(context.ParseResult.GetValue(path), "manifest path", "the path argument");
            ImmutableArray<RawManifestDocument> documents;
            try
            {
                documents = await _manifestLoader.LoadAsync(
                    input,
                    GetOutputDirectory(context),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or FormatException
                    or ArgumentException
                    or YamlException)
            {
                throw new CliOperationException(
                    $"Manifest input failed: {MutationRedact(exception.Message)}",
                    exception);
            }

            return await ExecuteAsync(
                context,
                options,
                GitHubManifestOperation.Add,
                () => new SubmitOperationRequest
                {
                    OutputDirectory = GetOutputDirectory(context),
                    Documents = documents,
                    Normalize = context.ParseResult.GetValue(normalize),
                }).ConfigureAwait(false);
        });
    }

    private void RegisterLocale(ICommandRegistry registry, bool update)
    {
        var package = PackageArgument();
        var version = VersionArgument();
        var locale = new Argument<string>("locale")
        {
            Description = "Exact BCP-47 locale casing.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var options = new MutationOptions(
            includeRelease: false,
            includeMetadata: true,
            includeReplace: false);
        string name = update ? "update-locale" : "new-locale";
        var command = new Command(
            name,
            update ? "Update one exact locale manifest." : "Create one exact locale manifest.")
        {
            Arguments = { package, version, locale },
        };
        options.AddTo(command);
        registry.AddCommand(command);
        registry.SetHandler(command, context =>
        {
            PackageLocaleMetadata metadata = BindLocale(
                context.ParseResult,
                options,
                Require(
                    context.ParseResult.GetValue(locale),
                    "locale",
                    "the locale argument"));
            return ExecuteAsync(
                context,
                options,
                GitHubManifestOperation.Update,
                () => update
                    ? new UpdateLocaleOperationRequest
                    {
                        OutputDirectory = GetOutputDirectory(context),
                        PackageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package)),
                        PackageVersion = ParseVersion(context.ParseResult.GetValue(version)),
                        Locale = metadata,
                    }
                    : new NewLocaleOperationRequest
                    {
                        OutputDirectory = GetOutputDirectory(context),
                        PackageIdentifier = ParseIdentifier(context.ParseResult.GetValue(package)),
                        PackageVersion = ParseVersion(context.ParseResult.GetValue(version)),
                        Locale = metadata,
                    });
        });
    }

    private async Task<int> ExecuteAsync(
        CommandContext context,
        MutationOptions options,
        GitHubManifestOperation operation,
        Func<WorkflowOperationRequest> requestFactory)
    {
        WorkflowOperationRequest request;
        try
        {
            request = ApplyCommon(context, options, requestFactory());
            ValidateReplace(context.ParseResult, options, request);
            bool edit = context.ParseResult.GetValue(options.Edit);
            if (edit && context.ParseResult.GetResult(options.Replace) is not null)
            {
                throw new CliUsageException("--edit cannot be combined with --replace.");
            }

            int editAttempts = context.ParseResult.GetValue(options.EditAttempts) ?? 3;
            if (editAttempts is < 1 or > 10)
            {
                throw new CliUsageException("--edit-attempts must be between 1 and 10.");
            }

            if (!edit && context.ParseResult.GetResult(options.EditAttempts) is not null)
            {
                throw new CliUsageException("--edit-attempts requires --edit.");
            }

            if (request is RemoveOperationRequest
                && edit)
            {
                throw new CliUsageException("--edit is not supported by the remove command.");
            }

            if (context.ParseResult.GetValue(options.OpenPullRequest)
                && !context.ParseResult.GetValue(options.Submit))
            {
                throw new CliUsageException("--open-pr requires --submit.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new CliUsageException(MutationRedact(exception.Message), exception);
        }

        IMutationWorkflow workflow = await CreateMutationWorkflowAsync(context).ConfigureAwait(false);
        using IDisposable? workflowLease = workflow as IDisposable;
        string? approvedReviewFingerprint = null;
        WorkflowOperationResult local = await RunLocalAsync(workflow, request, context)
            .ConfigureAwait(false);
        (local, request) = await ResolveQuestionsAsync(workflow, local, request, context)
            .ConfigureAwait(false);
        if (local.Plan.RequiresReview)
        {
            string displayedReviewFingerprint = ReviewFingerprint(local.Plan);
            ReportApprovalContext(context, local.Plan, "Review approval required");
            if (!context.ParseResult.GetValue(options.ApproveReviews))
            {
                if (!context.Interaction.CanPrompt)
                {
                    MutationOutput.Write(context, local, remote: null);
                    throw new MissingInputException(
                        "Review approval is required; inspect the emitted plan and pass "
                        + "--approve-reviews to approve only the listed reviews.");
                }

                bool approved = await context.Interaction.ConfirmAsync(
                    "Approve the listed human-correction reviews?",
                    defaultValue: false,
                    context.CancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    MutationOutput.Write(context, local, remote: null);
                    return ExitCodes.OperationFailed;
                }
            }

            request = ReviewApproval.Bind(request, local.Plan);
            local = await RunLocalAsync(workflow, request, context).ConfigureAwait(false);
            if (!string.Equals(
                    displayedReviewFingerprint,
                    ReviewFingerprint(local.Plan),
                    StringComparison.Ordinal))
            {
                MutationOutput.Write(context, local, remote: null);
                context.Output.WriteError(
                    "The human-correction reviews changed after approval; nothing was applied. "
                    + "Review the new values and rerun.");
                return ExitCodes.OperationFailed;
            }

            approvedReviewFingerprint = displayedReviewFingerprint;
        }

        WorkflowReleaseProvenance? originalReleaseProvenance = local.Plan.Release;
        WorkflowReleaseProvenance? applicableReleaseProvenance = originalReleaseProvenance;
        ImmutableHashSet<string> releaseInstallerUrls = InstallerUrls(local.Plan);
        bool learningOnly = local.Code == WorkflowResultCode.NoChanges
            && local.Plan.LearnedOverride is not null
            && local.Plan.ReviewApproved;
        bool submitRequested = context.ParseResult.GetValue(options.Submit) && !learningOnly;
        if (local.Code == WorkflowResultCode.NoChanges
            && submitRequested
            && !context.IsDryRun)
        {
            if (!context.ParseResult.GetValue(options.Yes))
            {
                ReportApprovalContext(
                    context,
                    local.Plan,
                    "Pending remote submission approval required");
                EnsurePrompting(
                    context,
                    "Resuming a pending remote submission requires --yes in non-interactive mode.");
                bool approved = await context.Interaction.ConfirmAsync(
                    "Resume the previously journaled GitHub submission?",
                    defaultValue: false,
                    context.CancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    MutationOutput.Write(context, local, remote: null);
                    return ExitCodes.OperationFailed;
                }
            }

            if (_submissionFactory is null)
            {
                throw new CliOperationException(
                    "Remote submission is unavailable because no submission workflow was composed.");
            }

            ISubmissionWorkflow recoveryWorkflow =
                await CreateSubmissionWorkflowAsync(context).ConfigureAwait(false);
            if (recoveryWorkflow is not IJournaledSubmissionWorkflow journaledRecovery)
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
            }

            GitHubLifecycleResult? recovered = await RunResumeAsync(
                journaledRecovery,
                local.Plan,
                context).ConfigureAwait(false);
            MutationOutput.Write(context, local, recovered);
            return recovered?.Applied == true
                ? ExitCodes.Success
                : ExitCodes.OperationFailed;
        }

        if (local.Code != WorkflowResultCode.Succeeded && !learningOnly)
        {
            MutationOutput.Write(context, local, remote: null);
            return ExitCodes.OperationFailed;
        }

        if (context.ParseResult.GetValue(options.Edit) && !learningOnly)
        {
            if (context.Configuration.OutputFormat == OutputFormat.Json || !context.Interaction.CanPrompt)
            {
                throw new MissingInputException(
                    "Manifest editing requires an interactive text terminal; JSON and non-interactive modes cannot edit.");
            }

            ImmutableArray<RawManifestDocument> initialEditableDocuments =
                operation is GitHubManifestOperation.Add or GitHubManifestOperation.Update
                && request is NewLocaleOperationRequest or UpdateLocaleOperationRequest
                    ?
                    [
                        .. local.Plan.AfterDocuments.Where(document =>
                            local.Plan.FileChanges.Any(change =>
                                string.Equals(
                                    change.RepositoryPath,
                                    document.RepositoryPath,
                                    StringComparison.Ordinal))),
                    ]
                    : local.Plan.AfterDocuments;
            var editablePaths = initialEditableDocuments
                .Select(static document => document.RepositoryPath)
                .ToImmutableHashSet(StringComparer.Ordinal);
            ImmutableArray<RawManifestDocument> completeDocuments = local.Plan.AfterDocuments;
            int maximumAttempts = context.ParseResult.GetValue(options.EditAttempts) ?? 3;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                ImmutableArray<RawManifestDocument> editableDocuments =
                [
                    .. completeDocuments.Where(document =>
                        editablePaths.Contains(document.RepositoryPath)),
                ];
                EditorResult edited = await RunEditorAsync(editableDocuments, context)
                    .ConfigureAwait(false);
                if (!edited.Accepted)
                {
                    throw edited.Code switch
                    {
                        EditorResultCode.Cancelled => new CliOperationException(
                            edited.ErrorMessage ?? "Manifest editing was cancelled; nothing was applied."),
                        EditorResultCode.MissingConfiguration => new MissingInputException(
                            edited.ErrorMessage ?? "An editor must be configured."),
                        EditorResultCode.InvalidConfiguration => new FormatException(
                            edited.ErrorMessage ?? "The editor configuration is invalid."),
                        EditorResultCode.Failed => new CliOperationException(
                            edited.ErrorMessage ?? "Manifest editing failed."),
                        _ => new CliOperationException("Manifest editing failed."),
                    };
                }

                ImmutableArray<RawManifestDocument> editedDocuments = MergeEditedDocuments(
                    completeDocuments,
                    editableDocuments,
                    edited.Documents);
                request = ApplyCommon(context, options, new SubmitOperationRequest
                {
                    OutputDirectory = request.OutputDirectory,
                    Documents = editedDocuments,
                    ApproveReview = request.ApproveReview,
                    ReleaseProvenance = applicableReleaseProvenance,
                });
                local = await RunLocalAsync(workflow, request, context).ConfigureAwait(false);
                if (approvedReviewFingerprint is not null
                    && !string.Equals(
                        approvedReviewFingerprint,
                        ReviewFingerprint(local.Plan),
                        StringComparison.Ordinal))
                {
                    MutationOutput.Write(context, local, remote: null);
                    context.Output.WriteError(
                        "Editing changed the approved human-correction reviews; nothing was "
                        + "applied. Review the new values and rerun.");
                    return ExitCodes.OperationFailed;
                }

                applicableReleaseProvenance = originalReleaseProvenance is not null
                    && releaseInstallerUrls.Count > 0
                    && releaseInstallerUrls.SetEquals(InstallerUrls(local.Plan))
                        ? originalReleaseProvenance
                        : null;
                if (applicableReleaseProvenance is not null)
                {
                    local = local with
                    {
                        Plan = local.Plan with { Release = applicableReleaseProvenance },
                    };
                }
                else
                {
                    request = request is SubmitOperationRequest submitRequest
                        ? submitRequest with { ReleaseProvenance = null }
                        : request;
                    local = local with { Plan = local.Plan with { Release = null } };
                }

                if (local.Code == WorkflowResultCode.Succeeded)
                {
                    break;
                }

                completeDocuments = local.Plan.AfterDocuments.IsEmpty
                    ? editedDocuments
                    : local.Plan.AfterDocuments;
                ReportEditFailure(context, local, attempt, maximumAttempts);
                if (attempt == maximumAttempts
                    || !await context.Interaction.ConfirmAsync(
                        "The edited manifests are still invalid. Edit again?",
                        defaultValue: false,
                        context.CancellationToken).ConfigureAwait(false))
                {
                    MutationOutput.Write(context, local, remote: null);
                    return ExitCodes.OperationFailed;
                }
            }
        }

        bool destructive = operation == GitHubManifestOperation.Remove
            || context.ParseResult.GetResult(options.Replace) is not null;
        if (!context.IsDryRun && destructive && !context.ParseResult.GetValue(options.Yes))
        {
            ReportApprovalContext(context, local.Plan, "Destructive operation approval required");
            EnsurePrompting(
                context,
                "Destructive removal or replacement requires --yes in non-interactive mode.");
            bool approved = await context.Interaction.ConfirmAsync(
                "Apply the destructive removal or replacement shown above?",
                defaultValue: false,
                context.CancellationToken).ConfigureAwait(false);
            if (!approved)
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
            }
        }

        GitHubLifecycleResult? remote = null;
        bool submit = submitRequested;
        bool submissionConsent = context.ParseResult.GetValue(options.Yes);
        ISubmissionWorkflow? submission = null;
        GitHubSubmissionRequest? submissionRequest = null;
        SubmissionJournalHandle? submissionHandle = null;
        if (submit)
        {
            if (_submissionFactory is null)
            {
                throw new CliOperationException(
                    "Remote submission is unavailable because no submission workflow was composed.");
            }

            if (!context.IsDryRun && !submissionConsent)
            {
                ReportApprovalContext(context, local.Plan, "Remote submission approval required");
                EnsurePrompting(
                    context,
                    "Remote submission requires --yes in non-interactive mode.");
                bool approved = await context.Interaction.ConfirmAsync(
                    "Submit the validated manifest changes to GitHub, creating a fork if needed?",
                    defaultValue: false,
                    context.CancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    MutationOutput.Write(context, local, remote: null);
                    return ExitCodes.OperationFailed;
                }

                submissionConsent = true;
            }

            submission = await CreateSubmissionWorkflowAsync(context).ConfigureAwait(false);
            submissionRequest = CreateSubmissionRequest(
                context,
                options,
                operation,
                request,
                local.Plan,
                submissionConsent);
            if (!context.IsDryRun && submission is IJournaledSubmissionWorkflow journaled)
            {
                submissionHandle = await RunPrepareAsync(
                    journaled,
                    submissionRequest,
                    context).ConfigureAwait(false);
            }
        }

        if (!context.IsDryRun)
        {
            string expectedPlanFingerprint = local.Plan.Fingerprint;
            local = workflow is IVerifiedMutationWorkflow verified
                ? await RunVerifiedLocalAsync(
                    verified,
                    request,
                    expectedPlanFingerprint,
                    context).ConfigureAwait(false)
                : await RunLocalAsync(
                    workflow,
                    WithExecutionMode(request, WorkflowExecutionMode.Apply),
                    context).ConfigureAwait(false);
            if (applicableReleaseProvenance is not null)
            {
                local = local with
                {
                    Plan = local.Plan with { Release = applicableReleaseProvenance },
                };
            }

            if (!local.Applied || !string.IsNullOrWhiteSpace(local.ErrorMessage))
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
            }
        }

        if (submit)
        {
            remote = submissionHandle is not null
                ? await RunPreparedRemoteAsync(
                    (IJournaledSubmissionWorkflow)submission!,
                    submissionHandle,
                    context).ConfigureAwait(false)
                : await RunRemoteAsync(
                    submission!,
                    submissionRequest!,
                    context).ConfigureAwait(false);
            if (!context.IsDryRun && !remote.Applied)
            {
                MutationOutput.Write(context, local, remote);
                return remote.Code == GitHubLifecycleResultCode.Cancelled
                    && context.CancellationToken.IsCancellationRequested
                        ? ExitCodes.Cancelled
                        : ExitCodes.OperationFailed;
            }
        }

        MutationOutput.Write(context, local, remote);
        if (remote?.Applied == true
            && context.ParseResult.GetValue(options.OpenPullRequest)
            && remote.RemoteState.PullRequestUri is Uri pullRequestUri)
        {
            try
            {
                await _urlLauncher.OpenAsync(pullRequestUri, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                context.Output.WriteDiagnostic(
                    $"Pull request #{remote.RemoteState.PullRequestNumber} was created, "
                    + "but opening it was cancelled.");
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or Win32Exception)
            {
                context.Output.WriteDiagnostic(
                    $"Pull request #{remote.RemoteState.PullRequestNumber} was created, "
                    + $"but the browser could not be opened: {MutationRedact(exception.Message)}");
            }
        }

        if (remote?.Code == GitHubLifecycleResultCode.Cancelled)
        {
            return context.CancellationToken.IsCancellationRequested
                ? ExitCodes.Cancelled
                : ExitCodes.OperationFailed;
        }

        if (context.IsDryRun)
        {
            return (local.Code == WorkflowResultCode.Succeeded || learningOnly)
                && (remote is null || remote.Code == GitHubLifecycleResultCode.Planned)
                ? ExitCodes.Success
                : ExitCodes.OperationFailed;
        }

        return local.Applied && (remote is null || remote.Applied)
            ? ExitCodes.Success
            : ExitCodes.OperationFailed;
    }

    private static async Task<(WorkflowOperationResult Result, WorkflowOperationRequest Request)>
        ResolveQuestionsAsync(
        IMutationWorkflow workflow,
        WorkflowOperationResult result,
        WorkflowOperationRequest request,
        CommandContext context)
    {
        for (int attempt = 0; result.Code == WorkflowResultCode.QuestionsRequired && attempt < 32; attempt++)
        {
            if (!context.Interaction.CanPrompt)
            {
                MutationOutput.Write(context, result, remote: null);
                throw new MissingInputException(
                    "Required workflow input is missing; inspect the emitted questions and "
                    + "supply explicit command options or an override pack.");
            }

            WorkflowOperationRequest updated = request;
            foreach (WorkflowQuestion question in result.Plan.Questions)
            {
                if (!CanApplyQuestion(updated, question))
                {
                    throw new MissingInputException(
                        $"Workflow question '{question.Code}' requires an explicit command option or override pack.");
                }

                string answer = question.Options.IsEmpty
                    ? await context.Interaction.AskAsync(
                        question.Prompt,
                        cancellationToken: context.CancellationToken).ConfigureAwait(false)
                    : await context.Interaction.SelectAsync(
                        question.Prompt,
                        question.Options,
                        context.CancellationToken).ConfigureAwait(false);
                updated = ApplyAnswer(updated, question, answer);
            }

            request = updated;
            result = await RunLocalAsync(workflow, updated, context).ConfigureAwait(false);
        }

        return (result, request);
    }

    private static async Task<WorkflowOperationResult> RunLocalAsync(
        IMutationWorkflow workflow,
        WorkflowOperationRequest request,
        CommandContext context)
    {
        try
        {
            return await context.Interaction.RunProgressAsync(
                "Downloading and analyzing installers",
                cancellation => workflow.ExecuteAsync(request, cancellation),
                context.CancellationToken).ConfigureAwait(false);
        }

        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Local mutation timed out: {MutationRedact(exception.Message)}",
                exception);
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException
                or DownloadException
                or WorkflowOperationException)
        {
            throw new CliOperationException(
                $"Local mutation failed: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private static async Task<WorkflowOperationResult> RunVerifiedLocalAsync(
        IVerifiedMutationWorkflow workflow,
        WorkflowOperationRequest request,
        string expectedPlanFingerprint,
        CommandContext context)
    {
        try
        {
            return await workflow.ApplyVerifiedAsync(
                request,
                expectedPlanFingerprint,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Verified local mutation timed out: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException
                or DownloadException
                or WorkflowOperationException)
        {
            throw new CliOperationException(
                $"Verified local mutation failed: {exception.Message}",
                exception);
        }
    }

    private async Task<EditorResult> RunEditorAsync(
        ImmutableArray<RawManifestDocument> documents,
        CommandContext context)
    {
        try
        {
            return await _editor.EditAsync(documents, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Manifest editor timed out: {MutationRedact(exception.Message)}",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Win32Exception)
        {
            throw new CliOperationException(
                $"Manifest editing failed: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private static void ReportEditFailure(
        CommandContext context,
        WorkflowOperationResult local,
        int attempt,
        int maximumAttempts)
    {
        context.Interaction.ReportStatus(
            $"Edited manifests did not pass validation (attempt {attempt}/{maximumAttempts}).");
        foreach (ValidationFinding finding in local.Plan.Validation.Findings)
        {
            context.Interaction.ReportStatus(
                $"  {finding.Severity} finding: {finding.Code}");
        }
    }

    private static async Task<GitHubLifecycleResult> RunRemoteAsync(
        ISubmissionWorkflow submissions,
        GitHubSubmissionRequest request,
        CommandContext context)
    {
        try
        {
            return await submissions.ExecuteAsync(request, context.CancellationToken)
                .ConfigureAwait(false);
        }

        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Remote submission timed out: {MutationRedact(exception.Message)}",
                exception);
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            throw new CliOperationException(
                $"Remote submission failed: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private static async Task<SubmissionJournalHandle> RunPrepareAsync(
        IJournaledSubmissionWorkflow submissions,
        GitHubSubmissionRequest request,
        CommandContext context)
    {
        try
        {
            return await submissions.PrepareAsync(request, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new CliOperationException(
                $"Submission journal preparation failed: {exception.Message}",
                exception);
        }
    }

    private static async Task<GitHubLifecycleResult> RunPreparedRemoteAsync(
        IJournaledSubmissionWorkflow submissions,
        SubmissionJournalHandle handle,
        CommandContext context)
    {
        try
        {
            return await submissions.ExecutePreparedAsync(handle, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            throw new CliOperationException(
                $"Journaled remote submission failed: {exception.Message}",
                exception);
        }
    }

    private static async Task<GitHubLifecycleResult?> RunResumeAsync(
        IJournaledSubmissionWorkflow submissions,
        LocalOperationPlan noChangesPlan,
        CommandContext context)
    {
        try
        {
            return await submissions.ResumePendingAsync(
                noChangesPlan.OutputDirectory,
                noChangesPlan.PackageIdentifier,
                noChangesPlan.PackageVersion,
                context.Configuration.Repository,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            throw new CliOperationException(
                $"Pending submission recovery failed: {exception.Message}",
                exception);
        }
    }

    private async Task<IMutationWorkflow> CreateMutationWorkflowAsync(CommandContext context)
    {
        try
        {
            return await _workflowFactory.CreateAsync(context, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Mutation workflow setup timed out: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private async Task<ISubmissionWorkflow> CreateSubmissionWorkflowAsync(CommandContext context)
    {
        try
        {
            return await _submissionFactory!.CreateAsync(context, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliOperationException(
                $"Submission workflow setup timed out: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private static WorkflowOperationRequest ApplyCommon(
        CommandContext context,
        MutationOptions options,
        WorkflowOperationRequest request)
    {
        RuleRuntimeConfiguration runtime = ParseRuleRuntime(context, options);
        OverridePackSet packs = ParseOverridePacks(context.ParseResult, options);
        string createdWith = context.ParseResult.GetValue(options.CreatedWith) ?? "winmatsch";
        if (ContainsYamlLineBreak(createdWith))
        {
            throw new CliUsageException(
                "--created-with must be a single line without control characters.");
        }

        string? createdWithUrl = context.ParseResult.GetValue(options.CreatedWithUrl);
        if (!string.IsNullOrWhiteSpace(createdWithUrl))
        {
            Uri normalizedCreatedWithUrl = ParseHttpUri(
                createdWithUrl,
                "--created-with-url");
            createdWith = $"{createdWith} ({normalizedCreatedWithUrl.AbsoluteUri})";
        }

        return request switch
        {
            NewOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            UpdateOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            RemoveOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            SubmitOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            NewLocaleOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            UpdateLocaleOperationRequest value => value with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                CreatedWith = createdWith,
                WarningPolicy = WarningPolicy(context.ParseResult, options),
                RuleRuntime = runtime,
                OverridePacks = packs,
                ExplainRules = context.ParseResult.GetValue(options.ExplainRules),
            },
            _ => throw new ArgumentException("Unsupported mutation request.", nameof(request)),
        };
    }

    private static WorkflowOperationRequest ApplyAnswer(
        WorkflowOperationRequest request,
        WorkflowQuestion question,
        string answer)
    {
        if (question.Code.StartsWith("METADATA_", StringComparison.Ordinal))
        {
            return request switch
            {
                NewOperationRequest value => value with
                {
                    Locale = ApplyMetadata(value.Locale, question.Code, answer),
                },
                NewLocaleOperationRequest value => value with
                {
                    Locale = ApplyMetadata(value.Locale, question.Code, answer),
                },
                UpdateLocaleOperationRequest value => value with
                {
                    Locale = ApplyMetadata(value.Locale, question.Code, answer),
                },
                _ => throw new CliOperationException(
                    $"Workflow question '{question.Code}' cannot be mapped to this request."),
            };
        }

        if (question.Path is not null && TryParseArchitecture(answer, out Architecture architecture))
        {
            var mapping = new UrlOverride(ParseHttpUri(question.Path, "mapping URL"), architecture, null, null);
            return request switch
            {
                NewOperationRequest value => value with
                {
                    UrlOverrides = value.UrlOverrides.Add(mapping),
                },
                UpdateOperationRequest value => value with
                {
                    UrlOverrides = value.UrlOverrides.Add(mapping),
                },
                _ => throw new CliOperationException(
                    $"Workflow question '{question.Code}' cannot be mapped to this request."),
            };
        }

        if (answer.Equals("approve", StringComparison.OrdinalIgnoreCase))
        {
            return (request, question.Code) switch
            {
                (UpdateOperationRequest value, "MAP_STRUCTURAL_REWRITE") => value with
                {
                    AllowStructuralRewrite = true,
                },
                (UpdateOperationRequest value, "CONTENT_CHANGED_AT_STABLE_URL") => value with
                {
                    AllowStableUrlContentChange = true,
                },
                (NewOperationRequest value, "CONTENT_SHARED_ACROSS_URLS") => value with
                {
                    AllowSharedContentAcrossUrls = true,
                },
                (UpdateOperationRequest value, "CONTENT_SHARED_ACROSS_URLS") => value with
                {
                    AllowSharedContentAcrossUrls = true,
                },
                _ => throw new CliOperationException(
                    $"Workflow approval '{question.Code}' cannot be mapped to this request."),
            };
        }

        throw new CliOperationException(
            $"Workflow question '{question.Code}' requires an explicit command option.");
    }

    private static bool CanApplyQuestion(
        WorkflowOperationRequest request,
        WorkflowQuestion question)
        => question.Code.StartsWith("METADATA_", StringComparison.Ordinal)
            || (question.Path is not null
                && question.Options.Any(option => TryParseArchitecture(option, out _)))
            || (question.Code == "MAP_STRUCTURAL_REWRITE"
                && request is UpdateOperationRequest
                && question.Options.Contains("approve", StringComparer.OrdinalIgnoreCase))
            || (question.Code == "CONTENT_CHANGED_AT_STABLE_URL"
                && request is UpdateOperationRequest
                && question.Options.Contains("approve", StringComparer.OrdinalIgnoreCase))
            || (question.Code == "CONTENT_SHARED_ACROSS_URLS"
                && request is NewOperationRequest or UpdateOperationRequest
                && question.Options.Contains("approve", StringComparer.OrdinalIgnoreCase));

    private static ImmutableArray<RawManifestDocument> MergeEditedDocuments(
        ImmutableArray<RawManifestDocument> completeSet,
        ImmutableArray<RawManifestDocument> editableSet,
        ImmutableArray<RawManifestDocument> editedSet)
    {
        string[] expectedPaths =
        [
            .. editableSet.Select(static document => document.RepositoryPath)
                .Order(StringComparer.Ordinal),
        ];
        string[] actualPaths =
        [
            .. editedSet.Select(static document => document.RepositoryPath)
                .Order(StringComparer.Ordinal),
        ];
        if (!expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal)
            || actualPaths.Distinct(StringComparer.Ordinal).Count() != actualPaths.Length)
        {
            throw new CliOperationException(
                "The editor result changed the manifest file set; only file contents may be edited.");
        }

        Dictionary<string, RawManifestDocument> replacements = editedSet.ToDictionary(
            static document => document.RepositoryPath,
            StringComparer.Ordinal);
        return
        [
            .. completeSet.Select(document =>
                replacements.TryGetValue(document.RepositoryPath, out RawManifestDocument? edited)
                    ? edited
                    : document),
        ];
    }

    private static PackageLocaleMetadata ApplyMetadata(
        PackageLocaleMetadata metadata,
        string code,
        string answer)
        => code switch
        {
            "METADATA_PUBLISHER" => metadata with { Publisher = answer },
            "METADATA_PACKAGENAME" => metadata with { PackageName = answer },
            "METADATA_LICENSE" => metadata with { License = answer },
            "METADATA_SHORTDESCRIPTION" => metadata with { ShortDescription = answer },
            _ => throw new CliOperationException(
                $"Workflow metadata question '{code}' is not supported by this CLI."),
        };

    private static void ValidateReplace(
        ParseResult result,
        MutationOptions options,
        WorkflowOperationRequest request)
    {
        if (result.GetResult(options.Replace) is null)
        {
            return;
        }

        if (request is not UpdateOperationRequest update)
        {
            throw new CliUsageException("--replace is supported only by the update command.");
        }

        string? explicitVersion = result.GetValue(options.Replace);
        if (!string.IsNullOrWhiteSpace(explicitVersion)
            && !string.Equals(
                ParseVersion(explicitVersion).Value,
                update.PreviousVersion.Value,
                StringComparison.Ordinal))
        {
            throw new CliUsageException(
                $"--replace version '{explicitVersion}' must match the update source version "
                + $"'{update.PreviousVersion.Value}'.");
        }
    }

    private static WorkflowOperationRequest WithExecutionMode(
        WorkflowOperationRequest request,
        WorkflowExecutionMode mode)
        => request switch
        {
            NewOperationRequest value => value with { ExecutionMode = mode },
            UpdateOperationRequest value => value with { ExecutionMode = mode },
            RemoveOperationRequest value => value with { ExecutionMode = mode },
            SubmitOperationRequest value => value with { ExecutionMode = mode },
            NewLocaleOperationRequest value => value with { ExecutionMode = mode },
            UpdateLocaleOperationRequest value => value with { ExecutionMode = mode },
            _ => throw new ArgumentException("Unsupported mutation request.", nameof(request)),
        };

    private static GitHubSubmissionRequest CreateSubmissionRequest(
        CommandContext context,
        MutationOptions options,
        GitHubManifestOperation operation,
        WorkflowOperationRequest localRequest,
        LocalOperationPlan plan,
        bool submissionConsent)
    {
        string? replaceValue = context.ParseResult.GetValue(options.Replace);
        PackageVersion? previousVersion = null;
        if (!string.IsNullOrWhiteSpace(replaceValue))
        {
            previousVersion = ParseVersion(replaceValue);
        }
        else if (operation == GitHubManifestOperation.Update
                 && context.ParseResult.GetResult(options.Replace) is not null)
        {
            previousVersion = localRequest is UpdateOperationRequest update
                ? update.PreviousVersion
                : throw new CliUsageException(
                    "--replace without an explicit version requires the update command.");
        }

        return new()
        {
            LocalPlan = plan,
            UpstreamRepository = context.Configuration.Repository,
            ExecutionMode = context.IsDryRun
                ? WorkflowExecutionMode.Plan
                : WorkflowExecutionMode.Apply,
            Operation = previousVersion is null ? operation : GitHubManifestOperation.Replace,
            Policy = new()
            {
                ForkConsent = submissionConsent
                    ? ForkConsentPolicy.AllowCreate
                    : ForkConsentPolicy.ExistingOnly,
                SkipPullRequestCheck = context.ParseResult.GetValue(options.SkipPullRequestCheck),
                ReplacePreviousVersion = previousVersion is not null,
                PreviousVersion = previousVersion,
                MinimumReleaseFreshness = plan.Release is null
                    ? TimeSpan.Zero
                    : context.Configuration.FreshnessDelay,
            },
            CreatedWith = localRequest.CreatedWith,
            CustomTitle = context.ParseResult.GetValue(options.PullRequestTitle),
            Resolves = context.ParseResult.GetValue(options.Resolves),
            IdempotencyKey =
                $"{plan.Operation}:{plan.PackageIdentifier.Value}:{plan.PackageVersion.Value}",
            ReleaseUpdatedAt = plan.Release?.UpdatedAt,
            ReleaseRepository = plan.Release?.Repository,
            ReleaseId = plan.Release?.ReleaseId,
        };
    }

    private static PackageLocaleMetadata BindLocale(
        ParseResult result,
        MutationOptions options,
        string? localeOverride = null)
    {
        string locale = localeOverride
            ?? result.GetValue(options.Locale)
            ?? "en-US";
        return new()
        {
            PackageLocale = new LanguageTag(locale),
            Publisher = result.GetValue(options.Publisher),
            PublisherUrl = result.GetValue(options.PublisherUrl),
            PublisherSupportUrl = result.GetValue(options.PublisherSupportUrl),
            PrivacyUrl = result.GetValue(options.PrivacyUrl),
            Author = result.GetValue(options.Author),
            PackageName = result.GetValue(options.PackageName),
            PackageUrl = result.GetValue(options.PackageUrl),
            License = result.GetValue(options.License),
            LicenseUrl = result.GetValue(options.LicenseUrl),
            Copyright = result.GetValue(options.Copyright),
            CopyrightUrl = result.GetValue(options.CopyrightUrl),
            ShortDescription = result.GetValue(options.ShortDescription),
            Description = result.GetValue(options.Description),
            Tags = result.GetValue(options.Tags),
            ReleaseNotes = result.GetValue(options.ReleaseNotes),
            ReleaseNotesUrl = result.GetValue(options.ReleaseNotesUrl),
        };
    }

    private static ImmutableHashSet<string> InstallerUrls(LocalOperationPlan plan)
        => plan.Preflight.InstallerArtifacts
            .Select(static artifact => artifact.InstallerUrl)
            .ToImmutableHashSet(StringComparer.Ordinal);

    private static string PlanFingerprint(LocalOperationPlan plan)
    {
        var builder = new StringBuilder();
        Append(plan.Operation);
        Append(plan.PackageIdentifier.Value);
        Append(plan.PackageVersion.Value);
        Append(plan.OutputDirectory);
        Append(plan.WarningPolicy.ToString());
        Append(plan.ReviewApproved.ToString());
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static change => change.RepositoryPath,
                     StringComparer.Ordinal))
        {
            Append(change.Kind.ToString());
            Append(change.RepositoryPath);
            Append(change.ExpectedState.ToString());
            Append(change.ExpectedSha256);
            Append(Convert.ToHexString(SHA256.HashData(change.Content.AsSpan())));
        }

        foreach (RawManifestDocument document in plan.AfterDocuments.OrderBy(
                     static document => document.RepositoryPath,
                     StringComparer.Ordinal))
        {
            Append(document.RepositoryPath);
            Append(Convert.ToHexString(SHA256.HashData(document.Content.AsSpan())));
        }

        foreach (WorkflowQuestion question in plan.Questions)
        {
            Append(question.Code);
            Append(question.Prompt);
            Append(question.Path);
            foreach (string option in question.Options)
            {
                Append(option);
            }
        }

        foreach (var execution in plan.Rules.Executions)
        {
            Append(execution.RuleId);
            Append(execution.Mode.ToString());
            Append(execution.ModeSource.ToString());
        }

        foreach (var change in plan.Rules.Changes)
        {
            Append(change.RuleId);
            Append(change.ManifestPath);
            Append(change.FieldPath);
            Append(change.Before);
            Append(change.After);
        }

        foreach (var review in plan.Rules.Reviews)
        {
            Append(review.ManifestPath);
            Append(review.FieldPath);
            Append(review.BotValue);
            Append(review.HumanValue);
            Append(review.GeneratedValue);
        }

        foreach (ValidationFinding finding in plan.Validation.Findings)
        {
            Append(finding.Code);
            Append(finding.Severity.ToString());
            Append(finding.Message);
            Append(finding.Path);
        }

        if (plan.Release is { } release)
        {
            Append(release.Repository.ToString());
            Append(release.ReleaseId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(release.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

        void Append(string? value)
        {
            builder.Append(value?.Length ?? -1)
                .Append(':')
                .Append(value)
                .Append('|');
        }
    }

    private static string ReviewFingerprint(LocalOperationPlan plan)
    {
        var builder = new StringBuilder();
        foreach (var review in plan.Rules.Reviews.OrderBy(
                     static review => review.ManifestPath,
                     StringComparer.Ordinal).ThenBy(
                     static review => review.FieldPath,
                     StringComparer.Ordinal))
        {
            Append(review.ManifestPath);
            Append(review.FieldPath);
            Append(review.BotValue);
            Append(review.HumanValue);
            Append(review.GeneratedValue);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

        void Append(string? value)
        {
            builder.Append(value?.Length ?? -1)
                .Append(':')
                .Append(value)
                .Append('|');
        }
    }

    private static ReleaseRequest ParseRelease(ParseResult result, MutationOptions options)
    {
        ImmutableArray<Uri> releaseUrls = ParseUris(
            result.GetValue(options.ReleaseUrls),
            "--release-url");
        if (releaseUrls.Length > 1)
        {
            throw new CliUsageException(
                "--release-url may be specified only once; multiple release repositories "
                + "cannot be combined safely.");
        }

        return new(
            result.GetValue(options.Release),
            ParseUris(result.GetValue(options.Urls), "--urls"),
            releaseUrls);
    }

    private static ImmutableArray<Uri> ParseUris(string[]? values, string option)
        =>
        [
            .. (values ?? []).Select(value => ParseHttpUri(value, option)),
        ];

    private static ImmutableArray<UrlOverride> ParseUrlOverrides(
        ParseResult result,
        MutationOptions options)
        =>
        [
            .. (result.GetValue(options.UrlOverrides) ?? []).Select(UrlOverride.Parse),
        ];

    private static OverridePackSet ParseOverridePacks(
        ParseResult result,
        MutationOptions options)
    {
        string[] paths = result.GetValue(options.OverridePacks) ?? [];
        if (paths.Length == 0)
        {
            return OverridePackSet.BuiltIn;
        }

        try
        {
            var packs = OverridePackSet.BuiltIn.Packs.ToDictionary(
                static pack => pack.PackageIdentifier.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (OverridePack pack in paths.Select(OverridePackYaml.ReadFile))
            {
                packs[pack.PackageIdentifier.Value] = pack;
            }

            return new(packs.Values);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or FormatException
                or ArgumentException
                or YamlException)
        {
            throw new CliUsageException(
                $"Override-pack input failed: {MutationRedact(exception.Message)}",
                exception);
        }
    }

    private static RuleRuntimeConfiguration ParseRuleRuntime(
        CommandContext context,
        MutationOptions options)
    {
        ParseResult result = context.ParseResult;
        RuleMode defaultMode = ParseRuleMode(result.GetValue(options.DefaultRuleMode) ?? "apply");
        var overrides = new Dictionary<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in result.GetValue(options.RuleModes) ?? [])
        {
            int separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new FormatException("--rule-mode must use RULE_ID=apply|log-only|disabled syntax.");
            }

            string ruleId = entry[..separator];
            if (!overrides.TryAdd(ruleId, ParseRuleMode(entry[(separator + 1)..])))
            {
                throw new CliUsageException(
                    $"--rule-mode specifies rule '{ruleId}' more than once.");
            }
        }

        var userOverrides = new Dictionary<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        foreach (string ruleId in context.Configuration.EnabledRules)
        {
            userOverrides[ruleId] = RuleMode.Apply;
        }

        foreach (string ruleId in context.Configuration.DisabledRules)
        {
            userOverrides[ruleId] = RuleMode.Disabled;
        }

        return new(defaultMode, userOverrides, overrides);
    }

    private static RuleMode ParseRuleMode(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "apply" => RuleMode.Apply,
            "log-only" or "logonly" => RuleMode.LogOnly,
            "disabled" => RuleMode.Disabled,
            _ => throw new FormatException(
                $"Unknown rule mode '{value}'. Use apply, log-only, or disabled."),
        };

    private static WarningPolicy WarningPolicy(ParseResult result, MutationOptions options)
        => result.GetValue(options.WarningsAsErrors)
            ? WinMatsch.Validation.WarningPolicy.TreatAsErrors
            : WinMatsch.Validation.WarningPolicy.Allow;

    private static PackageIdentifier ParseIdentifier(string? value)
        => new(Require(value, "package identifier", "the package argument"));

    private static PackageVersion ParseVersion(string? value)
        => new(Require(value, "package version", "the version argument or --version"));

    private static Uri ParseHttpUri(string value, string option)
    {
        if (ContainsYamlLineBreak(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"{option} requires an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    private static bool ContainsYamlLineBreak(string value)
        => value.Any(static character =>
            char.IsControl(character)
            || character is '\u0085' or '\u2028' or '\u2029');

    private static string MutationRedact(string value)
        => CliRedactor.RedactUrl(value, redactAllQueryValues: true);

    private static bool TryParseArchitecture(string value, out Architecture architecture)
    {
        architecture = value.Trim().ToLowerInvariant() switch
        {
            "x86" => Architecture.X86,
            "x64" => Architecture.X64,
            "arm" => Architecture.Arm,
            "arm64" => Architecture.Arm64,
            "neutral" => Architecture.Neutral,
            _ => (Architecture)(-1),
        };
        return Enum.IsDefined(architecture);
    }

    private static string Require(string? value, string label, string channel)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new MissingInputException($"Missing {label}; supply it through {channel}.");

    private static string GetOutputDirectory(CommandContext context)
        => context.Configuration.OutputDirectory ?? Environment.CurrentDirectory;

    private static void EnsurePrompting(CommandContext context, string message)
    {
        if (!context.Interaction.CanPrompt)
        {
            throw new MissingInputException(message);
        }
    }

    private static void ReportApprovalContext(
        CommandContext context,
        LocalOperationPlan plan,
        string heading)
    {
        context.Interaction.ReportStatus(
            $"{heading}: {plan.Operation} {plan.PackageIdentifier.Value} {plan.PackageVersion.Value}");
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static item => item.RepositoryPath,
                     StringComparer.Ordinal))
        {
            context.Interaction.ReportStatus($"  {change.Kind}: {change.RepositoryPath}");
        }

        foreach (var review in plan.Rules.Reviews)
        {
            context.Interaction.ReportStatus(
                $"  Review: {review.ManifestPath}:{review.FieldPath} "
                + $"human={MutationRedact(review.HumanValue ?? "(null)")} "
                + $"generated={MutationRedact(review.GeneratedValue ?? "(null)")}");
        }

        foreach (ValidationFinding finding in plan.Validation.Findings)
        {
            context.Interaction.ReportStatus(
                $"  {finding.Severity} finding: {finding.Code}");
        }
    }

    private static Argument<string> PackageArgument() => new("package")
    {
        Description = "Exact package identifier, including repository casing.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static Argument<string> VersionArgument() => new("version")
    {
        Description = "Exact package version, including repository casing.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private sealed class MutationOptions
    {
        public MutationOptions(bool includeRelease, bool includeMetadata, bool includeReplace)
        {
            IncludeRelease = includeRelease;
            IncludeMetadata = includeMetadata;
            IncludeReplace = includeReplace;
        }

        public bool IncludeRelease { get; }

        public bool IncludeMetadata { get; }

        public bool IncludeReplace { get; }

        public Option<string?> Version { get; } = new("--version")
        {
            Description = "Target package version.",
            HelpName = "version",
        };

        public Option<string?> Release { get; } = new("--release")
        {
            Description = "Release tag or name to discover.",
            HelpName = "tag",
        };

        public Option<string[]> Urls { get; } = Multiple("--urls", "Installer HTTP(S) URLs.");

        public Option<string[]> ReleaseUrls { get; } =
            Multiple("--release-url", "Release metadata HTTP(S) URLs.");

        public Option<string[]> UrlOverrides { get; } =
            Multiple("--url", "Installer override in url|arch|scope|displayVersion form.");

        public Option<bool> Submit { get; } = new("--submit")
        {
            Description = "Submit the validated local plan through the GitHub lifecycle workflow.",
        };

        public Option<bool> OpenPullRequest { get; } = new("--open-pr")
        {
            Description = "Open the created pull request in the default browser.",
        };

        public Option<string?> Resolves { get; } = new("--resolves")
        {
            Description = "Issue reference included in the pull request body.",
        };

        public Option<string?> CreatedWith { get; } = new("--created-with")
        {
            Description = "Tool name written to generated manifest headers.",
        };

        public Option<string?> CreatedWithUrl { get; } = new("--created-with-url")
        {
            Description = "Tool HTTP(S) URL written with generated manifest provenance.",
        };

        public Option<bool> SkipPullRequestCheck { get; } = new("--skip-pr-check")
        {
            Description = "Skip only the early duplicate pull-request check.",
        };

        public Option<string?> Replace { get; } = new("--replace")
        {
            Description = "Replace the previous version, optionally naming its exact version.",
            Arity = ArgumentArity.ZeroOrOne,
            HelpName = "version",
        };

        public Option<string?> PullRequestTitle { get; } = new("--prtitle")
        {
            Description = "Custom pull-request title.",
        };

        public Option<bool> Yes { get; } = new("--yes")
        {
            Description = "Approve destructive actions, fork creation, and remote submission. "
                + "Does not answer workflow questions or approve human-correction reviews.",
        };

        public Option<bool> ApproveReviews { get; } = new("--approve-reviews")
        {
            Description = "Approve only the human-correction reviews listed in the emitted plan. "
                + "Does not answer mapping or metadata questions.",
        };

        public Option<bool> Edit { get; } = new("--edit")
        {
            Description = "Edit an isolated temporary manifest copy until validation succeeds or "
                + "the bounded retry loop is exited.",
        };

        public Option<int?> EditAttempts { get; } = new("--edit-attempts")
        {
            Description = "Maximum edit/validate attempts (default: 3, maximum: 10). Requires --edit.",
            HelpName = "count",
        };

        public Option<bool> AllowStructuralRewrite { get; } = new("--allow-structural-rewrite")
        {
            Description = "Approve an installer architecture/type/scope layout rewrite.",
        };

        public Option<bool> AllowStableUrlChange { get; } = new("--allow-stable-url-change")
        {
            Description = "Approve changed bytes behind a stable installer URL.",
        };

        public Option<bool> AllowSharedContent { get; } = new("--allow-shared-content")
        {
            Description = "Approve distinct installer URLs resolving to identical bytes.",
        };

        public Option<bool> WarningsAsErrors { get; } = new("--warnings-as-errors")
        {
            Description = "Treat validation warnings as blocking.",
        };

        public Option<string?> DefaultRuleMode { get; } = new("--default-rule-mode")
        {
            Description = "Default rule mode: apply, log-only, or disabled.",
        };

        public Option<string[]> RuleModes { get; } =
            Multiple("--rule-mode", "Rule override in RULE_ID=apply|log-only|disabled form.");

        public Option<bool> ExplainRules { get; } = new("--explain-rules")
        {
            Description = "Include deterministic rule trace output.",
        };

        public Option<string[]> OverridePacks { get; } =
            Multiple("--override-pack", "Path to a package override-pack YAML file.");

        public Option<string?> Locale { get; } = Text("--locale", "Default package locale.");

        public Option<string?> Publisher { get; } = Text("--publisher", "Publisher.");

        public Option<string?> PublisherUrl { get; } = Text("--publisher-url", "Publisher URL.");

        public Option<string?> PublisherSupportUrl { get; } =
            Text("--publisher-support-url", "Publisher support URL.");

        public Option<string?> PrivacyUrl { get; } = Text("--privacy-url", "Privacy URL.");

        public Option<string?> Author { get; } = Text("--author", "Author.");

        public Option<string?> PackageName { get; } = Text("--package-name", "Package name.");

        public Option<string?> PackageUrl { get; } = Text("--package-url", "Package URL.");

        public Option<string?> License { get; } = Text("--license", "License.");

        public Option<string?> LicenseUrl { get; } = Text("--license-url", "License URL.");

        public Option<string?> Copyright { get; } = Text("--copyright", "Copyright.");

        public Option<string?> CopyrightUrl { get; } = Text("--copyright-url", "Copyright URL.");

        public Option<string?> ShortDescription { get; } =
            Text("--short-description", "Short description.");

        public Option<string?> Description { get; } = Text("--description", "Description.");

        public Option<string[]> Tags { get; } = Multiple("--tags", "Package tags.");

        public Option<string?> ReleaseNotes { get; } = Text("--release-notes", "Release notes.");

        public Option<string?> ReleaseNotesUrl { get; } =
            Text("--release-notes-url", "Release notes URL.");

        public void AddTo(Command command)
        {
            command.Options.Add(Submit);
            command.Options.Add(OpenPullRequest);
            command.Options.Add(Resolves);
            command.Options.Add(CreatedWith);
            command.Options.Add(CreatedWithUrl);
            command.Options.Add(SkipPullRequestCheck);
            if (IncludeReplace)
            {
                command.Options.Add(Replace);
            }
            command.Options.Add(PullRequestTitle);
            command.Options.Add(Yes);
            command.Options.Add(ApproveReviews);
            command.Options.Add(Edit);
            command.Options.Add(EditAttempts);
            command.Options.Add(WarningsAsErrors);
            command.Options.Add(DefaultRuleMode);
            command.Options.Add(RuleModes);
            command.Options.Add(ExplainRules);
            command.Options.Add(OverridePacks);
            if (IncludeRelease)
            {
                command.Options.Add(Version);
                command.Options.Add(Release);
                command.Options.Add(Urls);
                command.Options.Add(ReleaseUrls);
                command.Options.Add(UrlOverrides);
                command.Options.Add(AllowSharedContent);
            }

            if (IncludeReplace)
            {
                command.Options.Add(AllowStructuralRewrite);
                command.Options.Add(AllowStableUrlChange);
            }

            if (IncludeMetadata)
            {
                command.Options.Add(Locale);
                command.Options.Add(Publisher);
                command.Options.Add(PublisherUrl);
                command.Options.Add(PublisherSupportUrl);
                command.Options.Add(PrivacyUrl);
                command.Options.Add(Author);
                command.Options.Add(PackageName);
                command.Options.Add(PackageUrl);
                command.Options.Add(License);
                command.Options.Add(LicenseUrl);
                command.Options.Add(Copyright);
                command.Options.Add(CopyrightUrl);
                command.Options.Add(ShortDescription);
                command.Options.Add(Description);
                command.Options.Add(Tags);
                command.Options.Add(ReleaseNotes);
                command.Options.Add(ReleaseNotesUrl);
            }
        }

        private static Option<string[]> Multiple(string name, string description) => new(name)
        {
            Description = description,
        };

        private static Option<string?> Text(string name, string description) => new(name)
        {
            Description = description,
        };
    }
}
