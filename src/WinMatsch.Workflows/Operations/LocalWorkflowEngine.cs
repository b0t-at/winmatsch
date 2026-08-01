using System.Collections.Immutable;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Rules;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;

namespace WinMatsch.Workflows.Operations;

public sealed class LocalWorkflowEngine
{
    private readonly IManifestSnapshotSource _manifests;
    private readonly IWorkflowReleaseSource? _releases;
    private readonly IWorkflowArtifactProcessor? _artifacts;
    private readonly IWorkflowRuleRunner _rules;
    private readonly IWorkflowPreflight _preflight;
    private readonly IWorkflowFileTransaction _transaction;
    private readonly IWorkflowClock _clock;

    public LocalWorkflowEngine(
        IManifestSnapshotSource manifests,
        IWorkflowRuleRunner rules,
        IWorkflowPreflight preflight,
        IWorkflowFileTransaction transaction,
        IWorkflowReleaseSource? releases = null,
        IWorkflowArtifactProcessor? artifacts = null,
        IWorkflowClock? clock = null)
    {
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _releases = releases;
        _artifacts = artifacts;
        _clock = clock ?? new SystemWorkflowClock();
    }

    public Task<WorkflowOperationResult> NewAsync(
        NewOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateOrUpdateAsync(request, previous: null, cancellationToken);
    }

    public async Task<WorkflowOperationResult> UpdateAsync(
        UpdateOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PackageSnapshot? previous = await _manifests.LoadAsync(
            request.OutputDirectory,
            request.PackageIdentifier,
            request.PreviousVersion,
            cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return MissingResult("update", request, request.PackageIdentifier, request.PreviousVersion);
        }

        return await CreateOrUpdateAsync(request, previous, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowOperationResult> RemoveAsync(
        RemoveOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PackageSnapshot? snapshot = await _manifests.LoadAsync(
            request.OutputDirectory,
            request.PackageIdentifier,
            request.PackageVersion,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return MissingResult("remove", request, request.PackageIdentifier, request.PackageVersion);
        }

        ImmutableArray<WorkflowFileChange> changes =
        [
            .. snapshot.Documents
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal)
                .Select(static document => new WorkflowFileChange(
                    PlannedChangeKind.Delete,
                    document.RepositoryPath)),
        ];
        ValidationReport validation = await _preflight.ValidateAsync(
            new()
            {
                BeforeDocuments = snapshot.Documents,
                AfterDocuments = [],
                Changes = changes,
                Options = PreflightOptions(request),
            },
            cancellationToken).ConfigureAwait(false);
        LocalOperationPlan plan = Plan(
            "remove",
            request,
            request.PackageIdentifier,
            request.PackageVersion,
            changes,
            snapshot.Documents,
            [],
            validation,
            RuleRunSummary.Empty,
            [],
            [new("REMOVE_EXACT", "Planned exact package-version removal.", snapshot.VersionDirectory)]);
        return await CompleteAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowOperationResult> SubmitAsync(
        SubmitOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ParsedRawSet parsed;
        try
        {
            parsed = ParseRawSet(request.Documents);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return InvalidResult("submit", request, exception.Message);
        }

        PackageSnapshot? before = await _manifests.LoadAsync(
            request.OutputDirectory,
            parsed.Identifier,
            parsed.Version,
            cancellationToken).ConfigureAwait(false);
        PackageManifests candidate = parsed.Manifests;
        RuleRunSummary ruleSummary = RuleRunSummary.Empty;
        ImmutableArray<RawManifestDocument> after = request.Documents;
        if (request.Normalize)
        {
            WorkflowRuleResult ruleResult = RunRules(request, candidate, before, [], request.PolicyEvidence);
            candidate = ruleResult.Manifests;
            ruleSummary = ruleResult.Summary;
            after = Serialize(candidate, request.CreatedWith);
        }

        ImmutableArray<WorkflowFileChange> changes = Diff(before?.Documents ?? [], after);
        ValidationReport validation = await ValidateAsync(
            request,
            before?.Documents ?? [],
            after,
            changes,
            [],
            cancellationToken).ConfigureAwait(false);
        validation = MergeRuleFindings(validation, ruleSummary);
        LocalOperationPlan plan = Plan(
            "submit",
            request,
            parsed.Identifier,
            parsed.Version,
            changes,
            before?.Documents ?? [],
            after,
            validation,
            ruleSummary,
            [],
            [new("SUBMIT_RAW", request.Normalize
                ? "User manifests were explicitly normalized."
                : "User manifest bytes were preserved exactly.")]);
        return await CompleteAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowOperationResult> NewLocaleAsync(
        NewLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
        => LocaleAsync(request, update: false, cancellationToken);

    public Task<WorkflowOperationResult> UpdateLocaleAsync(
        UpdateLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
        => LocaleAsync(request, update: true, cancellationToken);

    private async Task<WorkflowOperationResult> CreateOrUpdateAsync(
        WorkflowOperationRequest operationRequest,
        PackageSnapshot? previous,
        CancellationToken cancellationToken)
    {
        bool isUpdate = previous is not null;
        NewOperationRequest? create = operationRequest as NewOperationRequest;
        UpdateOperationRequest? update = operationRequest as UpdateOperationRequest;
        PackageIdentifier identifier = create?.PackageIdentifier
            ?? update?.PackageIdentifier
            ?? throw new ArgumentException("Unsupported create/update request.", nameof(operationRequest));
        ImmutableArray<DiscoveredAsset> assets = create?.Assets ?? update!.Assets;
        ReleaseRequest release = create?.Release ?? update!.Release;
        if (assets.IsEmpty && _releases is not null)
        {
            assets = await _releases.DiscoverAsync(identifier, release, cancellationToken).ConfigureAwait(false);
        }

        if (assets.IsEmpty)
        {
            return InvalidResult(isUpdate ? "update" : "new", operationRequest, "No Windows release assets were supplied or discovered.");
        }

        string? artifactDirectory = create?.ArtifactDirectory ?? update?.ArtifactDirectory;
        var artifactSnapshots = ImmutableArray.CreateBuilder<ArtifactSnapshot>();
        var enrichedAssets = ImmutableArray.CreateBuilder<DiscoveredAsset>();
        foreach (DiscoveredAsset asset in assets.OrderBy(static item => item.DownloadUri.AbsoluteUri, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Content is not null && asset.Analysis is not null)
            {
                enrichedAssets.Add(asset);
                continue;
            }

            if (_artifacts is null || string.IsNullOrWhiteSpace(artifactDirectory))
            {
                return InvalidResult(
                    isUpdate ? "update" : "new",
                    operationRequest,
                    $"Asset '{asset.DownloadUri}' requires download and analysis evidence.");
            }

            ArtifactSnapshot snapshot = await _artifacts.AcquireAsync(
                asset,
                artifactDirectory,
                cancellationToken).ConfigureAwait(false);
            artifactSnapshots.Add(snapshot);
            enrichedAssets.Add(snapshot.Asset);
        }

        string? requestedVersion = create?.PackageVersion ?? update?.PackageVersion;
        PackageVersionResolution versionResolution = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = identifier,
            ExplicitPackageVersion = requestedVersion,
            OverridePacks = operationRequest.OverridePacks,
            Assets = enrichedAssets.ToImmutable(),
        });
        ImmutableArray<UrlOverride> urlOverrides = create?.UrlOverrides ?? update!.UrlOverrides;
        AssetMappingPlan mapping = AssetMappingPlanner.CreatePlan(new()
        {
            PackageIdentifier = identifier,
            Version = versionResolution,
            Assets = enrichedAssets.ToImmutable(),
            PreviousInstallers = previous is null ? [] : PreviousInstallerEntry.FromManifests(previous.Manifests),
            OverridePacks = operationRequest.OverridePacks,
            UrlOverrides = urlOverrides,
            AllowStructuralRewrite = update?.AllowStructuralRewrite ?? false,
            AllowStableUrlContentChange = update?.AllowStableUrlContentChange ?? false,
            AllowSharedContentAcrossUrls = create?.AllowSharedContentAcrossUrls
                ?? update?.AllowSharedContentAcrossUrls
                ?? false,
        });
        ImmutableArray<WorkflowQuestion> mappingQuestions =
        [
            .. mapping.UnresolvedQuestions.Select(static question => new WorkflowQuestion(
                question.Code,
                question.Prompt,
                question.Options,
                question.AssetUrl)),
        ];
        if (!mapping.CanApply || versionResolution.Version is null)
        {
            PackageVersion fallback = previous?.PackageVersion
                ?? (PackageVersion.TryCreate(requestedVersion ?? "0", out PackageVersion? value)
                    ? value!
                    : new PackageVersion("0"));
            return QuestionResult(
                isUpdate ? "update" : "new",
                operationRequest,
                identifier,
                fallback,
                mappingQuestions.IsEmpty
                    ? [new("MAPPING_UNRESOLVED", "Release asset mapping requires explicit input.", [])]
                    : mappingQuestions,
                mapping.Diagnostics.Select(static item =>
                    new WorkflowAuditEntry(item.Code, item.Message, item.AssetUrl)));
        }

        PackageVersion newVersion = versionResolution.Version;
        PackageSnapshot? existing = await _manifests.LoadAsync(
            operationRequest.OutputDirectory,
            identifier,
            newVersion,
            cancellationToken).ConfigureAwait(false);
        if (!isUpdate && existing is not null)
        {
            return ConflictResult("new", operationRequest, identifier, newVersion, "The package version already exists.");
        }

        if (isUpdate
            && existing is not null
            && !string.Equals(previous!.PackageVersion.Value, newVersion.Value, StringComparison.Ordinal))
        {
            return ConflictResult(
                "update",
                operationRequest,
                identifier,
                newVersion,
                "The target package version already exists.");
        }

        PackageManifests candidate;
        ImmutableArray<WorkflowQuestion> metadataQuestions = [];
        if (previous is null)
        {
            metadataQuestions = RequiredMetadataQuestions(create!.Locale);
            if (!metadataQuestions.IsEmpty)
            {
                return QuestionResult(
                    "new",
                    operationRequest,
                    identifier,
                    newVersion,
                    metadataQuestions,
                    []);
            }

            candidate = CreateNewManifests(identifier, newVersion, create.Locale, mapping);
        }
        else
        {
            candidate = CloneWithVersion(previous.Manifests, newVersion);
            candidate.Installer.Installers = CreateInstallers(mapping);
        }

        ImmutableArray<InstallerEvidence> installerEvidence =
        [
            .. artifactSnapshots.Select(static snapshot => new InstallerEvidence
            {
                InstallerUrl = snapshot.Asset.DownloadUri.AbsoluteUri,
                Analysis = snapshot.Analysis,
                Properties = snapshot.Properties,
            }),
        ];
        PolicyEvidence policyEvidence = MergePolicyEvidence(
            operationRequest.PolicyEvidence,
            artifactSnapshots,
            enrichedAssets);
        WorkflowRuleResult rules = RunRules(
            operationRequest,
            candidate,
            previous,
            installerEvidence,
            policyEvidence);
        candidate = rules.Manifests;
        ImmutableArray<RawManifestDocument> after = Serialize(candidate, operationRequest.CreatedWith);
        ImmutableArray<RawManifestDocument> beforeDocuments = existing?.Documents ?? [];
        ImmutableArray<WorkflowFileChange> changes = Diff(beforeDocuments, after);
        if (update?.ReplacePreviousVersion == true
            && !string.Equals(previous!.PackageVersion.Value, newVersion.Value, StringComparison.Ordinal))
        {
            changes =
            [
                .. previous.Documents.Select(static document =>
                    new WorkflowFileChange(PlannedChangeKind.Delete, document.RepositoryPath)),
                .. changes,
            ];
            beforeDocuments = [.. previous.Documents, .. beforeDocuments];
        }

        if (isUpdate && InstallerHashesEqual(previous!.Manifests, candidate))
        {
            return NoChangeResult("update", operationRequest, identifier, newVersion, beforeDocuments);
        }

        ValidationReport validation = await ValidateAsync(
            operationRequest,
            beforeDocuments,
            after,
            changes,
            [
                .. artifactSnapshots.Select(static snapshot => new InstallerArtifact(
                    snapshot.Asset.DownloadUri.AbsoluteUri,
                    snapshot.Download)),
            ],
            cancellationToken).ConfigureAwait(false);
        validation = MergeRuleFindings(validation, rules.Summary);
        ImmutableArray<WorkflowAuditEntry> audit =
        [
            new("VERSION", $"Resolved package version {newVersion.Value}.", versionResolution.Source?.ToString()),
            .. enrichedAssets.Select(static asset => new WorkflowAuditEntry(
                "RELEASE_ASSET",
                asset.DownloadUri.AbsoluteUri,
                $"{asset.ReleaseUri.AbsoluteUri}|{asset.ReleaseTag}|{asset.ReleasePublishedAt:O}")),
            .. mapping.Decisions.Select(static decision => new WorkflowAuditEntry(
                $"MAP_{decision.Kind.ToString().ToUpperInvariant()}",
                decision.Reason,
                decision.Installer?.Url.AbsoluteUri)),
            new("CREATED_AT", _clock.UtcNow.ToString("O"), "workflow-clock"),
        ];
        LocalOperationPlan plan = Plan(
            isUpdate ? "update" : "new",
            operationRequest,
            identifier,
            newVersion,
            changes,
            beforeDocuments,
            after,
            validation,
            rules.Summary,
            [],
            audit);
        return await CompleteAsync(operationRequest, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowOperationResult> LocaleAsync(
        WorkflowOperationRequest operationRequest,
        bool update,
        CancellationToken cancellationToken)
    {
        PackageIdentifier identifier;
        PackageVersion version;
        PackageLocaleMetadata metadata;
        if (operationRequest is NewLocaleOperationRequest create)
        {
            identifier = create.PackageIdentifier;
            version = create.PackageVersion;
            metadata = create.Locale;
        }
        else if (operationRequest is UpdateLocaleOperationRequest change)
        {
            identifier = change.PackageIdentifier;
            version = change.PackageVersion;
            metadata = change.Locale;
        }
        else
        {
            throw new ArgumentException("Unsupported locale request.", nameof(operationRequest));
        }

        PackageSnapshot? snapshot = await _manifests.LoadAsync(
            operationRequest.OutputDirectory,
            identifier,
            version,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return MissingResult(update ? "update-locale" : "new-locale", operationRequest, identifier, version);
        }

        LanguageTag defaultLocale = snapshot.Manifests.Version.DefaultLocale!;
        if (metadata.PackageLocale == defaultLocale)
        {
            return InvalidResult(
                update ? "update-locale" : "new-locale",
                operationRequest,
                "Locale workflows cannot add or update the default locale.");
        }

        int existingIndex = snapshot.Manifests.Locales.FindIndex(
            locale => locale.PackageLocale == metadata.PackageLocale);
        if ((!update && existingIndex >= 0) || (update && existingIndex < 0))
        {
            return ConflictResult(
                update ? "update-locale" : "new-locale",
                operationRequest,
                identifier,
                version,
                update ? "The locale does not exist." : "The locale already exists.");
        }

        PackageManifests candidate = CloneWithVersion(snapshot.Manifests, version);
        LocaleManifest locale = update
            ? ApplyLocaleMetadata(candidate.Locales[existingIndex], metadata)
            : CreateLocale(identifier, version, metadata);
        if (update)
        {
            candidate.Locales[existingIndex] = locale;
        }
        else
        {
            candidate.Locales.Add(locale);
        }

        WorkflowRuleResult rules = RunRules(
            operationRequest,
            candidate,
            snapshot,
            [],
            operationRequest.PolicyEvidence);
        candidate = rules.Manifests;
        ImmutableArray<RawManifestDocument> serialized = Serialize(candidate, operationRequest.CreatedWith);
        string localeFile = $"{ManifestPaths.GetVersionDirectory(identifier, version)}/{ManifestPaths.GetLocaleFileName(identifier, metadata.PackageLocale)}";
        RawManifestDocument changedLocale = serialized.Single(document =>
            string.Equals(document.RepositoryPath, localeFile, StringComparison.Ordinal));
        ImmutableArray<RawManifestDocument> after =
        [
            .. snapshot.Documents
                .Where(document => !string.Equals(document.RepositoryPath, localeFile, StringComparison.Ordinal))
                .Append(changedLocale)
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
        ImmutableArray<WorkflowFileChange> changes = Diff(snapshot.Documents, after);
        ValidationReport validation = await ValidateAsync(
            operationRequest,
            snapshot.Documents,
            after,
            changes,
            [],
            cancellationToken).ConfigureAwait(false);
        validation = MergeRuleFindings(validation, rules.Summary);
        LocalOperationPlan plan = Plan(
            update ? "update-locale" : "new-locale",
            operationRequest,
            identifier,
            version,
            changes,
            snapshot.Documents,
            after,
            validation,
            rules.Summary,
            [],
            [new("LOCALE_EXACT", $"{(update ? "Updated" : "Added")} locale {metadata.PackageLocale.Value}.", localeFile)]);
        return await CompleteAsync(operationRequest, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ValidationReport> ValidateAsync(
        WorkflowOperationRequest request,
        ImmutableArray<RawManifestDocument> before,
        ImmutableArray<RawManifestDocument> after,
        ImmutableArray<WorkflowFileChange> changes,
        ImmutableArray<InstallerArtifact> artifacts,
        CancellationToken cancellationToken)
        => await _preflight.ValidateAsync(
            new()
            {
                BeforeDocuments = before,
                AfterDocuments = after,
                Changes = changes,
                InstallerArtifacts = artifacts,
                Options = PreflightOptions(request),
            },
            cancellationToken).ConfigureAwait(false);

    private async Task<WorkflowOperationResult> CompleteAsync(
        WorkflowOperationRequest request,
        LocalOperationPlan plan,
        CancellationToken cancellationToken)
    {
        WorkflowResultCode code = ResultCode(plan);
        if (request.ExecutionMode == WorkflowExecutionMode.Plan || code != WorkflowResultCode.Succeeded)
        {
            return new() { Code = code, Plan = plan, Applied = false };
        }

        try
        {
            await _transaction.ApplyAsync(
                request.OutputDirectory,
                plan.PackageIdentifier.Value,
                plan.FileChanges,
                cancellationToken).ConfigureAwait(false);
            return new() { Code = WorkflowResultCode.Succeeded, Plan = plan, Applied = true };
        }
        catch (WorkflowOperationException exception)
        {
            return new()
            {
                Code = exception.Code,
                Plan = plan,
                Applied = false,
                ErrorMessage = exception.Message,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.ApplyFailed,
                "The local manifest transaction failed and was rolled back.",
                exception);
        }
    }

    private static WorkflowResultCode ResultCode(LocalOperationPlan plan)
    {
        if (!plan.Questions.IsEmpty)
        {
            return WorkflowResultCode.QuestionsRequired;
        }

        if (plan.RequiresReview)
        {
            return WorkflowResultCode.ReviewRequired;
        }

        if (!plan.Validation.CanProceed(plan.WarningPolicy))
        {
            return WorkflowResultCode.ValidationFailed;
        }

        return plan.FileChanges.IsEmpty ? WorkflowResultCode.NoChanges : WorkflowResultCode.Succeeded;
    }

    private WorkflowRuleResult RunRules(
        WorkflowOperationRequest request,
        PackageManifests candidate,
        PackageSnapshot? previous,
        ImmutableArray<InstallerEvidence> installerEvidence,
        PolicyEvidence policyEvidence)
        => _rules.Run(new()
        {
            Manifests = candidate,
            Previous = previous?.Manifests,
            OriginalBotSubmission = previous?.OriginalBotSubmission,
            InstallerEvidence = installerEvidence,
            Runtime = request.RuleRuntime,
            OverridePacks = request.OverridePacks,
            PolicyEvidence = policyEvidence,
            Options = new RuleOptions { Explain = request.ExplainRules },
        });

    private static PackageManifests CreateNewManifests(
        PackageIdentifier identifier,
        PackageVersion version,
        PackageLocaleMetadata metadata,
        AssetMappingPlan mapping)
        => new()
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = metadata.PackageLocale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                Installers = CreateInstallers(mapping),
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = metadata.PackageLocale,
                Publisher = metadata.Publisher,
                PublisherUrl = metadata.PublisherUrl,
                PublisherSupportUrl = metadata.PublisherSupportUrl,
                PrivacyUrl = metadata.PrivacyUrl,
                Author = metadata.Author,
                PackageName = metadata.PackageName,
                PackageUrl = metadata.PackageUrl,
                License = metadata.License,
                LicenseUrl = metadata.LicenseUrl,
                Copyright = metadata.Copyright,
                CopyrightUrl = metadata.CopyrightUrl,
                ShortDescription = metadata.ShortDescription,
                Description = metadata.Description,
                Tags = metadata.Tags is null ? null : [.. metadata.Tags],
                ReleaseNotes = metadata.ReleaseNotes,
                ReleaseNotesUrl = metadata.ReleaseNotesUrl,
            },
            Locales = [],
        };

    private static List<Installer> CreateInstallers(AssetMappingPlan mapping)
        => mapping.Decisions
            .Where(static decision => decision.Installer is not null)
            .Select(static decision =>
            {
                PlannedInstaller planned = decision.Installer!;
                return new Installer
                {
                    InstallerUrl = planned.Url.AbsoluteUri,
                    InstallerSha256 = planned.Sha256,
                    Architecture = planned.Architecture,
                    InstallerType = planned.InstallerType,
                    NestedInstallerType = planned.NestedInstallerType,
                    Scope = planned.Scope,
                    InstallerLocale = planned.InstallerLocale,
                    NestedInstallerFiles =
                    [
                        .. planned.NestedInstallerFiles.Select(static file => new NestedInstallerFile
                        {
                            RelativeFilePath = file.RelativeFilePath,
                            PortableCommandAlias = file.PortableCommandAlias,
                        }),
                    ],
                    ArchiveBinariesDependOnPath = planned.ArchiveBinariesDependOnPath,
                    AppsAndFeaturesEntries = planned.DisplayVersion is null
                        ? null
                        : [new AppsAndFeaturesEntry { DisplayVersion = planned.DisplayVersion }],
                };
            })
            .ToList();

    private static LocaleManifest CreateLocale(
        PackageIdentifier identifier,
        PackageVersion version,
        PackageLocaleMetadata metadata)
        => new()
        {
            PackageIdentifier = identifier,
            PackageVersion = version,
            PackageLocale = metadata.PackageLocale,
            Publisher = metadata.Publisher,
            PublisherUrl = metadata.PublisherUrl,
            PublisherSupportUrl = metadata.PublisherSupportUrl,
            PrivacyUrl = metadata.PrivacyUrl,
            Author = metadata.Author,
            PackageName = metadata.PackageName,
            PackageUrl = metadata.PackageUrl,
            License = metadata.License,
            LicenseUrl = metadata.LicenseUrl,
            Copyright = metadata.Copyright,
            CopyrightUrl = metadata.CopyrightUrl,
            ShortDescription = metadata.ShortDescription,
            Description = metadata.Description,
            Tags = metadata.Tags is null ? null : [.. metadata.Tags],
            ReleaseNotes = metadata.ReleaseNotes,
            ReleaseNotesUrl = metadata.ReleaseNotesUrl,
        };

    private static LocaleManifest ApplyLocaleMetadata(
        LocaleManifest locale,
        PackageLocaleMetadata metadata)
    {
        locale.PackageLocale = metadata.PackageLocale;
        locale.Publisher = metadata.Publisher ?? locale.Publisher;
        locale.PublisherUrl = metadata.PublisherUrl ?? locale.PublisherUrl;
        locale.PublisherSupportUrl = metadata.PublisherSupportUrl ?? locale.PublisherSupportUrl;
        locale.PrivacyUrl = metadata.PrivacyUrl ?? locale.PrivacyUrl;
        locale.Author = metadata.Author ?? locale.Author;
        locale.PackageName = metadata.PackageName ?? locale.PackageName;
        locale.PackageUrl = metadata.PackageUrl ?? locale.PackageUrl;
        locale.License = metadata.License ?? locale.License;
        locale.LicenseUrl = metadata.LicenseUrl ?? locale.LicenseUrl;
        locale.Copyright = metadata.Copyright ?? locale.Copyright;
        locale.CopyrightUrl = metadata.CopyrightUrl ?? locale.CopyrightUrl;
        locale.ShortDescription = metadata.ShortDescription ?? locale.ShortDescription;
        locale.Description = metadata.Description ?? locale.Description;
        locale.Tags = metadata.Tags is null ? locale.Tags : [.. metadata.Tags];
        locale.ReleaseNotes = metadata.ReleaseNotes ?? locale.ReleaseNotes;
        locale.ReleaseNotesUrl = metadata.ReleaseNotesUrl ?? locale.ReleaseNotesUrl;
        return locale;
    }

    private static ImmutableArray<WorkflowQuestion> RequiredMetadataQuestions(PackageLocaleMetadata metadata)
    {
        var questions = ImmutableArray.CreateBuilder<WorkflowQuestion>();
        Add(nameof(metadata.Publisher), metadata.Publisher);
        Add(nameof(metadata.PackageName), metadata.PackageName);
        Add(nameof(metadata.License), metadata.License);
        Add(nameof(metadata.ShortDescription), metadata.ShortDescription);
        return questions.ToImmutable();

        void Add(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                questions.Add(new($"METADATA_{field.ToUpperInvariant()}", $"Provide required locale metadata '{field}'.", []));
            }
        }
    }

    private static ImmutableArray<RawManifestDocument> Serialize(PackageManifests manifests, string createdWith)
    {
        string directory = ManifestPaths.GetVersionDirectory(
            manifests.Version.PackageIdentifier!,
            manifests.Version.PackageVersion!);
        return
        [
            .. PackageManifestIO.SerializeFiles(
                    manifests,
                    new ManifestWriteOptions { CreatedWith = createdWith })
                .Select(pair => new RawManifestDocument(
                    $"{directory}/{pair.Key}",
                    StrictUtf8.Encode(pair.Value)))
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
    }

    private static PackageManifests CloneWithVersion(PackageManifests source, PackageVersion version)
    {
        IReadOnlyDictionary<string, string> files = PackageManifestIO.SerializeFiles(source);
        InstallerManifest installer = ManifestYamlReader.ReadInstaller(
            files.Single(static pair => pair.Key.EndsWith(".installer.yaml", StringComparison.Ordinal)).Value);
        DefaultLocaleManifest defaultLocale = ManifestYamlReader.ReadDefaultLocale(
            files.Single(static pair =>
                pair.Key.Contains(".locale.", StringComparison.Ordinal)
                && ManifestYamlReader.TryDetectType(pair.Value) == ManifestType.DefaultLocale).Value);
        VersionManifest versionManifest = ManifestYamlReader.ReadVersion(
            files.Single(static pair => ManifestYamlReader.TryDetectType(pair.Value) == ManifestType.Version).Value);
        List<LocaleManifest> locales = files
            .Where(static pair => ManifestYamlReader.TryDetectType(pair.Value) == ManifestType.Locale)
            .Select(static pair => ManifestYamlReader.ReadLocale(pair.Value))
            .ToList();
        versionManifest.PackageVersion = version;
        installer.PackageVersion = version;
        defaultLocale.PackageVersion = version;
        foreach (LocaleManifest locale in locales)
        {
            locale.PackageVersion = version;
        }

        return new()
        {
            Version = versionManifest,
            Installer = installer,
            DefaultLocale = defaultLocale,
            Locales = locales,
        };
    }

    private static ParsedRawSet ParseRawSet(ImmutableArray<RawManifestDocument> documents)
    {
        if (documents.IsEmpty)
        {
            throw new InvalidDataException("A submit operation requires manifest documents.");
        }

        InstallerManifest? installer = null;
        DefaultLocaleManifest? defaultLocale = null;
        VersionManifest? version = null;
        var locales = new List<LocaleManifest>();
        foreach (RawManifestDocument document in documents.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            string yaml = StrictUtf8.Decode(document.Content.AsSpan());
            switch (ManifestYamlReader.TryDetectType(yaml))
            {
                case ManifestType.Installer when installer is null:
                    installer = ManifestYamlReader.ReadInstaller(yaml);
                    break;
                case ManifestType.DefaultLocale when defaultLocale is null:
                    defaultLocale = ManifestYamlReader.ReadDefaultLocale(yaml);
                    break;
                case ManifestType.Version when version is null:
                    version = ManifestYamlReader.ReadVersion(yaml);
                    break;
                case ManifestType.Locale:
                    locales.Add(ManifestYamlReader.ReadLocale(yaml));
                    break;
                default:
                    throw new InvalidDataException($"Manifest set contains a duplicate, unsupported, or untyped document '{document.RepositoryPath}'.");
            }
        }

        PackageManifests manifests = new()
        {
            Installer = installer ?? throw new InvalidDataException("Installer manifest is missing."),
            DefaultLocale = defaultLocale ?? throw new InvalidDataException("Default locale manifest is missing."),
            Version = version ?? throw new InvalidDataException("Version manifest is missing."),
            Locales = locales,
        };
        PackageManifestIO.Validate(manifests);
        return new(manifests.Version.PackageIdentifier!, manifests.Version.PackageVersion!, manifests);
    }

    private static ImmutableArray<WorkflowFileChange> Diff(
        ImmutableArray<RawManifestDocument> before,
        ImmutableArray<RawManifestDocument> after)
    {
        Dictionary<string, RawManifestDocument> oldFiles = before.ToDictionary(
            static document => document.RepositoryPath,
            StringComparer.Ordinal);
        Dictionary<string, RawManifestDocument> newFiles = after.ToDictionary(
            static document => document.RepositoryPath,
            StringComparer.Ordinal);
        var changes = ImmutableArray.CreateBuilder<WorkflowFileChange>();
        foreach ((string path, RawManifestDocument document) in newFiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!oldFiles.TryGetValue(path, out RawManifestDocument? old))
            {
                changes.Add(new(PlannedChangeKind.Add, path, document.Content.AsSpan()));
            }
            else if (!old.Content.AsSpan().SequenceEqual(document.Content.AsSpan()))
            {
                changes.Add(new(PlannedChangeKind.Update, path, document.Content.AsSpan()));
            }
        }

        foreach (string path in oldFiles.Keys.Except(newFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            changes.Add(new(PlannedChangeKind.Delete, path));
        }

        return changes.ToImmutable();
    }

    private static bool InstallerHashesEqual(PackageManifests before, PackageManifests after)
    {
        string[] previous = (before.Installer.Installers ?? [])
            .Select(static installer => $"{installer.InstallerUrl}|{installer.InstallerSha256}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] current = (after.Installer.Installers ?? [])
            .Select(static installer => $"{installer.InstallerUrl}|{installer.InstallerSha256}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return previous.SequenceEqual(current, StringComparer.Ordinal);
    }

    private static PolicyEvidence MergePolicyEvidence(
        PolicyEvidence supplied,
        ImmutableArray<ArtifactSnapshot>.Builder artifacts,
        ImmutableArray<DiscoveredAsset>.Builder assets)
    {
        var dependencies = new Dictionary<string, PayloadDependencyAnalysis>(
            supplied.DependencyAnalyses,
            StringComparer.OrdinalIgnoreCase);
        foreach (ArtifactSnapshot artifact in artifacts)
        {
            if (artifact.DependencyAnalysis is not null)
            {
                dependencies[artifact.Asset.DownloadUri.AbsoluteUri] = artifact.DependencyAnalysis;
            }
        }

        return new()
        {
            HttpsUpgradeConfirmations = supplied.HttpsUpgradeConfirmations,
            ConfirmedUrls = supplied.ConfirmedUrls
                .Concat(assets.Select(static asset => asset.DownloadUri.AbsoluteUri))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ExistingDisplayVersions = supplied.ExistingDisplayVersions,
            DependencyAnalyses = dependencies,
            InstallerScopes = supplied.InstallerScopes,
            SiblingImportUrls = supplied.SiblingImportUrls,
            PipelineLogExcerpts = supplied.PipelineLogExcerpts,
            SchemaHeaderComments = supplied.SchemaHeaderComments,
            ReleaseDate = assets
                .Select(static asset => asset.ReleasePublishedAt)
                .Where(static value => value is not null)
                .OrderByDescending(static value => value)
                .Select(static value => DateOnly.FromDateTime(value!.Value.UtcDateTime))
                .FirstOrDefault(),
        };
    }

    private static ValidationReport MergeRuleFindings(
        ValidationReport validation,
        RuleRunSummary rules)
        => new(
        [
            .. validation.Findings,
            .. rules.Findings.Select(static finding => new ValidationFinding(
                $"RULE_{finding.RuleId}",
                finding.Severity switch
                {
                    RuleSeverity.Info => ValidationSeverity.Info,
                    RuleSeverity.Warning => ValidationSeverity.Warning,
                    RuleSeverity.Error => ValidationSeverity.Error,
                    _ => throw new ArgumentOutOfRangeException(nameof(rules)),
                },
                finding.Message,
                finding.Path)),
        ]);

    private static PreflightOptions PreflightOptions(WorkflowOperationRequest request)
        => new()
        {
            WarningPolicy = request.WarningPolicy,
            NetworkMode = request.NetworkValidationMode,
        };

    private static LocalOperationPlan Plan(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        ImmutableArray<WorkflowFileChange> changes,
        ImmutableArray<RawManifestDocument> before,
        ImmutableArray<RawManifestDocument> after,
        ValidationReport validation,
        RuleRunSummary rules,
        ImmutableArray<WorkflowQuestion> questions,
        IEnumerable<WorkflowAuditEntry> audit)
        => new()
        {
            Operation = operation,
            PackageIdentifier = identifier,
            PackageVersion = version,
            OutputDirectory = Path.GetFullPath(request.OutputDirectory),
            FileChanges = changes,
            BeforeDocuments = before,
            AfterDocuments = after,
            Validation = validation,
            WarningPolicy = request.WarningPolicy,
            Rules = rules,
            Questions = questions,
            ReviewApproved = request.ApproveReview,
            Audit =
            [
                .. audit
                    .OrderBy(static entry => entry.Code, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Provenance, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Message, StringComparer.Ordinal),
            ],
        };

    private static WorkflowOperationResult MissingResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version)
        => new()
        {
            Code = WorkflowResultCode.NotFound,
            Plan = Plan(
                operation,
                request,
                identifier,
                version,
                [],
                [],
                [],
                new ValidationReport(
                [
                    new("WF_NOT_FOUND", ValidationSeverity.Error, "The exact package version was not found."),
                ]),
                RuleRunSummary.Empty,
                [],
                []),
        };

    private static WorkflowOperationResult InvalidResult(
        string operation,
        WorkflowOperationRequest request,
        string message)
        => new()
        {
            Code = WorkflowResultCode.InvalidRequest,
            Plan = Plan(
                operation,
                request,
                new PackageIdentifier("Invalid.Request"),
                new PackageVersion("0"),
                [],
                [],
                [],
                new ValidationReport([new("WF_INVALID", ValidationSeverity.Error, message)]),
                RuleRunSummary.Empty,
                [],
                []),
        };

    private static WorkflowOperationResult ConflictResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        string message)
        => new()
        {
            Code = WorkflowResultCode.Conflict,
            Plan = Plan(
                operation,
                request,
                identifier,
                version,
                [],
                [],
                [],
                new ValidationReport([new("WF_CONFLICT", ValidationSeverity.Error, message)]),
                RuleRunSummary.Empty,
                [],
                []),
        };

    private static WorkflowOperationResult NoChangeResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        ImmutableArray<RawManifestDocument> before)
        => new()
        {
            Code = WorkflowResultCode.NoChanges,
            Plan = Plan(
                operation,
                request,
                identifier,
                version,
                [],
                before,
                before,
                new ValidationReport(),
                RuleRunSummary.Empty,
                [],
                [new("UPDATE_UNCHANGED_HASHES", "All selected installer URL and hash identities are unchanged.")]),
        };

    private static WorkflowOperationResult QuestionResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        ImmutableArray<WorkflowQuestion> questions,
        IEnumerable<WorkflowAuditEntry> audit)
        => new()
        {
            Code = WorkflowResultCode.QuestionsRequired,
            Plan = Plan(
                operation,
                request,
                identifier,
                version,
                [],
                [],
                [],
                new ValidationReport(),
                RuleRunSummary.Empty,
                questions,
                audit),
        };

    private sealed record ParsedRawSet(
        PackageIdentifier Identifier,
        PackageVersion Version,
        PackageManifests Manifests);
}

public sealed class NewWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        NewOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.NewAsync(request, cancellationToken);
}

public sealed class UpdateWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        UpdateOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.UpdateAsync(request, cancellationToken);
}

public sealed class RemoveWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        RemoveOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.RemoveAsync(request, cancellationToken);
}

public sealed class LocalSubmitWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        SubmitOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.SubmitAsync(request, cancellationToken);
}

public sealed class NewLocaleWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        NewLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.NewLocaleAsync(request, cancellationToken);
}

public sealed class UpdateLocaleWorkflow(LocalWorkflowEngine engine)
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        UpdateLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
        => engine.UpdateLocaleAsync(request, cancellationToken);
}
