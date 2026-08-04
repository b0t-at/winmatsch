using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Rules;
using WinMatsch.Validation;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public static class SubmissionJournalMaterializer
{
    public static async Task<VerifiedSubmissionRecoveryRequest> MaterializeVerifiedAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        CancellationToken cancellationToken)
        => new(await MaterializeAsync(entry, gitHub, cancellationToken).ConfigureAwait(false));

    public static async Task<VerifiedSubmissionRecoveryRequest> MaterializeVerifiedAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        InstallerDownloader downloader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        return await MaterializeVerifiedAsync(
            entry,
            gitHub,
            new DurableInstallerPreflightNetwork(downloader),
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<VerifiedSubmissionRecoveryRequest> MaterializeVerifiedAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        IPreflightNetwork artifactNetwork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifactNetwork);
        return new(await MaterializeAsync(
            entry,
            gitHub,
            artifactNetwork,
            cancellationToken).ConfigureAwait(false));
    }

    public static async Task<GitHubSubmissionRequest> MaterializeAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        CancellationToken cancellationToken)
        => await MaterializeAsync(
            entry,
            gitHub,
            artifactNetwork: null,
            cancellationToken).ConfigureAwait(false);

    public static async Task<GitHubSubmissionRequest> MaterializeAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        InstallerDownloader downloader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        return await MaterializeAsync(
            entry,
            gitHub,
            new DurableInstallerPreflightNetwork(downloader),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubSubmissionRequest> MaterializeAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        IPreflightNetwork? artifactNetwork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(gitHub);
        VerifyRepositoryIdentity(entry);
        BranchState upstream = await gitHub.GetDefaultBranchAsync(
            entry.RemoteRequest.UpstreamRepository,
            cancellationToken).ConfigureAwait(false);

        ImmutableArray<RawManifestDocument> after = ReadLocalDocuments(
            entry.Repository.CanonicalPath,
            entry.LocalPlan.AfterDocuments);
        ImmutableArray<RawManifestDocument> before = await ReadRemoteDocumentsAsync(
            entry,
            gitHub,
            upstream.HeadSha,
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<WorkflowFileChange> changes = ReadChanges(
            entry.Repository.CanonicalPath,
            entry.LocalPlan.FileChanges);
        ImmutableArray<InstallerArtifact> artifacts = await MaterializeArtifactsAsync(
            after,
            entry.LocalPlan.InstallerArtifacts,
            artifactNetwork,
            cancellationToken).ConfigureAwait(false);
        var preflight = new WorkflowPreflightRequest
        {
            BeforeDocuments = before,
            AfterDocuments = after,
            Changes = changes,
            InstallerArtifacts = artifacts,
            ExistingVersions =
            [
                .. entry.LocalPlan.ExistingVersions.Select(static item =>
                    new ExistingVersionSnapshot(item.PackageVersion, item.DisplayVersions)),
            ],
            Options = new PreflightOptions
            {
                WarningPolicy = entry.LocalPlan.WarningPolicy,
                NetworkMode = entry.LocalPlan.NetworkMode,
            },
        };
        var plan = new LocalOperationPlan
        {
            Operation = entry.LocalPlan.Operation,
            PackageIdentifier = entry.LocalPlan.PackageIdentifier,
            PackageVersion = entry.LocalPlan.PackageVersion,
            OutputDirectory = entry.Repository.CanonicalPath,
            FileChanges = changes,
            BeforeDocuments = before,
            AfterDocuments = after,
            Validation = new ValidationReport(),
            WarningPolicy = entry.LocalPlan.WarningPolicy,
            Preflight = preflight,
            Rules = RuleRunSummary.Empty,
            Release = entry.LocalPlan.Release,
            PlanningInputsFingerprint = entry.LocalPlan.PlanningInputsFingerprint,
            RuleEvaluationFingerprint = entry.LocalPlan.RuleEvaluationFingerprint,
            ValidationFingerprint = entry.LocalPlan.ValidationFingerprint,
            AuditFingerprint = entry.LocalPlan.AuditFingerprint,
            PreflightEvidenceFingerprint = entry.LocalPlan.PreflightEvidenceFingerprint,
            LearnedOverrideFingerprint = entry.LocalPlan.LearnedOverrideFingerprint,
            ReviewApproved = entry.LocalPlan.ReviewApproved,
        };
        if (!string.Equals(
                plan.Fingerprint,
                entry.LocalPlan.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new SubmissionJournalTamperedException(
                "The verified local and upstream bytes do not reproduce the journaled plan fingerprint.");
        }

        SubmissionJournalRemoteRequest remote = entry.RemoteRequest;
        return new()
        {
            LocalPlan = plan,
            UpstreamRepository = remote.UpstreamRepository,
            TargetRepository = entry.RemoteState.Fork ?? remote.TargetRepository,
            ForkOwner = remote.ForkOwner,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Operation = remote.Operation,
            Policy = NormalizePolicy(remote.Policy),
            CreatedWith = remote.CreatedWith,
            CustomTitle = remote.CustomTitle,
            Resolves = remote.Resolves,
            SupersedesPullRequestNumber = remote.SupersedesPullRequestNumber,
            IdempotencyKey = remote.IdempotencyKey,
            RepositoryEvidence = remote.RepositoryEvidence,
            VanityUrlAnnotations = remote.VanityUrlAnnotations,
            ReleaseUpdatedAt = remote.ReleaseUpdatedAt,
            ReleaseRepository = remote.ReleaseRepository,
            ReleaseId = remote.ReleaseId,
            Presentation = remote.Presentation,
            ResumeFrom = entry.RemoteState,
        };
    }

    private static ImmutableArray<RawManifestDocument> ReadLocalDocuments(
        string root,
        ImmutableArray<SubmissionJournalDocumentIdentity> identities)
        =>
        [
            .. identities
                .OrderBy(static identity => identity.RepositoryPath, StringComparer.Ordinal)
                .Select(identity =>
                {
                    byte[] content = ReadVerifiedLocalFile(root, identity);
                    return new RawManifestDocument(identity.RepositoryPath, content);
                }),
        ];

    private static async Task<ImmutableArray<RawManifestDocument>> ReadRemoteDocumentsAsync(
        SubmissionJournalEntry entry,
        IGitHubRepositoryClient gitHub,
        string upstreamSha,
        CancellationToken cancellationToken)
    {
        var documents = ImmutableArray.CreateBuilder<RawManifestDocument>();
        foreach (SubmissionJournalDocumentIdentity identity in entry.LocalPlan.BeforeDocuments
                     .OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            RepositoryContent remote;
            try
            {
                remote = await gitHub.GetContentAsync(
                    entry.RemoteRequest.UpstreamRepository,
                    identity.RepositoryPath,
                    upstreamSha,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SubmissionJournalConflictException(
                    $"Upstream path '{identity.RepositoryPath}' no longer exists.",
                    exception);
            }

            VerifyIdentity(identity, remote.Bytes.Span);
            documents.Add(new(identity.RepositoryPath, remote.Bytes.Span));
        }

        return documents.ToImmutable();
    }

    private static ImmutableArray<WorkflowFileChange> ReadChanges(
        string root,
        ImmutableArray<SubmissionJournalFileIdentity> identities)
        =>
        [
            .. identities.Select(identity =>
            {
                if (identity.Kind == PlannedChangeKind.Delete)
                {
                    string deletedPath = Resolve(root, identity.RepositoryPath);
                    if (File.Exists(deletedPath))
                    {
                        throw new SubmissionJournalConflictException(
                            $"Journaled deletion '{identity.RepositoryPath}' was recreated locally.");
                    }

                    return new WorkflowFileChange(
                        identity.Kind,
                        identity.RepositoryPath,
                        expectedState: identity.ExpectedState,
                        expectedSha256: identity.ExpectedSha256,
                        provenance: identity.Provenance);
                }

                byte[] content = ReadVerifiedLocalFile(
                    root,
                    new(
                        identity.RepositoryPath,
                        identity.CommittedSha256
                            ?? throw new SubmissionJournalTamperedException(
                                $"Journaled change '{identity.RepositoryPath}' has no committed identity."),
                        identity.CommittedLength));
                return new WorkflowFileChange(
                    identity.Kind,
                    identity.RepositoryPath,
                    content,
                    identity.ExpectedState,
                    identity.ExpectedSha256,
                    identity.Provenance);
            }),
        ];

    private static async Task<ImmutableArray<InstallerArtifact>> MaterializeArtifactsAsync(
        ImmutableArray<RawManifestDocument> after,
        ImmutableArray<SubmissionJournalArtifactIdentity> identities,
        IPreflightNetwork? artifactNetwork,
        CancellationToken cancellationToken)
    {
        if (identities.IsEmpty)
        {
            return [];
        }

        if (artifactNetwork is null)
        {
            throw new InvalidOperationException(
                "Journaled installer artifacts must be reacquired before materialization.");
        }

        var installers = new Dictionary<string, (string Url, string Sha256)>(StringComparer.Ordinal);
        foreach (RawManifestDocument document in after)
        {
            string yaml = new UTF8Encoding(false, true).GetString(document.Content.AsSpan());
            if (ManifestYamlReader.TryDetectType(yaml) != ManifestType.Installer)
            {
                continue;
            }

            InstallerManifest manifest = ManifestYamlReader.ReadInstaller(yaml);
            foreach (Installer installer in manifest.Installers ?? [])
            {
                if (string.IsNullOrWhiteSpace(installer.InstallerUrl)
                    || installer.InstallerSha256 is null)
                {
                    continue;
                }

                installers[HashText(installer.InstallerUrl)] =
                    (installer.InstallerUrl, installer.InstallerSha256.Value);
            }
        }

        var artifacts = ImmutableArray.CreateBuilder<InstallerArtifact>(identities.Length);
        foreach (SubmissionJournalArtifactIdentity identity in identities)
        {
            if (!installers.TryGetValue(identity.InstallerUrlSha256, out var installer)
                || !string.Equals(
                    installer.Sha256,
                    identity.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SubmissionJournalConflictException(
                    "The current local installer manifest no longer matches journaled artifact evidence.");
            }

            var expected = new DownloadResult
            {
                FilePath = UnmaterializedArtifactPath(),
                FileName = "journaled-installer.bin",
                Sha256 = new Sha256Hash(identity.ContentSha256),
                SizeInBytes = identity.SizeInBytes,
                InitialUrl = installer.Url,
                FinalUrl = installer.Url,
            };
            DownloadRevalidationResult revalidated;
            try
            {
                revalidated = await artifactNetwork.RevalidateAsync(
                    expected,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException exception)
            {
                throw new IOException(
                    "Journaled installer artifact reacquisition failed for "
                    + GitHubSubmissionFormatter.Redact(installer.Url)
                    + ": "
                    + GitHubSubmissionFormatter.Redact(exception.Message),
                    exception);
            }

            DownloadResult download = revalidated.Result;
            if (revalidated.Status != DownloadRevalidationStatus.Unchanged
                || download.ContentIdentity != expected.ContentIdentity)
            {
                throw new SubmissionJournalConflictException(
                    "The current installer bytes no longer match the journaled artifact identity.");
            }

            if (string.IsNullOrWhiteSpace(download.FilePath)
                || !Path.IsPathFullyQualified(download.FilePath)
                || !File.Exists(download.FilePath))
            {
                throw new IOException(
                    "Journaled installer artifact reacquisition did not produce an accessible "
                    + "absolute file path.");
            }

            artifacts.Add(new(installer.Url, download));
        }

        return artifacts.ToImmutable();
    }

    private static string UnmaterializedArtifactPath()
        => Path.Combine(
            Path.GetTempPath(),
            "winmatsch-journal-artifacts",
            Guid.NewGuid().ToString("N"),
            "pending.bin");

    private static byte[] ReadVerifiedLocalFile(
        string root,
        SubmissionJournalDocumentIdentity identity)
    {
        string path = Resolve(root, identity.RepositoryPath);
        byte[] content = File.ReadAllBytes(path);
        VerifyIdentity(identity, content);
        return content;
    }

    private static void VerifyIdentity(
        SubmissionJournalDocumentIdentity identity,
        ReadOnlySpan<byte> content)
    {
        if (content.Length != identity.Length
            || !string.Equals(
                WorkflowFileChange.Hash(content),
                identity.Sha256,
                StringComparison.Ordinal))
        {
            throw new SubmissionJournalConflictException(
                $"Document '{identity.RepositoryPath}' does not match its journaled byte identity.");
        }
    }

    private static void VerifyRepositoryIdentity(SubmissionJournalEntry entry)
    {
        string root = entry.Repository.CanonicalPath;
        if (!Directory.Exists(root)
            || !string.Equals(
                DirectoryPin.GetIdentity(root),
                entry.Repository.FileSystemIdentity,
                StringComparison.Ordinal))
        {
            throw new SubmissionJournalConflictException(
                "The journal is not bound to the current output repository identity.");
        }
    }

    private static string Resolve(string root, string repositoryPath)
    {
        string full = SecurePath.Resolve(root, repositoryPath, requireExistingLeaf: false);
        SecurePath.RejectReparsePoints(root, full);
        return full;
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static GitHubSubmissionPolicy NormalizePolicy(GitHubSubmissionPolicy policy)
        => policy with
        {
            DuplicateHashes = policy.DuplicateHashes with
            {
                DeniedSha256 = policy.DuplicateHashes.DeniedSha256.ToImmutableHashSet(
                    StringComparer.OrdinalIgnoreCase),
                AllowedSha256 = policy.DuplicateHashes.AllowedSha256.ToImmutableHashSet(
                    StringComparer.OrdinalIgnoreCase),
            },
        };
}
