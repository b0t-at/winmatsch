using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using WinMatsch.Cli.Hosting;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;

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
}

public sealed class MutationCommandModule : ICommandModule
{
    private readonly IMutationWorkflow _workflow;
    private readonly ISubmissionWorkflow? _submissions;
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
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _submissions = submissions;
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
        var options = new MutationOptions(includeRelease: true, includeMetadata: true);
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
        var options = new MutationOptions(includeRelease: true, includeMetadata: false);
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
        var options = new MutationOptions(includeRelease: false, includeMetadata: false);
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
            Description = "Existing raw manifest file or directory.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var options = new MutationOptions(includeRelease: false, includeMetadata: false);
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
            string input = Require(context.ParseResult.GetValue(path), "manifest path", "--path");
            ImmutableArray<RawManifestDocument> documents;
            try
            {
                documents = await _manifestLoader.LoadAsync(
                    input,
                    GetOutputDirectory(context),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or FormatException)
            {
                throw new CliOperationException($"Manifest input failed: {exception.Message}", exception);
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
        var options = new MutationOptions(includeRelease: false, includeMetadata: true);
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
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new CliUsageException(exception.Message, exception);
        }

        WorkflowOperationResult local = await RunLocalAsync(request, context).ConfigureAwait(false);
        (local, request) = await ResolveQuestionsAsync(local, request, context).ConfigureAwait(false);
        if (local.Plan.RequiresReview)
        {
            ReportApprovalContext(context, local.Plan, "Review approval required");
            EnsurePrompting(context, "Review approval is required; rerun interactively.");
            bool approved = await context.Interaction.ConfirmAsync(
                "Approve the listed human-correction reviews?",
                defaultValue: false,
                context.CancellationToken).ConfigureAwait(false);
            if (!approved)
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
            }

            request = ApproveReview(request);
            local = await RunLocalAsync(request, context).ConfigureAwait(false);
        }

        if (local.Code != WorkflowResultCode.Succeeded)
        {
            MutationOutput.Write(context, local, remote: null);
            return ExitCodes.OperationFailed;
        }

        if (context.ParseResult.GetValue(options.Edit))
        {
            if (context.Configuration.OutputFormat == OutputFormat.Json || !context.Interaction.CanPrompt)
            {
                throw new MissingInputException(
                    "Manifest editing requires an interactive text terminal; JSON and non-interactive modes cannot edit.");
            }

            if (request is UpdateOperationRequest update && update.ReplacePreviousVersion)
            {
                throw new CliUsageException("--edit cannot be combined with --replace.");
            }

            EditorResult edited = await _editor.EditAsync(
                local.Plan.AfterDocuments,
                context.CancellationToken).ConfigureAwait(false);
            if (!edited.Accepted)
            {
                throw new OperationCanceledException(
                    edited.ErrorMessage ?? "Manifest editing was cancelled.",
                    context.CancellationToken);
            }

            request = ApplyCommon(context, options, new SubmitOperationRequest
            {
                OutputDirectory = request.OutputDirectory,
                Documents = edited.Documents,
            });
            local = await RunLocalAsync(request, context).ConfigureAwait(false);
            if (local.Code != WorkflowResultCode.Succeeded)
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
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

        if (!context.IsDryRun)
        {
            request = WithExecutionMode(request, WorkflowExecutionMode.Apply);
            local = await RunLocalAsync(request, context).ConfigureAwait(false);
            if (!local.Applied)
            {
                MutationOutput.Write(context, local, remote: null);
                return ExitCodes.OperationFailed;
            }
        }

        GitHubLifecycleResult? remote = null;
        bool submit = context.ParseResult.GetValue(options.Submit);
        if (submit)
        {
            if (_submissions is null)
            {
                throw new CliOperationException(
                    "Remote submission is unavailable because no submission workflow was composed.");
            }

            if (!context.IsDryRun && !context.ParseResult.GetValue(options.Yes))
            {
                ReportApprovalContext(context, local.Plan, "Remote submission approval required");
                EnsurePrompting(
                    context,
                    "Remote submission requires --yes in non-interactive mode.");
                bool approved = await context.Interaction.ConfirmAsync(
                    "Submit the validated manifest changes to GitHub?",
                    defaultValue: false,
                    context.CancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    MutationOutput.Write(context, local, remote: null);
                    return ExitCodes.OperationFailed;
                }
            }

            remote = await RunRemoteAsync(
                CreateSubmissionRequest(context, options, operation, request, local.Plan),
                context).ConfigureAwait(false);
            if (remote.Applied
                && context.ParseResult.GetValue(options.OpenPullRequest)
                && remote.RemoteState.PullRequestUri is Uri pullRequestUri)
            {
                try
                {
                    await _urlLauncher.OpenAsync(pullRequestUri, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    throw new CliOperationException(
                        $"Pull request launch failed: {exception.Message}",
                        exception);
                }
            }
        }

        MutationOutput.Write(context, local, remote);
        if (context.IsDryRun)
        {
            return local.Code == WorkflowResultCode.Succeeded
                && (remote is null || remote.Code == GitHubLifecycleResultCode.Planned)
                ? ExitCodes.Success
                : ExitCodes.OperationFailed;
        }

        return local.Applied && (remote is null || remote.Applied)
            ? ExitCodes.Success
            : ExitCodes.OperationFailed;
    }

    private async Task<(WorkflowOperationResult Result, WorkflowOperationRequest Request)>
        ResolveQuestionsAsync(
        WorkflowOperationResult result,
        WorkflowOperationRequest request,
        CommandContext context)
    {
        for (int attempt = 0; result.Code == WorkflowResultCode.QuestionsRequired && attempt < 32; attempt++)
        {
            EnsurePrompting(
                context,
                "Required workflow input is missing; supply command options in non-interactive mode.");
            WorkflowOperationRequest updated = request;
            foreach (WorkflowQuestion question in result.Plan.Questions)
            {
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
            result = await RunLocalAsync(updated, context).ConfigureAwait(false);
        }

        return (result, request);
    }

    private async Task<WorkflowOperationResult> RunLocalAsync(
        WorkflowOperationRequest request,
        CommandContext context)
    {
        try
        {
            return await _workflow.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            throw new CliOperationException($"Local mutation failed: {exception.Message}", exception);
        }
    }

    private async Task<GitHubLifecycleResult> RunRemoteAsync(
        GitHubSubmissionRequest request,
        CommandContext context)
    {
        try
        {
            return await _submissions!.ExecuteAsync(request, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            throw new CliOperationException($"Remote submission failed: {exception.Message}", exception);
        }
    }

    private static WorkflowOperationRequest ApplyCommon(
        CommandContext context,
        MutationOptions options,
        WorkflowOperationRequest request)
    {
        RuleRuntimeConfiguration runtime = ParseRuleRuntime(context.ParseResult, options);
        OverridePackSet packs = ParseOverridePacks(context.ParseResult, options);
        string createdWith = context.ParseResult.GetValue(options.CreatedWith) ?? "winmatsch";
        string? createdWithUrl = context.ParseResult.GetValue(options.CreatedWithUrl);
        if (!string.IsNullOrWhiteSpace(createdWithUrl))
        {
            _ = ParseHttpUri(createdWithUrl, "--created-with-url");
            createdWith = $"{createdWith} ({createdWithUrl})";
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

        throw new CliOperationException(
            $"Workflow question '{question.Code}' requires an explicit command option.");
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

    private static WorkflowOperationRequest ApproveReview(WorkflowOperationRequest request)
        => request switch
        {
            NewOperationRequest value => value with { ApproveReview = true },
            UpdateOperationRequest value => value with { ApproveReview = true },
            RemoveOperationRequest value => value with { ApproveReview = true },
            SubmitOperationRequest value => value with { ApproveReview = true },
            NewLocaleOperationRequest value => value with { ApproveReview = true },
            UpdateLocaleOperationRequest value => value with { ApproveReview = true },
            _ => throw new ArgumentException("Unsupported mutation request.", nameof(request)),
        };

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
        LocalOperationPlan plan)
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
                ForkConsent = context.ParseResult.GetValue(options.Yes)
                    ? ForkConsentPolicy.AllowCreate
                    : ForkConsentPolicy.ExistingOnly,
                SkipPullRequestCheck = context.ParseResult.GetValue(options.SkipPullRequestCheck),
                ReplacePreviousVersion = previousVersion is not null,
                PreviousVersion = previousVersion,
            },
            CreatedWith = context.ParseResult.GetValue(options.CreatedWith) ?? "winmatsch",
            CustomTitle = context.ParseResult.GetValue(options.PullRequestTitle),
            Resolves = context.ParseResult.GetValue(options.Resolves),
            IdempotencyKey =
                $"{plan.Operation}:{plan.PackageIdentifier.Value}:{plan.PackageVersion.Value}",
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

    private static ReleaseRequest ParseRelease(ParseResult result, MutationOptions options)
        => new(
            result.GetValue(options.Release),
            ParseUris(result.GetValue(options.Urls), "--urls"),
            ParseUris(result.GetValue(options.ReleaseUrls), "--release-url"));

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

        var packs = OverridePackSet.BuiltIn.Packs.ToDictionary(
            static pack => pack.PackageIdentifier.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (OverridePack pack in paths.Select(OverridePackYaml.ReadFile))
        {
            packs[pack.PackageIdentifier.Value] = pack;
        }

        return new(packs.Values);
    }

    private static RuleRuntimeConfiguration ParseRuleRuntime(
        ParseResult result,
        MutationOptions options)
    {
        RuleMode defaultMode = ParseRuleMode(result.GetValue(options.DefaultRuleMode) ?? "apply");
        var overrides = new Dictionary<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in result.GetValue(options.RuleModes) ?? [])
        {
            int separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new FormatException("--rule-mode must use RULE_ID=apply|log-only|disabled syntax.");
            }

            overrides.Add(entry[..separator], ParseRuleMode(entry[(separator + 1)..]));
        }

        return new(defaultMode, commandOverrides: overrides);
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
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"{option} requires an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

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
                $"  Review: {review.ManifestPath}:{review.FieldPath}");
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
        public MutationOptions(bool includeRelease, bool includeMetadata)
        {
            IncludeRelease = includeRelease;
            IncludeMetadata = includeMetadata;
        }

        public bool IncludeRelease { get; }

        public bool IncludeMetadata { get; }

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
            Description = "Explicitly approve destructive actions, fork creation, and submission.",
        };

        public Option<bool> Edit { get; } = new("--edit")
        {
            Description = "Edit an isolated temporary manifest copy and rerun full preflight.",
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
            command.Options.Add(Replace);
            command.Options.Add(PullRequestTitle);
            command.Options.Add(Yes);
            command.Options.Add(Edit);
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
                command.Options.Add(AllowStructuralRewrite);
                command.Options.Add(AllowStableUrlChange);
                command.Options.Add(AllowSharedContent);
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
            AllowMultipleArgumentsPerToken = true,
        };

        private static Option<string?> Text(string name, string description) => new(name)
        {
            Description = description,
        };
    }
}
