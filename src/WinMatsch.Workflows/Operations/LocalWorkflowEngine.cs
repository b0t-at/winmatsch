using System.Collections.Immutable;
using System.Text;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;
using YamlDotNet.Core;

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
    private readonly IOverridePackStore? _overridePackStore;
    private readonly ILocalOperationLockProvider _planLocks;
    private readonly string _trustedGitHubHost;

    public LocalWorkflowEngine(
        IManifestSnapshotSource manifests,
        IWorkflowRuleRunner rules,
        IWorkflowPreflight preflight,
        IWorkflowFileTransaction transaction,
        IWorkflowReleaseSource? releases = null,
        IWorkflowArtifactProcessor? artifacts = null,
        IWorkflowClock? clock = null,
        IOverridePackStore? overridePackStore = null,
        ILocalOperationLockProvider? planLocks = null,
        string trustedGitHubHost = "github.com")
    {
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _releases = releases;
        _artifacts = artifacts;
        _clock = clock ?? new SystemWorkflowClock();
        _overridePackStore = overridePackStore;
        _planLocks = planLocks ?? new FileLocalOperationLockProvider();
        _trustedGitHubHost = string.IsNullOrWhiteSpace(trustedGitHubHost)
            ? throw new ArgumentException("A trusted GitHub web host is required.", nameof(trustedGitHubHost))
            : trustedGitHubHost;
    }

    public async Task<WorkflowOperationResult> ApplyVerifiedPlanAsync(
        WorkflowOperationRequest request,
        string expectedPlanFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlanFingerprint);
        PackageIdentifier identifier = PackageIdentifierFor(request);
        await using IAsyncDisposable packageLock = await _planLocks.AcquireAsync(
            request.OutputDirectory,
            identifier,
            cancellationToken).ConfigureAwait(false);

        WorkflowOperationRequest planningRequest = WithExecutionMode(
            request,
            WorkflowExecutionMode.Plan);
        WorkflowOperationResult current = await ExecuteCurrentAsync(
            planningRequest,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverForVerifiedApplyAsync(
                request,
                identifier,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowOperationException exception)
        {
            return current with
            {
                Code = exception.Code,
                Applied = false,
                ErrorMessage = exception.Message,
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            return current with
            {
                Code = WorkflowResultCode.ApplyFailed,
                Applied = false,
                ErrorMessage = exception.Message,
                Recovery = new(exception.Message, [], JournalRetained: true),
            };
        }

        current = await ExecuteCurrentAsync(
            planningRequest,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                current.Plan.Fingerprint,
                expectedPlanFingerprint,
                StringComparison.Ordinal))
        {
            return current with
            {
                Code = WorkflowResultCode.StalePlan,
                Applied = false,
                ErrorMessage = "The operation changed after approval; review the new plan before applying.",
            };
        }

        if (current.Plan.RequiresReview
            || current.Plan.Rules.RequiresReview && !current.Plan.ReviewApproved)
        {
            return current with
            {
                Code = WorkflowResultCode.Conflict,
                Applied = false,
                ErrorMessage = "The current review set is not bound to the approved plan fingerprint.",
            };
        }

        WorkflowResultCode code = ResultCode(current.Plan);
        bool learningOnly = code == WorkflowResultCode.NoChanges
            && current.Plan.LearnedOverride is not null
            && current.Plan.ReviewApproved;
        if (code != WorkflowResultCode.Succeeded && !learningOnly)
        {
            return current with { Applied = false };
        }

        return await CompleteAsync(
            WithExecutionMode(request, WorkflowExecutionMode.Apply),
            current.Plan,
            cancellationToken,
            expectedPlanFingerprint).ConfigureAwait(false);
    }

    private async Task RecoverForVerifiedApplyAsync(
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        CancellationToken cancellationToken)
    {
        if (_transaction is IWorkflowCoordinatedRecovery coordinatedTransaction
            && _overridePackStore is IOverridePackCoordinatedRecovery coordinatedStore)
        {
            await using IOverridePackRecoveryLease overrideLease =
                await coordinatedStore.AcquireRecoveryLeaseAsync(
                    identifier,
                    cancellationToken).ConfigureAwait(false);
            string recoveryRoot = overrideLease.PendingOutputDirectory
                ?? request.OutputDirectory;
            using IDisposable transactionLease =
                await coordinatedTransaction.RecoverAndHoldAsync(
                    recoveryRoot,
                    identifier.Value,
                    cancellationToken).ConfigureAwait(false);
            _ = await overrideLease.CompleteAfterManifestRecoveryAsync()
                .ConfigureAwait(false);
            if (!string.Equals(
                    Path.GetFullPath(recoveryRoot),
                    Path.GetFullPath(request.OutputDirectory),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                await coordinatedTransaction.RecoverAsync(
                    request.OutputDirectory,
                    identifier.Value,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (_transaction is IWorkflowFileTransactionRecovery recovery)
        {
            await recovery.RecoverAsync(
                request.OutputDirectory,
                identifier.Value,
                cancellationToken).ConfigureAwait(false);
            if (_overridePackStore is IOverridePackStoreRecovery recoveryStore)
            {
                _ = await recoveryStore.LoadAfterManifestRecoveryAsync(
                    identifier,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task<WorkflowOperationResult> NewAsync(
        NewOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PackageVersion version = PackageVersion.TryCreate(request.PackageVersion ?? "0", out PackageVersion? parsed)
            ? parsed!
            : new PackageVersion("0");
        return ExecuteSnapshotOperationAsync(
            "new",
            request,
            request.PackageIdentifier,
            version,
            recoveredOverride => CreateOrUpdateAsync(
                request,
                previous: null,
                recoveredOverride,
                cancellationToken),
            cancellationToken);
    }

    public async Task<WorkflowOperationResult> UpdateAsync(
        UpdateOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = await ResolveUpdateSourceAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.PreviousVersion is null)
        {
            string message = (_manifests as IManifestSnapshotSourceDiagnosticSource)
                    ?.GetListVersionsDiagnostic(request.PackageIdentifier)
                ?? $"Package '{request.PackageIdentifier.Value}' has no source versions in the "
                    + "output directory or configured manifest repository.";
            return InvalidResult(
                "update",
                request,
                message);
        }

        return await ExecuteSnapshotOperationAsync(
            "update",
            request,
            request.PackageIdentifier,
            request.PreviousVersion,
            recoveredOverride => UpdateCoreAsync(
                request,
                recoveredOverride,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateOperationRequest> ResolveUpdateSourceAsync(
        UpdateOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreviousVersion is not null)
        {
            return request;
        }

        ImmutableArray<PackageSnapshot> versions = await _manifests.ListVersionsAsync(
            request.OutputDirectory,
            request.PackageIdentifier,
            cancellationToken).ConfigureAwait(false);
        PackageSnapshot? latest = versions.MaxBy(static snapshot => snapshot.PackageVersion);
        return latest is null
            ? request
            : request with { PreviousVersion = latest.PackageVersion };
    }

    public async Task<ImmutableArray<PreviousInstallerEntry>> LoadPreviousInstallersAsync(
        UpdateOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreviousVersion is null)
        {
            return [];
        }

        PackageSnapshot? previous = await _manifests.LoadAsync(
            request.OutputDirectory,
            request.PackageIdentifier,
            request.PreviousVersion,
            cancellationToken).ConfigureAwait(false);
        return previous is null
            ? []
            : PreviousInstallerEntry.FromManifests(previous.Manifests);
    }

    private async Task<WorkflowOperationResult> UpdateCoreAsync(
        UpdateOperationRequest request,
        OverridePackStoreSnapshot? recoveredOverride,
        CancellationToken cancellationToken)
    {
        PackageSnapshot? previous = await _manifests.LoadAsync(
            request.OutputDirectory,
            request.PackageIdentifier,
            request.PreviousVersion!,
            cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            string? diagnostic = (_manifests as IManifestSnapshotSourceDiagnosticSource)
                ?.GetLoadDiagnostic(
                    request.PackageIdentifier,
                    request.PreviousVersion!);
            return MissingResult(
                "update",
                request,
                request.PackageIdentifier,
                request.PreviousVersion!,
                diagnostic);
        }

        if (previous.IsRemote && request.ReplacePreviousVersion)
        {
            return InvalidResult(
                "update",
                request,
                "--replace requires the source version to exist under --output; "
                + "a repository fallback is read-only.");
        }

        return await CreateOrUpdateAsync(
            request,
            previous,
            recoveredOverride,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowOperationResult> RemoveAsync(
        RemoveOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSnapshotOperationAsync(
            "remove",
            request,
            request.PackageIdentifier,
            request.PackageVersion,
            _ => RemoveCoreAsync(request, cancellationToken),
            cancellationToken);
    }

    private async Task<WorkflowOperationResult> RemoveCoreAsync(
        RemoveOperationRequest request,
        CancellationToken cancellationToken)
    {
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
                    document.RepositoryPath,
                    expectedState: ExpectedFileState.Present,
                    expectedSha256: WorkflowFileChange.Hash(document.Content.AsSpan()),
                    provenance: WorkflowChangeProvenance.ToolGenerated)),
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
        catch (Exception exception) when (
            exception is InvalidDataException
                or FormatException
                or ArgumentException
                or DecoderFallbackException
                or YamlException)
        {
            return InvalidResult("submit", request, exception.Message);
        }

        return await ExecuteSnapshotOperationAsync(
            "submit",
            request,
            parsed.Identifier,
            parsed.Version,
            _ => SubmitParsedAsync(request, parsed, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowOperationResult> SubmitParsedAsync(
        SubmitOperationRequest request,
        ParsedRawSet parsed,
        CancellationToken cancellationToken)
    {
        PackageSnapshot? before = await _manifests.LoadAsync(
            request.OutputDirectory,
            parsed.Identifier,
            parsed.Version,
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<ExistingVersionSnapshot> existingVersions = CreateExistingVersions(
            RetainedVersions(
                await _manifests.ListVersionsAsync(
                    request.OutputDirectory,
                    parsed.Identifier,
                    cancellationToken).ConfigureAwait(false),
                parsed.Version,
                update: null));
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

        using var artifactDirectory = new ArtifactDirectoryLease(
            request.ExecutionMode,
            request.ArtifactDirectory);
        var acquired = ImmutableArray.CreateBuilder<InstallerArtifact>();
        acquired.AddRange(request.InstallerArtifacts);
        if (_artifacts is not null)
        {
            int assetId = 0;
            foreach (string installerUrl in (candidate.Installer.Installers ?? [])
                         .Select(static installer => installer.InstallerUrl)
                         .Where(static url => !string.IsNullOrWhiteSpace(url))
                         .Select(static url => url!)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                if (acquired.Any(artifact => string.Equals(
                        artifact.InstallerUrl,
                        installerUrl,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                var uri = new Uri(installerUrl, UriKind.Absolute);
                ArtifactSnapshot artifact = await _artifacts.AcquireAsync(
                    new DiscoveredAsset
                    {
                        ReleaseId = 0,
                        ReleaseTag = candidate.Version.PackageVersion!.Value,
                        ReleaseName = "local submit",
                        ReleaseUri = uri,
                        IsPrerelease = false,
                        AssetId = assetId++,
                        AssetName = Path.GetFileName(uri.LocalPath),
                        DownloadUri = uri,
                        DeclaredContentType = "application/octet-stream",
                        DeclaredSize = 0,
                        AssetCreatedAt = DateTimeOffset.UnixEpoch,
                    },
                    artifactDirectory.Path,
                    cancellationToken).ConfigureAwait(false);
                acquired.Add(new InstallerArtifact(installerUrl, artifact.Download));
            }
        }

        ImmutableArray<InstallerArtifact> installerArtifacts = acquired.ToImmutable();
        ImmutableArray<WorkflowFileChange> changes = Diff(
            before?.Documents ?? [],
            after,
            toolGenerated: request.Normalize);
        ValidationReport validation = await ValidateAsync(
            request,
            before?.Documents ?? [],
            after,
            changes,
            installerArtifacts,
            existingVersions,
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
                : "User manifest bytes were preserved exactly.")],
            installerArtifacts,
            existingVersions) with
        {
            Release = request.ReleaseProvenance,
        };
        return await CompleteAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowOperationResult> NewLocaleAsync(
        NewLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSnapshotOperationAsync(
            "new-locale",
            request,
            request.PackageIdentifier,
            request.PackageVersion,
            _ => LocaleAsync(request, update: false, cancellationToken),
            cancellationToken);
    }

    public Task<WorkflowOperationResult> UpdateLocaleAsync(
        UpdateLocaleOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSnapshotOperationAsync(
            "update-locale",
            request,
            request.PackageIdentifier,
            request.PackageVersion,
            _ => LocaleAsync(request, update: true, cancellationToken),
            cancellationToken);
    }

    private async Task<WorkflowOperationResult> CreateOrUpdateAsync(
        WorkflowOperationRequest operationRequest,
        PackageSnapshot? previous,
        OverridePackStoreSnapshot? recoveredOverride,
        CancellationToken cancellationToken)
    {
        bool isUpdate = previous is not null;
        NewOperationRequest? create = operationRequest as NewOperationRequest;
        UpdateOperationRequest? update = operationRequest as UpdateOperationRequest;
        OverridePackSet explicitOverridePacks = operationRequest.OverridePacks;
        PackageIdentifier identifier = create?.PackageIdentifier
            ?? update?.PackageIdentifier
            ?? throw new ArgumentException("Unsupported create/update request.", nameof(operationRequest));
        OverridePackStoreSnapshot learnedSnapshot;
        if (recoveredOverride is not null)
        {
            learnedSnapshot = recoveredOverride;
        }
        else if (_overridePackStore is null)
        {
            learnedSnapshot = new(null, null, null);
        }
        else
        {
            learnedSnapshot = await _overridePackStore.LoadAsync(
                identifier,
                allowRecoveryWrites: operationRequest.ExecutionMode == WorkflowExecutionMode.Apply,
                cancellationToken).ConfigureAwait(false);
        }

        ImmutableArray<WorkflowAuditEntry> learnedStoreAudit =
        [
            .. learnedSnapshot.Pack is null
                ? []
                : new[]
                {
                    new WorkflowAuditEntry(
                        "LEARNED_OVERRIDE_ACTIVE",
                        identifier.Value,
                        learnedSnapshot.ActivatedFromRecovery
                            ? "Activated from the durable manifest/override transaction journal."
                            : learnedSnapshot.ContentSha256),
                },
            .. learnedSnapshot.PendingActivation
                ? new[]
                {
                    new WorkflowAuditEntry(
                        "LEARNED_OVERRIDE_PENDING",
                        identifier.Value,
                        "Approved override is inactive and retained for apply-mode recovery."),
                }
                : [],
            .. learnedSnapshot.RecoveredFromBackup
                ? new[]
                {
                    new WorkflowAuditEntry(
                        "LEARNED_OVERRIDE_BACKUP_RECOVERED",
                        identifier.Value,
                        learnedSnapshot.QuarantinedCorruptPrimary
                            ? "The active override pack was corrupt; the last verified backup is in use and the corrupt primary was quarantined."
                            : "The active override pack is corrupt; Plan mode is using the last verified backup without modifying the store."),
                }
                : [],
        ];
        if (learnedSnapshot.Pack is not null)
        {
            operationRequest = WithOverridePacks(
                operationRequest,
                OverridePackSet.Compose(
                    new OverridePackSet([learnedSnapshot.Pack]),
                    explicitOverridePacks));
            create = operationRequest as NewOperationRequest;
            update = operationRequest as UpdateOperationRequest;
        }
        ImmutableArray<PackageSnapshot> packageVersions = await _manifests.ListVersionsAsync(
            operationRequest.OutputDirectory,
            identifier,
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<DiscoveredAsset> assets = create?.Assets ?? update!.Assets;
        ImmutableArray<DiscoveredAsset> continuityCandidates =
            update?.ReleaseAssetCandidates ?? [];
        ImmutableArray<AssetMappingCompletion> preparedCompletions =
            update?.ReleaseAssetCompletions ?? [];
        ImmutableArray<AssetMappingBinding> preparedBindings =
            update?.ReleaseAssetBindings ?? [];
        ReleaseRequest release = create?.Release ?? update!.Release;
        if (assets.IsEmpty && _releases is not null)
        {
            WorkflowReleaseAssets discovered = await _releases
                .DiscoverAsync(identifier, release, cancellationToken)
                .ConfigureAwait(false);
            assets = discovered.Selected;
            continuityCandidates = discovered.ContinuityCandidates;
        }

        if (assets.IsEmpty)
        {
            return InvalidResult(isUpdate ? "update" : "new", operationRequest, "No Windows release assets were supplied or discovered.");
        }

        string? requestedVersion = create?.PackageVersion ?? update?.PackageVersion;
        ImmutableArray<PreviousInstallerEntry> previousInstallers = previous is null
            ? []
            : PreviousInstallerEntry.FromManifests(previous.Manifests);
        ImmutableArray<DiscoveredAsset> selectedAssets = assets;
        PackageVersionResolution continuityVersion = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = identifier,
            ExplicitPackageVersion = requestedVersion,
            OverridePacks = operationRequest.OverridePacks,
            Assets = assets,
        });
        AssetMappingContinuityPlan continuity =
            isUpdate && continuityVersion.Version is { } targetVersion
                ? AssetMappingPlanner.CompleteReleaseAssetContinuity(
                    assets,
                    continuityCandidates,
                    previousInstallers,
                    targetVersion)
                : new([], []);
        ImmutableArray<AssetMappingCompletion> discoveredCompletions = continuity.Completions;
        ImmutableArray<AssetMappingCompletion> completions =
        [
            .. preparedCompletions
                .Concat(discoveredCompletions)
                .DistinctBy(static completion => completion.PreviousPosition)
                .OrderBy(static completion => completion.PreviousPosition),
        ];
        ImmutableArray<AssetMappingBinding> bindings =
        [
            .. preparedBindings
                .Concat(continuity.Bindings)
                .DistinctBy(static binding => binding.PreviousPosition)
                .OrderBy(static binding => binding.PreviousPosition),
        ];
        if (!discoveredCompletions.IsEmpty)
        {
            assets =
            [
                .. assets
                    .Concat(discoveredCompletions.Select(static completion => completion.Asset))
                    .DistinctBy(static asset => asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal),
            ];
        }

        HashSet<string> callerUrls = release.InstallerUrls
            .Select(static uri => uri.AbsoluteUri)
            .ToHashSet(StringComparer.Ordinal);
        ImmutableArray<WorkflowAuditEntry> assetInputAudit =
        [
            .. selectedAssets
                .Where(asset => callerUrls.Contains(asset.DownloadUri.AbsoluteUri))
                .Select(static asset => new WorkflowAuditEntry(
                    "MAP_SUPPLIED",
                    asset.DownloadUri.AbsoluteUri,
                    "caller-supplied URL")),
            .. completions.Select(static completion => new WorkflowAuditEntry(
                "MAP_COMPLETED",
                $"Previous installer {completion.PreviousPosition} mapped to {completion.Asset.DownloadUri.AbsoluteUri}.",
                completion.Provenance)),
        ];

        ImmutableArray<WorkflowAuditEntry> releaseMetadataAudit = [];
        if (create is not null && _releases is IWorkflowReleaseMetadataSource metadataSource)
        {
            WorkflowReleaseMetadata releaseMetadata = await metadataSource.DiscoverMetadataAsync(
                identifier,
                release,
                assets,
                cancellationToken).ConfigureAwait(false);
            create = create with
            {
                Locale = MergeReleaseMetadata(create.Locale, releaseMetadata.Metadata),
            };
            operationRequest = create;
            releaseMetadataAudit =
            [
                .. releaseMetadata.Metadata.Provenance.Select(pair => new WorkflowAuditEntry(
                    "RELEASE_METADATA",
                    pair.Key,
                    pair.Value)),
                new(
                    "REPOSITORY_METADATA_STATUS",
                    releaseMetadata.Availability.ToString(),
                    releaseMetadata.Diagnostic),
            ];
        }

        using var artifactDirectory = new ArtifactDirectoryLease(
            operationRequest.ExecutionMode,
            create?.ArtifactDirectory ?? update?.ArtifactDirectory,
            update?.UsePreparedArtifactDirectory ?? false);
        var artifactSnapshots = ImmutableArray.CreateBuilder<ArtifactSnapshot>();
        var installerArtifacts = ImmutableArray.CreateBuilder<InstallerArtifact>();
        installerArtifacts.AddRange(create?.InstallerArtifacts ?? update!.InstallerArtifacts);
        var enrichedAssets = ImmutableArray.CreateBuilder<DiscoveredAsset>();
        foreach (DiscoveredAsset asset in assets.OrderBy(static item => item.DownloadUri.AbsoluteUri, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Content is not null && asset.Analysis is not null)
            {
                enrichedAssets.Add(asset);
                bool hasArtifact = installerArtifacts.Any(artifact =>
                    string.Equals(
                        artifact.InstallerUrl,
                        asset.DownloadUri.AbsoluteUri,
                        StringComparison.Ordinal));
                if (operationRequest.ExecutionMode == WorkflowExecutionMode.Apply
                    && !hasArtifact
                    && _artifacts is not null)
                {
                    ArtifactSnapshot revalidated = await _artifacts.AcquireAsync(
                        asset,
                        artifactDirectory.Path,
                        cancellationToken).ConfigureAwait(false);
                    artifactSnapshots.Add(revalidated);
                    installerArtifacts.Add(new(asset.DownloadUri.AbsoluteUri, revalidated.Download));
                }

                continue;
            }

            if (_artifacts is null)
            {
                return InvalidResult(
                    isUpdate ? "update" : "new",
                    operationRequest,
                    $"Asset '{asset.DownloadUri}' requires download and analysis evidence.");
            }

            ArtifactSnapshot snapshot = await _artifacts.AcquireAsync(
                asset,
                artifactDirectory.Path,
                cancellationToken).ConfigureAwait(false);
            artifactSnapshots.Add(snapshot);
            installerArtifacts.Add(new(snapshot.Asset.DownloadUri.AbsoluteUri, snapshot.Download));
            enrichedAssets.Add(snapshot.Asset);
        }

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
            PreviousInstallers = previousInstallers,
            AssetBindings = bindings,
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
                assetInputAudit.Concat(mapping.Diagnostics.Select(static item =>
                    new WorkflowAuditEntry(item.Code, item.Message, item.AssetUrl))));
        }

        PackageVersion newVersion = versionResolution.Version;
        if (previous?.IsRemote == true && previous.PackageVersion.Equals(newVersion))
        {
            return InvalidResult(
                "update",
                operationRequest,
                "Updating a package version in place requires that version to exist under "
                + "--output; a repository fallback is read-only.");
        }

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
            candidate.Installer.Installers = CreateInstallers(
                mapping,
                candidate.Installer.Installers,
                previousInstallers);
            ClearStaleRootNestedState(candidate.Installer);
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
            enrichedAssets,
            RetainedVersions(packageVersions, newVersion, update));
        WorkflowRuleResult rules = RunRules(
            operationRequest,
            candidate,
            previous,
            installerEvidence,
            policyEvidence);
        candidate = rules.Manifests;
        ImmutableArray<RawManifestDocument> beforeDocuments = existing?.Documents ?? [];
        ImmutableArray<RawManifestDocument> after = Serialize(
            candidate,
            operationRequest.CreatedWith,
            beforeDocuments);
        ImmutableArray<WorkflowFileChange> changes = Diff(
            beforeDocuments,
            after,
            toolGenerated: true);
        if (update?.ReplacePreviousVersion == true
            && !string.Equals(previous!.PackageVersion.Value, newVersion.Value, StringComparison.Ordinal))
        {
            changes =
            [
                .. previous.Documents.Select(static document =>
                    new WorkflowFileChange(
                        PlannedChangeKind.Delete,
                        document.RepositoryPath,
                        expectedState: ExpectedFileState.Present,
                        expectedSha256: WorkflowFileChange.Hash(document.Content.AsSpan()),
                        provenance: WorkflowChangeProvenance.ToolGenerated)),
                .. changes,
            ];
            beforeDocuments = [.. previous.Documents, .. beforeDocuments];
        }

        ImmutableArray<PackageSnapshot> retainedVersions = RetainedVersions(
            packageVersions,
            newVersion,
            update);
        ImmutableArray<ExistingVersionSnapshot> existingVersionEvidence =
            CreateExistingVersions(retainedVersions);

        ValidationReport validation = await ValidateAsync(
            operationRequest,
            beforeDocuments,
            after,
            changes,
            installerArtifacts.ToImmutable(),
            existingVersionEvidence,
            cancellationToken).ConfigureAwait(false);
        validation = MergeRuleFindings(validation, rules.Summary);
        validation = AddStaleLearnedOverrideFinding(validation, rules.Summary);
        validation = AddLearnedStoreFindings(validation, learnedSnapshot);
        WorkflowReleaseProvenance? releaseProvenance = CreateReleaseProvenance(enrichedAssets);
        LocalOperationPlan reviewedPlan = Plan(
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
            [],
            installerArtifacts.ToImmutable(),
            existingVersionEvidence,
            reviewApproved: false) with
        {
            Release = releaseProvenance,
        };
        bool reviewApproved = ReviewApproval.Matches(
            operationRequest,
            rules.Summary,
            LocalOperationPlanFingerprint.CreateApprovalFingerprint(reviewedPlan));
        LearnedOverridePlan? learnedOverride = null;
        if (!rules.Summary.Reviews.IsEmpty && reviewApproved)
        {
            if (_overridePackStore is null
                || previous?.OriginalBotSubmission is null)
            {
                validation = AddValidationFinding(validation, new ValidationFinding(
                    "WF_LEARNED_OVERRIDE_UNAVAILABLE",
                    ValidationSeverity.Error,
                    "Approved human corrections require a configured override store and the original bot submission."));
            }
            else
            {
                try
                {
                    bool scopeLayoutChanged =
                        (previous.OriginalBotSubmission.Installer.Scope is null)
                        != (previous.Manifests.Installer.Scope is null);
                    bool layoutReviewApproved = rules.Summary.Reviews.Any(
                        static review => string.Equals(
                            review.FieldPath,
                            "Scope",
                            StringComparison.Ordinal));
                    bool learnedScopeLayout = scopeLayoutChanged && layoutReviewApproved;
                    HumanCorrectionReview[] fieldReviews =
                    [
                        .. rules.Summary.Reviews.Where(review =>
                            !learnedScopeLayout
                            || previous.Manifests.Installer.Scope is not null
                            || !string.Equals(review.FieldPath, "Scope", StringComparison.Ordinal)),
                    ];
                    ImmutableArray<LearnedFieldOverride> approved = LearnedOverrideBuilder.Create(
                        previous.OriginalBotSubmission,
                        previous.Manifests,
                        fieldReviews);
                    ScopeLayoutOverride? scopeLayout = learnedScopeLayout
                        ? previous.Manifests.Installer.Scope is null
                            ? ScopeLayoutOverride.PerInstaller
                            : ScopeLayoutOverride.Root
                        : null;
                    ScopeLayoutOverride? previousScopeLayout = learnedScopeLayout
                        ? previous.OriginalBotSubmission.Installer.Scope is null
                            ? ScopeLayoutOverride.PerInstaller
                            : ScopeLayoutOverride.Root
                        : null;
                    var proposed = new OverridePack
                    {
                        PackageIdentifier = identifier,
                        LearnedFields = approved,
                        ScopeLayout = scopeLayout,
                    };
                    OverridePack merged = learnedSnapshot.Pack is null
                        ? proposed
                        : OverridePackSet.Merge(learnedSnapshot.Pack, proposed);
                    learnedOverride = new(
                        merged,
                        learnedSnapshot.ContentSha256,
                        learnedSnapshot.FormatVersion,
                        approved,
                        scopeLayout,
                        previousScopeLayout,
                        learnedSnapshot.Pack);
                    if (!changes.IsEmpty)
                    {
                        OverridePackSet approvedPacks = OverridePackSet.Compose(
                            new OverridePackSet([merged]),
                            explicitOverridePacks);
                        WorkflowRuleResult approvedRules = ApplyApprovedOverride(
                            operationRequest,
                            candidate,
                            previous,
                            installerEvidence,
                            approvedPacks);
                        candidate = approvedRules.Manifests;
                        beforeDocuments = existing?.Documents ?? [];
                        after = Serialize(
                            candidate,
                            operationRequest.CreatedWith,
                            beforeDocuments);
                        changes = Diff(
                            beforeDocuments,
                            after,
                            toolGenerated: true);
                        if (update?.ReplacePreviousVersion == true
                            && !string.Equals(
                                previous!.PackageVersion.Value,
                                newVersion.Value,
                                StringComparison.Ordinal))
                        {
                            changes =
                            [
                                .. previous.Documents.Select(static document =>
                                    new WorkflowFileChange(
                                        PlannedChangeKind.Delete,
                                        document.RepositoryPath,
                                        expectedState: ExpectedFileState.Present,
                                        expectedSha256: WorkflowFileChange.Hash(document.Content.AsSpan()),
                                        provenance: WorkflowChangeProvenance.ToolGenerated)),
                                .. changes,
                            ];
                            beforeDocuments = [.. previous.Documents, .. beforeDocuments];
                        }

                        validation = await ValidateAsync(
                            operationRequest,
                            beforeDocuments,
                            after,
                            changes,
                            installerArtifacts.ToImmutable(),
                            existingVersionEvidence,
                            cancellationToken).ConfigureAwait(false);
                        validation = MergeRuleFindings(validation, rules.Summary);
                        validation = MergeRuleFindings(validation, approvedRules.Summary);
                        validation = AddLearnedStoreFindings(validation, learnedSnapshot);
                        if (approvedRules.Summary.Findings.Any(static finding =>
                                string.Equals(
                                    finding.RuleId,
                                    RuleIds.ApplyOverridePackFields,
                                    StringComparison.Ordinal)
                                && finding.Severity != RuleSeverity.Info)
                            || !approvedRules.Summary.Reviews.IsEmpty)
                        {
                            validation = AddValidationFinding(validation, new ValidationFinding(
                                "WF_LEARNED_OVERRIDE_APPLY_FAILED",
                                ValidationSeverity.Error,
                                "The approved learned override could not be applied to the reviewed generated values."));
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException)
                {
                    validation = AddValidationFinding(validation, new ValidationFinding(
                        "WF_LEARNED_OVERRIDE_UNSAFE",
                        ValidationSeverity.Error,
                        exception.Message));
                }
            }
        }
        ImmutableArray<WorkflowAuditEntry> audit =
        [
            new("VERSION", $"Resolved package version {newVersion.Value}.", versionResolution.Source?.ToString()),
            .. previous is null
                ? []
                : new[]
                {
                    new WorkflowAuditEntry(
                        "UPDATE_SOURCE_VERSION",
                        previous.PackageVersion.Value),
                },
            .. enrichedAssets.Select(static asset => new WorkflowAuditEntry(
                "RELEASE_ASSET",
                asset.DownloadUri.AbsoluteUri,
                $"{asset.ReleaseUri.AbsoluteUri}|{asset.ReleaseTag}|{asset.ReleasePublishedAt:O}")),
            .. mapping.Decisions.Select(static decision => new WorkflowAuditEntry(
                $"MAP_{decision.Kind.ToString().ToUpperInvariant()}",
                decision.Reason,
                decision.Installer?.Url.AbsoluteUri)),
            .. assetInputAudit,
            .. releaseMetadataAudit,
            .. learnedStoreAudit,
            .. isUpdate && changes.IsEmpty
                ? new[]
                {
                    new WorkflowAuditEntry(
                        "UPDATE_NO_CHANGES",
                        "The complete generated manifest set is byte-identical to the existing set."),
                }
                : [],
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
            audit,
            installerArtifacts.ToImmutable(),
            existingVersionEvidence,
            reviewApproved) with
        {
            Release = releaseProvenance,
            LearnedOverride = learnedOverride,
            LearnedOverrideFingerprint = learnedOverride is null
                ? null
                : LocalOperationPlanFingerprint.CreateComponent(learnedOverride),
        };
        if (learnedOverride is not null)
        {
            plan = plan with
            {
                Audit =
                [
                    .. plan.Audit,
                    new(
                        "LEARNED_OVERRIDE_STAGED",
                        identifier.Value,
                        $"{learnedOverride.ApprovedFields.Length} approved field correction(s); activation follows manifest commit."),
                ],
            };
        }

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
        ImmutableArray<ExistingVersionSnapshot> existingVersions = CreateExistingVersions(
            RetainedVersions(
                await _manifests.ListVersionsAsync(
                    operationRequest.OutputDirectory,
                    identifier,
                    cancellationToken).ConfigureAwait(false),
                version,
                update: null));

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
        ImmutableArray<RawManifestDocument> serialized = Serialize(
            candidate,
            operationRequest.CreatedWith,
            snapshot.Documents);
        LanguageTag outputLocale = locale.PackageLocale!;
        string localeFile = $"{ManifestPaths.GetVersionDirectory(identifier, version)}/{ManifestPaths.GetLocaleFileName(identifier, outputLocale)}";
        RawManifestDocument changedLocale = serialized.Single(document =>
            string.Equals(document.RepositoryPath, localeFile, StringComparison.Ordinal));
        ImmutableArray<RawManifestDocument> after =
        [
            .. snapshot.Documents
                .Where(document => !string.Equals(document.RepositoryPath, localeFile, StringComparison.Ordinal))
                .Append(changedLocale)
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
        ImmutableArray<WorkflowFileChange> changes = Diff(
            snapshot.Documents,
            after,
            toolGenerated: true);
        ValidationReport validation = await ValidateAsync(
            operationRequest,
            snapshot.Documents,
            after,
            changes,
            [],
            existingVersions,
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
            [new("LOCALE_EXACT", $"{(update ? "Updated" : "Added")} locale {metadata.PackageLocale.Value}.", localeFile)],
            existingVersions: existingVersions);
        return await CompleteAsync(operationRequest, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ValidationReport> ValidateAsync(
        WorkflowOperationRequest request,
        ImmutableArray<RawManifestDocument> before,
        ImmutableArray<RawManifestDocument> after,
        ImmutableArray<WorkflowFileChange> changes,
        ImmutableArray<InstallerArtifact> artifacts,
        ImmutableArray<ExistingVersionSnapshot> existingVersions,
        CancellationToken cancellationToken)
        => await _preflight.ValidateAsync(
            new()
            {
                BeforeDocuments = before,
                AfterDocuments = after,
                Changes = changes,
                InstallerArtifacts = artifacts,
                ExistingVersions = existingVersions,
                Options = PreflightOptions(request),
            },
            cancellationToken).ConfigureAwait(false);

    private async Task<WorkflowOperationResult> CompleteAsync(
        WorkflowOperationRequest request,
        LocalOperationPlan plan,
        CancellationToken cancellationToken,
        string? expectedPlanFingerprint = null)
    {
        WorkflowResultCode code = ResultCode(plan);
        bool learningOnly = code == WorkflowResultCode.NoChanges
            && plan.LearnedOverride is not null
            && plan.ReviewApproved;
        if (request.ExecutionMode == WorkflowExecutionMode.Plan
            || code != WorkflowResultCode.Succeeded && !learningOnly)
        {
            return new() { Code = code, Plan = plan, Applied = false };
        }

        OverridePackWriteResult? persisted = null;
        try
        {
            async Task ApplyBoundaryAsync(CancellationToken token)
            {
                IOverridePackWriteStage? stage = null;
                if (plan.LearnedOverride is { } learnedOverride)
                {
                    stage = await _overridePackStore!.StageAsync(
                        new(
                            plan.PackageIdentifier,
                            learnedOverride.Pack,
                            learnedOverride.ExpectedContentSha256,
                            learnedOverride.ExpectedFormatVersion,
                            plan.FileChanges.IsEmpty
                                ? null
                                : request.OutputDirectory,
                            plan.FileChanges),
                        token).ConfigureAwait(false);
                }

                try
                {
                    await _transaction.ApplyAsync(
                        request.OutputDirectory,
                        plan.PackageIdentifier.Value,
                        plan.FileChanges,
                        token).ConfigureAwait(false);
                }
                catch (WorkflowCommittedProvenanceException provenanceException)
                {
                    if (stage is not null)
                    {
                        try
                        {
                            await stage.RetainForRecoveryAsync().ConfigureAwait(false);
                        }
                        catch (Exception retentionException)
                        {
                            throw new WorkflowCommittedLearnedOverrideException(
                                "The manifests committed, but provenance finalization failed and the approved learned override recovery lock could not be released.",
                                new AggregateException(provenanceException, retentionException));
                        }
                    }

                    throw;
                }
                catch (WorkflowCommittedException)
                {
                    if (stage is not null)
                    {
                        persisted = await ActivateLearnedOverrideAsync(stage)
                            .ConfigureAwait(false);
                    }

                    throw;
                }
                catch (Exception primaryException)
                {
                    if (stage is not null)
                    {
                        await AbortLearnedOverrideAsync(
                            stage,
                            primaryException).ConfigureAwait(false);
                    }

                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(primaryException)
                        .Throw();
                    throw;
                }

                if (stage is not null)
                {
                    persisted = await ActivateLearnedOverrideAsync(stage)
                        .ConfigureAwait(false);
                }
            }

            ValidationReport finalValidation;
            if (expectedPlanFingerprint is not null)
            {
                if (_preflight is not IWorkflowVerifiedPreflight verifiedPreflight)
                {
                    throw new WorkflowOperationException(
                        WorkflowResultCode.Conflict,
                        "The configured preflight cannot enforce the verified apply boundary.");
                }

                finalValidation = await verifiedPreflight.ExecuteVerifiedAsync(
                    plan.Preflight,
                    async (boundaryValidation, token) =>
                    {
                        ValidationReport completeValidation = MergeRuleFindings(
                            boundaryValidation,
                            plan.Rules);
                        LocalOperationPlan boundaryPlan = plan with
                        {
                            Validation = completeValidation,
                            ValidationFingerprint =
                                LocalOperationPlanFingerprint.CreateComponent(
                                    completeValidation.Findings),
                        };
                        if (!string.Equals(
                                boundaryPlan.Fingerprint,
                                expectedPlanFingerprint,
                                StringComparison.Ordinal))
                        {
                            throw new WorkflowOperationException(
                                WorkflowResultCode.StalePlan,
                                "Final preflight changed the approved operation plan.");
                        }

                        await ApplyBoundaryAsync(token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                finalValidation = await _preflight.ExecuteAsync(
                    plan.Preflight,
                    ApplyBoundaryAsync,
                    cancellationToken).ConfigureAwait(false);
            }
            finalValidation = MergeRuleFindings(finalValidation, plan.Rules);
            if (persisted is not null && plan.LearnedOverride is not null)
            {
                plan = AddLearnedOverrideAudit(plan, plan.LearnedOverride, persisted);
            }

            LocalOperationPlan finalPlan = plan with
            {
                Validation = finalValidation,
                ValidationFingerprint =
                    LocalOperationPlanFingerprint.CreateComponent(finalValidation.Findings),
            };
            return finalValidation.CanProceed(plan.WarningPolicy)
                ? new()
                {
                    Code = learningOnly
                        ? WorkflowResultCode.NoChanges
                        : WorkflowResultCode.Succeeded,
                    Plan = finalPlan,
                    Applied = true,
                }
                : new() { Code = WorkflowResultCode.ValidationFailed, Plan = finalPlan, Applied = false };
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
        catch (WorkflowCommittedException exception)
        {
            if (persisted is not null && plan.LearnedOverride is not null)
            {
                plan = AddLearnedOverrideAudit(plan, plan.LearnedOverride, persisted);
            }

            return new()
            {
                Code = WorkflowResultCode.Succeeded,
                Plan = plan,
                Applied = true,
                ErrorMessage = exception.Message,
            };
        }
        catch (WorkflowRecoveryException exception)
        {
            return new()
            {
                Code = WorkflowResultCode.ApplyFailed,
                Plan = plan,
                Applied = false,
                ErrorMessage = exception.PrimaryException.Message,
                Recovery = new(
                    exception.PrimaryException.Message,
                    [.. exception.RecoveryExceptions.Select(static failure => failure.Message)],
                    exception.JournalRetained),
            };
        }

        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new()
            {
                Code = WorkflowResultCode.ApplyFailed,
                Plan = plan,
                Applied = false,
                ErrorMessage = exception.Message,
                Recovery = new(exception.Message, [], JournalRetained: false),
            };
        }
    }

    private static LocalOperationPlan AddLearnedOverrideAudit(
        LocalOperationPlan plan,
        LearnedOverridePlan learnedOverride,
        OverridePackWriteResult persisted)
        => plan with
        {
            Audit =
            [
                .. plan.Audit,
                .. learnedOverride.ApprovedFields.Select(field => new WorkflowAuditEntry(
                    "LEARNED_OVERRIDE_PERSISTED",
                    $"{field.DocumentKey}:{field.SemanticPath}",
                    $"{field.BotValueSha256}->{field.ValueSha256}|{field.SourceFingerprint}|{field.Source}")),
                .. learnedOverride.ApprovedScopeLayout is { } layout
                    ? new[]
                    {
                        new WorkflowAuditEntry(
                            "LEARNED_SCOPE_LAYOUT_PERSISTED",
                            layout.ToString(),
                            $"{learnedOverride.PreviousScopeLayout}->{layout}|approved merged-manifest scope layout"),
                    }
                    : [],
                new(
                    "LEARNED_OVERRIDE_WRITE",
                    persisted.Path,
                    $"{persisted.BeforeSha256 ?? "<absent>"}->{persisted.AfterSha256}"),
                .. persisted.Warning is null
                    ? []
                    : new[]
                    {
                        new WorkflowAuditEntry(
                            "LEARNED_OVERRIDE_CLEANUP_PENDING",
                            persisted.Warning,
                            persisted.RecoveryRetained ? "recovery-artifacts-retained" : null),
                    },
            ],
        };

    private static async Task<OverridePackWriteResult> ActivateLearnedOverrideAsync(
        IOverridePackWriteStage stage)
    {
        try
        {
            await stage.MarkManifestCommittedAsync().ConfigureAwait(false);
            return await stage.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception commitException)
        {
            Exception failure = await RetainStageFailureAsync(
                stage,
                commitException).ConfigureAwait(false);
            throw new WorkflowCommittedLearnedOverrideException(
                "The manifest transaction committed, but its approved learned override remains inactive and retained for automatic recovery.",
                failure);
        }
    }

    private static async Task AbortLearnedOverrideAsync(
        IOverridePackWriteStage stage,
        Exception primaryException)
    {
        try
        {
            await stage.AbortAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            throw new WorkflowRecoveryException(
                "The local manifest transaction failed and learned-override cleanup was incomplete.",
                primaryException,
                rollbackException: null,
                cleanupException,
                stage.RecoveryRetained);
        }
    }

    private static async Task<Exception> RetainStageFailureAsync(
        IOverridePackWriteStage stage,
        Exception commitException)
    {
        try
        {
            await stage.RetainForRecoveryAsync().ConfigureAwait(false);
            return commitException;
        }
        catch (Exception retentionException)
        {
            return new AggregateException(commitException, retentionException);
        }
    }

    private async Task<WorkflowOperationResult> ExecuteSnapshotOperationAsync(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        Func<OverridePackStoreSnapshot?, Task<WorkflowOperationResult>> action,
        CancellationToken cancellationToken)
    {
        OverridePackStoreSnapshot? recoveredOverride = null;
        try
        {
            if (request.ExecutionMode == WorkflowExecutionMode.Apply
                && _transaction is IWorkflowCoordinatedRecovery coordinatedTransaction
                && _overridePackStore is IOverridePackCoordinatedRecovery coordinatedStore)
            {
                await using IOverridePackRecoveryLease overrideLease =
                    await coordinatedStore.AcquireRecoveryLeaseAsync(
                        identifier,
                        cancellationToken).ConfigureAwait(false);
                string recoveryRoot = overrideLease.PendingOutputDirectory
                    ?? request.OutputDirectory;
                using IDisposable transactionLease =
                    await coordinatedTransaction.RecoverAndHoldAsync(
                        recoveryRoot,
                        identifier.Value,
                        cancellationToken).ConfigureAwait(false);
                recoveredOverride = await overrideLease
                    .CompleteAfterManifestRecoveryAsync()
                    .ConfigureAwait(false);
                if (!string.Equals(
                        Path.GetFullPath(recoveryRoot),
                        Path.GetFullPath(request.OutputDirectory),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    await coordinatedTransaction.RecoverAsync(
                        request.OutputDirectory,
                        identifier.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else if (request.ExecutionMode == WorkflowExecutionMode.Apply
                && _transaction is IWorkflowFileTransactionRecovery recovery)
            {
                await recovery.RecoverAsync(
                    request.OutputDirectory,
                    identifier.Value,
                    cancellationToken).ConfigureAwait(false);
                if (_overridePackStore is IOverridePackStoreRecovery recoveryStore)
                {
                    recoveredOverride = await recoveryStore.LoadAfterManifestRecoveryAsync(
                        identifier,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OverridePackStoreRecoveryException exception)
        {
            return RecoveryResult(
                operation,
                request,
                identifier,
                version,
                exception,
                [],
                exception.JournalRetained);
        }
        catch (WorkflowRecoveryException exception)
        {
            return RecoveryResult(
                operation,
                request,
                identifier,
                version,
                exception.PrimaryException,
                [.. exception.RecoveryExceptions],
                exception.JournalRetained);
        }
        catch (WorkflowOperationException exception)
        {
            return OperationFailureResult(
                operation,
                request,
                identifier,
                version,
                exception);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            return RecoveryResult(
                operation,
                request,
                identifier,
                version,
                exception,
                [],
                journalRetained: true);
        }

        try
        {
            return await action(recoveredOverride).ConfigureAwait(false);
        }
        catch (OverridePackStoreRecoveryException exception)
        {
            return RecoveryResult(
                operation,
                request,
                identifier,
                version,
                exception,
                [],
                exception.JournalRetained);
        }
        catch (WorkflowRecoveryException exception)
        {
            return RecoveryResult(
                operation,
                request,
                identifier,
                version,
                exception.PrimaryException,
                [.. exception.RecoveryExceptions],
                exception.JournalRetained);
        }
        catch (WorkflowOperationException exception)
        {
            return OperationFailureResult(
                operation,
                request,
                identifier,
                version,
                exception);
        }
        catch (ZipAnalysisException exception)
        {
            return AnalysisFailureResult(
                operation,
                request,
                identifier,
                version,
                exception);
        }
    }

    private static WorkflowOperationResult OperationFailureResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        WorkflowOperationException exception)
        => new()
        {
            Code = exception.Code,
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
                    new(
                        exception.Code == WorkflowResultCode.Conflict
                            ? "WF_CONFLICT"
                            : "WF_OPERATION_FAILED",
                        ValidationSeverity.Error,
                        exception.Message),
                ]),
                RuleRunSummary.Empty,
                [],
                []),
            Applied = false,
            ErrorMessage = exception.Message,
        };

    private static WorkflowOperationResult AnalysisFailureResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        ZipAnalysisException exception)
        => new()
        {
            Code = WorkflowResultCode.ValidationFailed,
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
                    new(
                        exception.Diagnostic.Code,
                        ValidationSeverity.Error,
                        exception.Diagnostic.Message,
                        exception.EntryPath),
                ]),
                RuleRunSummary.Empty,
                [],
                []),
            Applied = false,
            ErrorMessage = exception.Message,
        };

    private static WorkflowOperationResult RecoveryResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        Exception primary,
        ImmutableArray<Exception> recoveryErrors,
        bool journalRetained)
        => new()
        {
            Code = WorkflowResultCode.ApplyFailed,
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
                    new(
                        "WF_RECOVERY_REQUIRED",
                        ValidationSeverity.Error,
                        primary.Message),
                ]),
                RuleRunSummary.Empty,
                [],
                []),
            Applied = false,
            ErrorMessage = primary.Message,
            Recovery = new(
                primary.Message,
                [.. recoveryErrors.Select(static failure => failure.Message)],
                journalRetained),
        };

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

    private static WorkflowOperationRequest WithOverridePacks(
        WorkflowOperationRequest request,
        OverridePackSet overridePacks)
        => request switch
        {
            NewOperationRequest value => value with { OverridePacks = overridePacks },
            UpdateOperationRequest value => value with { OverridePacks = overridePacks },
            RemoveOperationRequest value => value with { OverridePacks = overridePacks },
            SubmitOperationRequest value => value with { OverridePacks = overridePacks },
            NewLocaleOperationRequest value => value with { OverridePacks = overridePacks },
            UpdateLocaleOperationRequest value => value with { OverridePacks = overridePacks },
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
        };

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

    private static WorkflowRuleResult ApplyApprovedOverride(
        WorkflowOperationRequest request,
        PackageManifests candidate,
        PackageSnapshot? previous,
        ImmutableArray<InstallerEvidence> installerEvidence,
        OverridePackSet overridePacks)
    {
        var context = new ManifestContext
        {
            Manifests = candidate,
            Previous = previous?.Manifests,
            OriginalBotSubmission = previous?.OriginalBotSubmission,
            Evidence = installerEvidence,
            Options = new RuleOptions { Explain = request.ExplainRules },
        };
        RulePipeline pipeline = RulePipeline.Create(
            [new ApplyOverridePackFieldsRule(overridePacks)],
            new RuleRuntimeConfiguration(
                commandOverrides: new Dictionary<string, RuleMode>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [RuleIds.ApplyOverridePackFields] = RuleMode.Apply,
                }),
            overridePacks);
        _ = pipeline.Run(context);
        return new(
            context.Manifests,
            new(
                [.. context.Executions],
                [.. context.Changes],
                [.. context.Findings],
                [.. context.HumanCorrectionReviews],
                [.. context.Trace]));
    }

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
                Installers = CreateInstallers(mapping, null, []),
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

    private static List<Installer> CreateInstallers(
        AssetMappingPlan mapping,
        List<Installer>? previousInstallerModels,
        ImmutableArray<PreviousInstallerEntry> previousInstallers)
        => mapping.Decisions
            .Where(static decision => decision.Installer is not null)
            .Select(decision =>
            {
                PlannedInstaller planned = decision.Installer!;
                PreviousInstallerEntry? previous = decision.PreviousPosition is { } position
                    ? previousInstallers.SingleOrDefault(installer => installer.Position == position)
                    : null;
                if (previous is not null
                    && previousInstallerModels is not null
                    && previous.Position < previousInstallerModels.Count
                    && planned.SemanticallyMatches(previous))
                {
                    return previousInstallerModels[previous.Position];
                }

                return new Installer
                {
                    InstallerUrl = planned.Url.AbsoluteUri,
                    InstallerSha256 = planned.Sha256,
                    Architecture = planned.Architecture,
                    InstallerType = planned.InstallerType,
                    NestedInstallerType = planned.NestedInstallerType,
                    Scope = planned.Scope,
                    InstallerLocale = planned.InstallerLocale,
                    NestedInstallerFiles = planned.NestedInstallerFiles.IsEmpty
                        ? null
                        :
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

    private static void ClearStaleRootNestedState(InstallerManifest manifest)
    {
        if (manifest.Installers is not { Count: > 0 } installers
            || installers.Any(installer =>
                (installer.InstallerType ?? manifest.InstallerType) == InstallerType.Zip
                || installer.NestedInstallerType is not null
                || installer.NestedInstallerFiles is { Count: > 0 }))
        {
            return;
        }

        manifest.NestedInstallerType = null;
        manifest.NestedInstallerFiles = null;
        manifest.ArchiveBinariesDependOnPath = null;
    }

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

    private static PackageLocaleMetadata MergeReleaseMetadata(
        PackageLocaleMetadata explicitMetadata,
        PackageLocaleMetadata discovered)
    {
        var provenance = explicitMetadata.Provenance.ToBuilder();
        foreach ((string field, string source) in discovered.Provenance)
        {
            if (!HasExplicitValue(explicitMetadata, field))
            {
                provenance[field] = source;
            }
        }

        return explicitMetadata with
        {
            PublisherUrl = explicitMetadata.PublisherUrl ?? discovered.PublisherUrl,
            PackageUrl = explicitMetadata.PackageUrl ?? discovered.PackageUrl,
            License = explicitMetadata.License ?? discovered.License,
            LicenseUrl = explicitMetadata.LicenseUrl ?? discovered.LicenseUrl,
            Tags = explicitMetadata.Tags ?? discovered.Tags,
            ReleaseNotes = explicitMetadata.ReleaseNotes ?? discovered.ReleaseNotes,
            ReleaseNotesUrl = explicitMetadata.ReleaseNotesUrl ?? discovered.ReleaseNotesUrl,
            Provenance = provenance.ToImmutable(),
        };
    }

    private static bool HasExplicitValue(PackageLocaleMetadata metadata, string field)
        => field switch
        {
            nameof(PackageLocaleMetadata.PublisherUrl) => metadata.PublisherUrl is not null,
            nameof(PackageLocaleMetadata.PackageUrl) => metadata.PackageUrl is not null,
            nameof(PackageLocaleMetadata.License) => metadata.License is not null,
            nameof(PackageLocaleMetadata.LicenseUrl) => metadata.LicenseUrl is not null,
            nameof(PackageLocaleMetadata.Tags) => metadata.Tags is not null,
            nameof(PackageLocaleMetadata.ReleaseNotes) => metadata.ReleaseNotes is not null,
            nameof(PackageLocaleMetadata.ReleaseNotesUrl) => metadata.ReleaseNotesUrl is not null,
            _ => true,
        };

    private static ImmutableArray<RawManifestDocument> Serialize(
        PackageManifests manifests,
        string createdWith,
        ImmutableArray<RawManifestDocument> existingDocuments = default)
    {
        string directory = ManifestPaths.GetVersionDirectory(
            manifests.Version.PackageIdentifier!,
            manifests.Version.PackageVersion!);
        Dictionary<string, RawManifestDocument> existingByPath = existingDocuments.IsDefaultOrEmpty
            ? new Dictionary<string, RawManifestDocument>(StringComparer.Ordinal)
            : existingDocuments.ToDictionary(
                static document => document.RepositoryPath,
                StringComparer.Ordinal);
        return
        [
            .. PackageManifestIO.SerializeFiles(
                    manifests,
                    new ManifestWriteOptions { CreatedWith = createdWith })
                .Select(pair =>
                {
                    string repositoryPath = $"{directory}/{pair.Key}";
                    string yaml = existingByPath.TryGetValue(repositoryPath, out RawManifestDocument? existing)
                        ? ManifestYamlText.PreserveExistingLineEndings(
                            pair.Value,
                            StrictUtf8.Decode(existing.Content.AsSpan()))
                        : pair.Value;
                    return new RawManifestDocument(repositoryPath, StrictUtf8.Encode(yaml));
                })
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
    }

    private static PackageManifests CloneWithVersion(PackageManifests source, PackageVersion version)
    {
        InstallerManifest installer = ManifestYamlReader.ReadInstaller(
            ManifestYamlWriter.Serialize(source.Installer));
        DefaultLocaleManifest defaultLocale = ManifestYamlReader.ReadDefaultLocale(
            ManifestYamlWriter.Serialize(source.DefaultLocale));
        VersionManifest versionManifest = ManifestYamlReader.ReadVersion(
            ManifestYamlWriter.Serialize(source.Version));
        List<LocaleManifest> locales = source.Locales
            .Select(static locale => ManifestYamlReader.ReadLocale(ManifestYamlWriter.Serialize(locale)))
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
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawManifestDocument document in documents.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            if (!paths.Add(document.RepositoryPath))
            {
                throw new InvalidDataException(
                    $"Manifest set contains duplicate repository path '{document.RepositoryPath}'.");
            }

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
        foreach (Installer item in manifests.Installer.Installers ?? [])
        {
            if (!Uri.TryCreate(item.InstallerUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException(
                    $"Installer URL '{item.InstallerUrl}' must be an absolute HTTP or HTTPS URL.");
            }
        }

        return new(manifests.Version.PackageIdentifier!, manifests.Version.PackageVersion!, manifests);
    }

    private static ImmutableArray<WorkflowFileChange> Diff(
        ImmutableArray<RawManifestDocument> before,
        ImmutableArray<RawManifestDocument> after,
        bool toolGenerated)
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
                changes.Add(new(
                    PlannedChangeKind.Add,
                    path,
                    document.Content.AsSpan(),
                    ExpectedFileState.Absent,
                    provenance: toolGenerated
                        ? WorkflowChangeProvenance.ToolGenerated
                        : WorkflowChangeProvenance.Untrusted));
            }
            else if (!old.Content.AsSpan().SequenceEqual(document.Content.AsSpan()))
            {
                changes.Add(new(
                    PlannedChangeKind.Update,
                    path,
                    document.Content.AsSpan(),
                    ExpectedFileState.Present,
                    WorkflowFileChange.Hash(old.Content.AsSpan()),
                    toolGenerated
                        ? WorkflowChangeProvenance.ToolGenerated
                        : WorkflowChangeProvenance.Untrusted));
            }
        }

        foreach (string path in oldFiles.Keys.Except(newFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            RawManifestDocument old = oldFiles[path];
            changes.Add(new(
                PlannedChangeKind.Delete,
                path,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(old.Content.AsSpan()),
                provenance: toolGenerated
                    ? WorkflowChangeProvenance.ToolGenerated
                    : WorkflowChangeProvenance.Untrusted));
        }

        return changes.ToImmutable();
    }

    private static PolicyEvidence MergePolicyEvidence(
        PolicyEvidence supplied,
        ImmutableArray<ArtifactSnapshot>.Builder artifacts,
        ImmutableArray<DiscoveredAsset>.Builder assets,
        ImmutableArray<PackageSnapshot> existingVersions)
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
            ExistingDisplayVersions = supplied.ExistingDisplayVersions
                .Concat(existingVersions.SelectMany(GetDisplayVersions))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
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

    private static ImmutableArray<PackageSnapshot> RetainedVersions(
        ImmutableArray<PackageSnapshot> versions,
        PackageVersion targetVersion,
        UpdateOperationRequest? update)
        =>
        [
            .. versions.Where(snapshot =>
                !snapshot.PackageVersion.Equals(targetVersion)
                && (update?.ReplacePreviousVersion != true
                    || !snapshot.PackageVersion.Equals(update.PreviousVersion))),
        ];

    private static ImmutableArray<ExistingVersionSnapshot> CreateExistingVersions(
        ImmutableArray<PackageSnapshot> versions)
        =>
        [
            .. versions
                .OrderBy(static snapshot => snapshot.PackageVersion.Value, StringComparer.Ordinal)
                .Select(static snapshot => new ExistingVersionSnapshot(
                    snapshot.PackageVersion.Value,
                    GetDisplayVersions(snapshot))),
        ];

    private static string[] GetDisplayVersions(PackageSnapshot snapshot)
        => GetDisplayVersions(snapshot.Manifests);

    private static string[] GetDisplayVersions(PackageManifests manifests)
        => (manifests.Installer.AppsAndFeaturesEntries ?? [])
            .Concat((manifests.Installer.Installers ?? [])
                .SelectMany(static installer => installer.AppsAndFeaturesEntries ?? []))
            .Select(static entry => entry.DisplayVersion)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

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

    private static ValidationReport AddValidationFinding(
        ValidationReport validation,
        ValidationFinding finding)
        => new([.. validation.Findings, finding]);

    private static ValidationReport AddLearnedStoreFindings(
        ValidationReport validation,
        OverridePackStoreSnapshot snapshot)
    {
        if (snapshot.RecoveredFromBackup)
        {
            validation = AddValidationFinding(validation, new ValidationFinding(
                "WF_LEARNED_OVERRIDE_BACKUP_RECOVERED",
                ValidationSeverity.Warning,
                snapshot.QuarantinedCorruptPrimary
                    ? "The active learned override pack was corrupt; the last verified backup is in use and the corrupt primary was quarantined."
                    : "The active learned override pack is corrupt; read-only planning is using the last verified backup without modifying the store."));
        }

        if (snapshot.PendingActivation)
        {
            validation = AddValidationFinding(validation, new ValidationFinding(
                "WF_LEARNED_OVERRIDE_RECOVERY_PENDING",
                ValidationSeverity.Error,
                "An approved learned override is pending manifest/provenance recovery and remains inactive."));
        }

        return validation;
    }

    private static ValidationReport AddStaleLearnedOverrideFinding(
        ValidationReport validation,
        RuleRunSummary rules)
        => rules.Findings.Any(static finding =>
                string.Equals(
                    finding.RuleId,
                    RuleIds.ApplyOverridePackFields,
                    StringComparison.Ordinal)
                && finding.Severity != RuleSeverity.Info)
            ? AddValidationFinding(validation, new ValidationFinding(
                "WF_LEARNED_OVERRIDE_STALE",
                ValidationSeverity.Error,
                "An active learned override no longer matches the raw generated value and must be reviewed again."))
            : validation;

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
        IEnumerable<WorkflowAuditEntry> audit,
        ImmutableArray<InstallerArtifact> installerArtifacts = default,
        ImmutableArray<ExistingVersionSnapshot> existingVersions = default,
        bool? reviewApproved = null)
    {
        var plan = new LocalOperationPlan
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
            Preflight = new WorkflowPreflightRequest
            {
                BeforeDocuments = before,
                AfterDocuments = after,
                Changes = changes,
                InstallerArtifacts = installerArtifacts.IsDefault ? [] : installerArtifacts,
                ExistingVersions = existingVersions.IsDefault ? [] : existingVersions,
                Options = PreflightOptions(request),
            },
            Rules = rules,
            PlanningInputsFingerprint =
                LocalOperationPlanFingerprint.CreateRequestFingerprint(request),
            RuleEvaluationFingerprint =
                LocalOperationPlanFingerprint.CreateComponent(rules),
            ValidationFingerprint =
                LocalOperationPlanFingerprint.CreateComponent(validation.Findings),
            AuditFingerprint = LocalOperationPlanFingerprint.CreateComponent(
                audit.Where(static entry =>
                    !string.Equals(entry.Code, "CREATED_AT", StringComparison.Ordinal))),
            Questions = questions,
            ReviewApproved = false,
            Audit =
            [
                .. audit
                    .OrderBy(static entry => entry.Code, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Provenance, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Message, StringComparer.Ordinal),
            ],
        };
        plan = plan with
        {
            PreflightEvidenceFingerprint =
                LocalOperationPlanFingerprint.CreatePreflightFingerprint(plan.Preflight),
        };
        return plan with
        {
            ReviewApproved = reviewApproved
                ?? ReviewApproval.Matches(
                    request,
                    rules,
                    LocalOperationPlanFingerprint.CreateApprovalFingerprint(plan)),
        };
    }

    private Task<WorkflowOperationResult> ExecuteCurrentAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken)
        => request switch
        {
            NewOperationRequest value => NewAsync(value, cancellationToken),
            UpdateOperationRequest value => UpdateAsync(value, cancellationToken),
            RemoveOperationRequest value => RemoveAsync(value, cancellationToken),
            SubmitOperationRequest value => SubmitAsync(value, cancellationToken),
            NewLocaleOperationRequest value => NewLocaleAsync(value, cancellationToken),
            UpdateLocaleOperationRequest value => UpdateLocaleAsync(value, cancellationToken),
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
        };

    private static PackageIdentifier PackageIdentifierFor(WorkflowOperationRequest request)
        => request switch
        {
            NewOperationRequest value => value.PackageIdentifier,
            UpdateOperationRequest value => value.PackageIdentifier,
            RemoveOperationRequest value => value.PackageIdentifier,
            SubmitOperationRequest value => ParseRawSet(value.Documents).Identifier,
            NewLocaleOperationRequest value => value.PackageIdentifier,
            UpdateLocaleOperationRequest value => value.PackageIdentifier,
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
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
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
        };

    private WorkflowReleaseProvenance? CreateReleaseProvenance(
        IEnumerable<DiscoveredAsset> assets)
    {
        DiscoveredAsset[] releaseAssets =
        [
            .. assets.Where(static asset => asset.ReleaseId > 0),
        ];
        if (releaseAssets.Length == 0
            || releaseAssets.Select(static asset => asset.ReleaseId).Distinct().Count() != 1)
        {
            return null;
        }

        Uri releaseUri = releaseAssets[0].ReleaseUri;
        if (!releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !releaseUri.Host.Equals(_trustedGitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] segments = releaseUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        DateTimeOffset updatedAt = releaseAssets
            .SelectMany(static asset => new DateTimeOffset?[]
            {
                asset.ReleaseUpdatedAt,
                asset.ReleasePublishedAt,
                asset.AssetUpdatedAt,
                asset.AssetCreatedAt,
            })
            .Where(static instant => instant.HasValue)
            .Select(static instant => instant!.Value)
            .Max();
        return new(
            new WinMatsch.GitHub.RepositoryCoordinates(segments[0], segments[1]),
            releaseAssets[0].ReleaseId,
            updatedAt);
    }

    private static WorkflowOperationResult MissingResult(
        string operation,
        WorkflowOperationRequest request,
        PackageIdentifier identifier,
        PackageVersion version,
        string? diagnostic = null)
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
                    new(
                        "WF_NOT_FOUND",
                        ValidationSeverity.Error,
                        diagnostic ?? "The exact package version was not found."),
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

    private sealed class ArtifactDirectoryLease : IDisposable
    {
        private readonly bool _owned;

        public ArtifactDirectoryLease(
            WorkflowExecutionMode mode,
            string? requestedPath,
            bool usePreparedPathInPlan = false)
        {
            if ((mode == WorkflowExecutionMode.Apply || usePreparedPathInPlan)
                && !string.IsNullOrWhiteSpace(requestedPath))
            {
                Path = System.IO.Path.GetFullPath(requestedPath);
                return;
            }

            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "winmatsch-artifacts",
                Guid.NewGuid().ToString("N"));
            _owned = true;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_owned && Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
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
