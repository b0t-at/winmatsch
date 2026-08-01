using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed partial class FileSubmissionJournalStore : ISubmissionJournalStore
{
    private readonly string _rootDirectory;
    private readonly string _overrideStoreDirectory;
    private readonly IWorkflowClock _clock;

    public FileSubmissionJournalStore(
        SubmissionJournalOptions? options = null,
        IWorkflowClock? clock = null)
    {
        SubmissionJournalOptions resolved = options ?? new SubmissionJournalOptions();
        _rootDirectory = Path.GetFullPath(resolved.RootDirectory);
        _overrideStoreDirectory = Path.GetFullPath(
            resolved.OverrideStoreDirectory
            ?? OverridePackStoreOptions.CreateDefault().RootDirectory);
        _clock = clock ?? new SystemWorkflowClock();
    }

    public async Task<SubmissionJournalHandle> PrepareAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.LocalPlan.CanApply || request.LocalPlan.FileChanges.IsEmpty)
        {
            throw new ArgumentException(
                "Only a non-empty, commit-ready local plan can prepare a submission.",
                nameof(request));
        }

        ValidateSecretFree(request);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        SubmissionRepositoryIdentity repository = CreateRepositoryIdentity(
            request.LocalPlan.OutputDirectory);
        RecoverPreparedIntentsUnderLock(repository, cancellationToken);
        GitHubSubmissionPlan remotePlan = GitHubLifecycleWorkflow.Plan(request);
        if (!remotePlan.CanApply)
        {
            throw new ArgumentException(
                "The GitHub submission plan is not commit-ready.",
                nameof(request));
        }

        SubmissionJournalRemoteRequest remoteRequest = Snapshot(request, remotePlan);
        ValidateSecretFree(remoteRequest);
        string remoteRequestFingerprint = SubmissionRequestFingerprint.Create(remoteRequest);
        SubmissionJournalEntry? existing = ReadAllEntries("*.journal")
            .Concat(ReadAllIntents())
            .FirstOrDefault(entry =>
                string.Equals(
                    entry.Repository.FileSystemIdentity,
                    repository.FileSystemIdentity,
                    StringComparison.Ordinal)
                && entry.LocalPlan.PackageIdentifier == request.LocalPlan.PackageIdentifier
                && entry.LocalPlan.PackageVersion == request.LocalPlan.PackageVersion
                && entry.RemoteRequest.UpstreamRepository == request.UpstreamRepository
                && entry.State is not SubmissionJournalState.Cancelled);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.LocalPlan.Fingerprint,
                    request.LocalPlan.Fingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.RemoteRequest.IdempotencyKey,
                    request.IdempotencyKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.RemoteRequestFingerprint,
                    remoteRequestFingerprint,
                    StringComparison.Ordinal))
            {
                throw new SubmissionJournalConflictException(
                    "A different pending submission already owns this repository package version.");
            }

            return new(existing.Id, existing.LocalPlan.Fingerprint);
        }

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _clock.UtcNow;
        var entry = new SubmissionJournalEntry
        {
            Id = id,
            Repository = repository,
            LocalPlan = Snapshot(request.LocalPlan),
            RemoteRequest = remoteRequest,
            RemoteRequestFingerprint = remoteRequestFingerprint,
            RemoteRequestFingerprintVersion = SubmissionRequestFingerprint.CurrentVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };
        WriteEnvelope(
            IntentPath(id),
            new SubmissionPreparedIntent(entry),
            SubmissionJournalJsonContext.Default.SubmissionPreparedIntent);
        return new(id, entry.LocalPlan.Fingerprint);
    }

    public async Task<SubmissionJournalEntry> ActivateAsync(
        SubmissionJournalHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        EnsureId(handle.Id);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string journalPath = JournalPath(handle.Id);
        if (File.Exists(journalPath))
        {
            SubmissionJournalEntry existing = ReadEntry(journalPath);
            EnsureFingerprint(existing, handle.LocalPlanFingerprint);
            return existing;
        }

        SubmissionJournalEntry entry = ReadIntent(IntentPath(handle.Id));
        EnsureFingerprint(entry, handle.LocalPlanFingerprint);
        if (!VerifyCommittedState(entry, out string? diagnostic))
        {
            throw new SubmissionJournalConflictException(
                diagnostic ?? "The committed local state does not match the prepared submission.");
        }

        WriteEntry(journalPath, entry);
        DeleteDurably(IntentPath(handle.Id));
        return entry;
    }

    public async Task<SubmissionJournalRecoveryResult> RecoverAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string canonical = CanonicalPath(outputDirectory);
        var activated = ImmutableArray.CreateBuilder<SubmissionJournalEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        foreach (string path in Directory.EnumerateFiles(_rootDirectory, "*.intent")
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmissionJournalEntry entry = ReadIntent(path);
            if (!string.Equals(
                    entry.Repository.CanonicalPath,
                    canonical,
                    PathComparison()))
            {
                continue;
            }

            string journalPath = JournalPath(entry.Id);
            if (File.Exists(journalPath))
            {
                DeleteDurably(path);
                continue;
            }

            if (VerifyCommittedState(entry, out string? committedDiagnostic))
            {
                WriteEntry(journalPath, entry);
                DeleteDurably(path);
                activated.Add(entry);
                continue;
            }

            if (VerifyUncommittedState(entry, out string? uncommittedDiagnostic))
            {
                DeleteDurably(path);
                diagnostics.Add(
                    $"Discarded uncommitted submission intent '{entry.Id}'.");
                continue;
            }

            diagnostics.Add(
                $"Retained submission intent '{entry.Id}': "
                + (committedDiagnostic ?? uncommittedDiagnostic ?? "local state is mixed."));
        }

        return new(activated.ToImmutable(), diagnostics.ToImmutable());
    }

    private void RecoverPreparedIntentsUnderLock(
        SubmissionRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFiles(_rootDirectory, "*.intent")
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmissionJournalEntry entry = ReadIntent(path);
            if (!string.Equals(
                    entry.Repository.FileSystemIdentity,
                    repository.FileSystemIdentity,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string journalPath = JournalPath(entry.Id);
            if (File.Exists(journalPath))
            {
                DeleteDurably(path);
            }
            else if (VerifyCommittedState(entry, out _))
            {
                WriteEntry(journalPath, entry);
                DeleteDurably(path);
            }
            else if (VerifyUncommittedState(entry, out _))
            {
                DeleteDurably(path);
            }
        }
    }

    public async Task<ImmutableArray<SubmissionJournalEntry>> ListPendingAsync(
        CancellationToken cancellationToken)
    {
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        return
        [
            .. ReadAllEntries("*.journal")
                .Where(static entry => entry.State is not SubmissionJournalState.Cancelled)
                .OrderBy(static entry => entry.CreatedAt)
                .ThenBy(static entry => entry.Id, StringComparer.Ordinal),
        ];
    }

    public async Task<SubmissionJournalEntry?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        EnsureId(id);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string path = JournalPath(id);
        return File.Exists(path) ? ReadEntry(path) : null;
    }

    public async Task<SubmissionJournalEntry> RecordRemoteStateAsync(
        string id,
        long expectedRevision,
        RemoteMutationState remoteState,
        SubmissionJournalState state,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        EnsureId(id);
        ArgumentNullException.ThrowIfNull(remoteState);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string path = JournalPath(id);
        SubmissionJournalEntry current = ReadEntry(path);
        EnsureRevision(current, expectedRevision);
        ValidateTransition(current.State, state);
        SubmissionJournalEntry updated = current with
        {
            Revision = checked(current.Revision + 1),
            State = state,
            RemoteState = remoteState,
            UpdatedAt = _clock.UtcNow,
            LastError = errorMessage,
        };
        WriteEntry(path, updated);
        return updated;
    }

    public async Task CancelAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        EnsureId(id);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string path = JournalPath(id);
        SubmissionJournalEntry current = ReadEntry(path);
        EnsureRevision(current, expectedRevision);
        if (current.RemoteState.RemoteOutcomeUncertain
            || current.State is SubmissionJournalState.BranchCreated
                or SubmissionJournalState.CommitCreated
                or SubmissionJournalState.PullRequestCreated
                or SubmissionJournalState.EscalationRequired)
        {
            throw new SubmissionJournalConflictException(
                "A submission with remote effects or uncertain state cannot be cancelled locally.");
        }

        WriteEntry(path, current with
        {
            Revision = checked(current.Revision + 1),
            State = SubmissionJournalState.Cancelled,
            UpdatedAt = _clock.UtcNow,
        });
    }

    public async Task CompleteAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        EnsureId(id);
        EnsureRoot();
        await using FileStream fileLock = AcquireGlobalLock();
        cancellationToken.ThrowIfCancellationRequested();
        string path = JournalPath(id);
        SubmissionJournalEntry current = ReadEntry(path);
        EnsureRevision(current, expectedRevision);
        if (current.State != SubmissionJournalState.PullRequestCreated
            || !current.RemoteState.PullRequestCreated
            || current.RemoteState.RemoteOutcomeUncertain)
        {
            throw new SubmissionJournalConflictException(
                "Only a verified pull-request boundary can complete a submission journal.");
        }

        DeleteDurably(path);
    }

    private static SubmissionJournalLocalPlan Snapshot(LocalOperationPlan plan)
        => new()
        {
            Operation = plan.Operation,
            PackageIdentifier = plan.PackageIdentifier,
            PackageVersion = plan.PackageVersion,
            Fingerprint = plan.Fingerprint,
            PlanningInputsFingerprint = plan.PlanningInputsFingerprint,
            RuleEvaluationFingerprint = Component(
                plan.RuleEvaluationFingerprint,
                plan.Rules),
            ValidationFingerprint = Component(
                plan.ValidationFingerprint,
                plan.Validation.Findings),
            AuditFingerprint = Component(
                plan.AuditFingerprint,
                plan.Audit.Where(static entry =>
                    !string.Equals(entry.Code, "CREATED_AT", StringComparison.Ordinal))),
            PreflightEvidenceFingerprint = string.IsNullOrWhiteSpace(
                plan.PreflightEvidenceFingerprint)
                ? LocalOperationPlanFingerprint.CreatePreflightFingerprint(plan.Preflight)
                : plan.PreflightEvidenceFingerprint,
            LearnedOverrideFingerprint = plan.LearnedOverrideFingerprint
                ?? (plan.LearnedOverride is null
                    ? null
                    : LocalOperationPlanFingerprint.CreateComponent(plan.LearnedOverride)),
            WarningPolicy = plan.WarningPolicy,
            NetworkMode = plan.Preflight.Options.NetworkMode,
            ReviewApproved = plan.ReviewApproved,
            LearnedOverrideContentSha256 = plan.LearnedOverride is null
                ? null
                : WorkflowFileChange.Hash(
                    Encoding.UTF8.GetBytes(
                        OverridePackYaml.Write(plan.LearnedOverride.Pack))),
            FileChanges =
            [
                .. plan.FileChanges.Select(static change => new SubmissionJournalFileIdentity(
                    change.Kind,
                    change.RepositoryPath,
                    change.ExpectedState,
                    change.ExpectedSha256,
                    change.Provenance,
                    change.Kind == PlannedChangeKind.Delete
                        ? null
                        : WorkflowFileChange.Hash(change.Content.AsSpan()),
                    change.Kind == PlannedChangeKind.Delete ? 0 : change.Content.Length)),
            ],
            BeforeDocuments =
            [
                .. plan.BeforeDocuments.Select(static document =>
                    new SubmissionJournalDocumentIdentity(
                        document.RepositoryPath,
                        WorkflowFileChange.Hash(document.Content.AsSpan()),
                        document.Content.Length)),
            ],
            AfterDocuments =
            [
                .. plan.AfterDocuments.Select(static document =>
                    new SubmissionJournalDocumentIdentity(
                        document.RepositoryPath,
                        WorkflowFileChange.Hash(document.Content.AsSpan()),
                        document.Content.Length)),
            ],
            InstallerArtifacts =
            [
                .. plan.Preflight.InstallerArtifacts.Select(static artifact =>
                    new SubmissionJournalArtifactIdentity(
                        HashText(artifact.InstallerUrl),
                        artifact.Download.Sha256.Value,
                        artifact.Download.SizeInBytes)),
            ],
            ExistingVersions =
            [
                .. plan.Preflight.ExistingVersions.Select(static existing =>
                    new SubmissionJournalExistingVersion(
                        existing.PackageVersion,
                        [.. existing.DisplayVersions.Order(StringComparer.Ordinal)])),
            ],
            Release = plan.Release,
        };

    private static SubmissionJournalRemoteRequest Snapshot(
        GitHubSubmissionRequest request,
        GitHubSubmissionPlan plan)
        => new()
        {
            UpstreamRepository = request.UpstreamRepository,
            TargetRepository = request.TargetRepository,
            ForkOwner = request.ForkOwner,
            Operation = request.Operation,
            Policy = request.Policy,
            CreatedWith = request.CreatedWith,
            CustomTitle = request.CustomTitle,
            Resolves = request.Resolves,
            SupersedesPullRequestNumber = request.SupersedesPullRequestNumber,
            IdempotencyKey = request.IdempotencyKey,
            RepositoryEvidence = request.RepositoryEvidence,
            VanityUrlAnnotations = request.VanityUrlAnnotations,
            ReleaseUpdatedAt = request.ReleaseUpdatedAt,
            ReleaseRepository = request.ReleaseRepository,
            ReleaseId = request.ReleaseId,
            Presentation = new(
                plan.CommitTitle,
                plan.PullRequestTitle,
                plan.PullRequestBody),
        };

    private bool VerifyCommittedState(
        SubmissionJournalEntry entry,
        out string? diagnostic)
    {
        if (!VerifyRepository(entry, out diagnostic))
        {
            return false;
        }

        if (HasIncompleteManifestTransaction(entry, out diagnostic)
            || !VerifyLearnedOverride(entry, out diagnostic))
        {
            return false;
        }

        foreach (SubmissionJournalFileIdentity change in entry.LocalPlan.FileChanges)
        {
            string path = ResolveRepositoryPath(entry.Repository.CanonicalPath, change.RepositoryPath);
            if (change.Kind == PlannedChangeKind.Delete)
            {
                if (File.Exists(path))
                {
                    diagnostic = $"Committed deletion '{change.RepositoryPath}' is present.";
                    return false;
                }
            }
            else if (!VerifyFile(path, change.CommittedSha256!, change.CommittedLength))
            {
                diagnostic = $"Committed file '{change.RepositoryPath}' changed or is missing.";
                return false;
            }
        }

        foreach (SubmissionJournalDocumentIdentity document in entry.LocalPlan.AfterDocuments)
        {
            string path = ResolveRepositoryPath(entry.Repository.CanonicalPath, document.RepositoryPath);
            if (!VerifyFile(path, document.Sha256, document.Length))
            {
                diagnostic = $"Validated document '{document.RepositoryPath}' changed or is missing.";
                return false;
            }
        }

        diagnostic = null;
        return true;
    }

    private static bool VerifyUncommittedState(
        SubmissionJournalEntry entry,
        out string? diagnostic)
    {
        if (!VerifyRepository(entry, out diagnostic))
        {
            return false;
        }

        foreach (SubmissionJournalFileIdentity change in entry.LocalPlan.FileChanges)
        {
            string path = ResolveRepositoryPath(entry.Repository.CanonicalPath, change.RepositoryPath);
            if (change.ExpectedState == ExpectedFileState.Absent)
            {
                if (File.Exists(path))
                {
                    diagnostic = $"Expected absent path '{change.RepositoryPath}' is present.";
                    return false;
                }
            }
            else if (change.ExpectedSha256 is null
                     || !VerifyFile(path, change.ExpectedSha256, expectedLength: null))
            {
                diagnostic = $"Original file '{change.RepositoryPath}' changed or is missing.";
                return false;
            }
        }

        diagnostic = null;
        return true;
    }

    private static bool HasIncompleteManifestTransaction(
        SubmissionJournalEntry entry,
        out string? diagnostic)
    {
        string packageKey = entry.LocalPlan.PackageIdentifier.Value.ToUpperInvariant();
        string prefix =
            $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageKey)))[..16]}-";
        foreach (string transaction in Directory.EnumerateDirectories(
                     entry.Repository.CanonicalPath,
                     $"{prefix}*",
                     SearchOption.TopDirectoryOnly))
        {
            string journal = Path.Combine(transaction, "journal");
            if (!File.Exists(journal)
                || !string.Equals(
                    File.ReadLines(journal).FirstOrDefault(),
                    "committed",
                    StringComparison.Ordinal))
            {
                diagnostic =
                    "The manifest transaction or original-submission provenance is not fully committed.";
                return true;
            }
        }

        diagnostic = null;
        return false;
    }

    private bool VerifyLearnedOverride(
        SubmissionJournalEntry entry,
        out string? diagnostic)
    {
        if (entry.LocalPlan.LearnedOverrideContentSha256 is null)
        {
            diagnostic = null;
            return true;
        }

        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = _overrideStoreDirectory,
        });
        string path = store.ResolvePath(entry.LocalPlan.PackageIdentifier);
        if (File.Exists($"{path}.transaction")
            || !File.Exists(path)
            || !VerifyFile(
                path,
                entry.LocalPlan.LearnedOverrideContentSha256,
                expectedLength: null))
        {
            diagnostic =
                "The approved learned override is not durably active for this committed manifest plan.";
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool VerifyFile(string path, string expectedSha256, long? expectedLength)
    {
        RejectReparsePoint(path);
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (expectedLength is not null && info.Length != expectedLength)
        {
            return false;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
    }

    private static bool VerifyRepository(
        SubmissionJournalEntry entry,
        out string? diagnostic)
    {
        string root = entry.Repository.CanonicalPath;
        if (!Directory.Exists(root))
        {
            diagnostic = "The journal repository no longer exists.";
            return false;
        }

        RejectReparsePoint(root);
        SubmissionRepositoryIdentity current = CreateRepositoryIdentity(root);
        if (!string.Equals(
                current.FileSystemIdentity,
                entry.Repository.FileSystemIdentity,
                StringComparison.Ordinal))
        {
            diagnostic = "The repository identity no longer matches the journal.";
            return false;
        }

        diagnostic = null;
        return true;
    }

    private SubmissionJournalEntry[] ReadAllEntries(string pattern)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_rootDirectory, pattern)
            .Order(StringComparer.Ordinal)
            .Select(ReadEntry)
            .ToArray();
    }

    private SubmissionJournalEntry[] ReadAllIntents()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_rootDirectory, "*.intent")
            .Order(StringComparer.Ordinal)
            .Select(ReadIntent)
            .ToArray();
    }

    private SubmissionJournalEntry ReadEntry(string path)
    {
        SubmissionJournalEntry entry = ReadEnvelope(
            path,
            SubmissionJournalJsonContext.Default.SubmissionJournalEntry);
        return ValidateAndMigrateRemoteFingerprint(path, entry, preparedIntent: false);
    }

    private SubmissionJournalEntry ReadIntent(string path)
    {
        SubmissionJournalEntry entry = ReadEnvelope(
            path,
            SubmissionJournalJsonContext.Default.SubmissionPreparedIntent).Entry;
        return ValidateAndMigrateRemoteFingerprint(path, entry, preparedIntent: true);
    }

    private SubmissionJournalEntry ValidateAndMigrateRemoteFingerprint(
        string path,
        SubmissionJournalEntry entry,
        bool preparedIntent)
    {
        string actual = SubmissionRequestFingerprint.Create(entry.RemoteRequest);
        if (entry.RemoteRequestFingerprintVersion
            is not (0 or SubmissionRequestFingerprint.CurrentVersion))
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' uses unsupported remote-request fingerprint version "
                + $"{entry.RemoteRequestFingerprintVersion}.");
        }

        if (!string.IsNullOrWhiteSpace(entry.RemoteRequestFingerprint)
            && !string.Equals(
                entry.RemoteRequestFingerprint,
                actual,
                StringComparison.Ordinal))
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has inconsistent remote-request identity.");
        }

        if (entry.RemoteRequestFingerprintVersion
                == SubmissionRequestFingerprint.CurrentVersion
            && !string.IsNullOrWhiteSpace(entry.RemoteRequestFingerprint))
        {
            return entry;
        }

        SubmissionJournalEntry migrated = entry with
        {
            RemoteRequestFingerprint = actual,
            RemoteRequestFingerprintVersion = SubmissionRequestFingerprint.CurrentVersion,
        };
        if (preparedIntent)
        {
            WriteEnvelope(
                path,
                new SubmissionPreparedIntent(migrated),
                SubmissionJournalJsonContext.Default.SubmissionPreparedIntent);
        }
        else
        {
            WriteEntry(path, migrated);
        }

        return migrated;
    }

    private void WriteEntry(string path, SubmissionJournalEntry entry)
        => WriteEnvelope(
            path,
            entry,
            SubmissionJournalJsonContext.Default.SubmissionJournalEntry);

    private static T ReadEnvelope<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        RejectReparsePoint(path);
        byte[] envelopeBytes = File.ReadAllBytes(path);
        SubmissionJournalEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                envelopeBytes,
                SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope);
        }
        catch (JsonException exception)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has invalid envelope JSON.",
                exception);
        }
        if (envelope is null)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has no envelope.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException exception)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has invalid payload encoding.",
                exception);
        }

        byte[] actual = SHA256.HashData(payload);
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(envelope.Sha256);
        }
        catch (FormatException exception)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has invalid integrity metadata.",
                exception);
        }

        if (expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' failed its integrity check.");
        }

        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo)
                ?? throw new SubmissionJournalTamperedException(
                    $"Submission journal '{Path.GetFileName(path)}' has no payload.");
        }
        catch (JsonException exception)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal '{Path.GetFileName(path)}' has invalid payload JSON.",
                exception);
        }
    }

    private void WriteEnvelope<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var envelope = new SubmissionJournalEnvelope(
            Convert.ToBase64String(payload),
            Convert.ToHexString(SHA256.HashData(payload)));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope);
        string temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            SetPrivateFileMode(temporary);
            if (File.Exists(path))
            {
                DurableFileSystem.ReplaceFile(temporary, path);
            }
            else
            {
                File.Move(temporary, path);
                DurableFileSystem.FlushDirectory(_rootDirectory);
            }

            SetPrivateFileMode(path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private FileStream AcquireGlobalLock()
    {
        string path = Path.Combine(_rootDirectory, ".lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            SetPrivateFileMode(path);
            return stream;
        }
        catch (IOException exception)
        {
            throw new SubmissionJournalConflictException(
                "Another process is updating the submission journal.",
                exception);
        }
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(_rootDirectory);
        RejectReparsePoint(_rootDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _rootDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string JournalPath(string id) => Path.Combine(_rootDirectory, $"{id}.journal");

    private string IntentPath(string id) => Path.Combine(_rootDirectory, $"{id}.intent");

    private static void DeleteDurably(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string directory = Path.GetDirectoryName(path)!;
        File.Delete(path);
        DurableFileSystem.FlushDirectory(directory);
    }

    private static string ResolveRepositoryPath(string root, string repositoryPath)
    {
        string path = SecurePath.Resolve(root, repositoryPath, requireExistingLeaf: false);
        SecurePath.RejectReparsePoints(root, path);
        return path;
    }

    private static SubmissionRepositoryIdentity CreateRepositoryIdentity(string outputDirectory)
    {
        string canonical = CanonicalPath(outputDirectory);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException(
                "The output repository must exist before a submission can be prepared.");
        }

        RejectReparsePoint(canonical);
        return new(canonical, DirectoryPin.GetIdentity(canonical));
    }

    private static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static string Component(string stored, object value)
        => string.IsNullOrWhiteSpace(stored)
            ? LocalOperationPlanFingerprint.CreateComponent(value)
            : stored;

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void EnsureFingerprint(SubmissionJournalEntry entry, string expected)
    {
        if (!string.Equals(entry.LocalPlan.Fingerprint, expected, StringComparison.Ordinal))
        {
            throw new SubmissionJournalConflictException(
                "The prepared submission fingerprint does not match the requested activation.");
        }
    }

    private static void EnsureRevision(SubmissionJournalEntry entry, long expected)
    {
        if (entry.Revision != expected)
        {
            throw new SubmissionJournalConflictException(
                $"Submission journal revision changed from {expected} to {entry.Revision}.");
        }
    }

    private static void ValidateTransition(
        SubmissionJournalState current,
        SubmissionJournalState next)
    {
        bool allowed = current switch
        {
            SubmissionJournalState.Pending => next is
                SubmissionJournalState.Pending
                or SubmissionJournalState.BranchCreated
                or SubmissionJournalState.EscalationRequired,
            SubmissionJournalState.BranchCreated => next is
                SubmissionJournalState.BranchCreated
                or SubmissionJournalState.CommitCreated
                or SubmissionJournalState.EscalationRequired,
            SubmissionJournalState.CommitCreated => next is
                SubmissionJournalState.CommitCreated
                or SubmissionJournalState.PullRequestCreated
                or SubmissionJournalState.EscalationRequired,
            SubmissionJournalState.PullRequestCreated => next is
                SubmissionJournalState.PullRequestCreated
                or SubmissionJournalState.EscalationRequired,
            SubmissionJournalState.EscalationRequired => next is
                SubmissionJournalState.EscalationRequired,
            SubmissionJournalState.Cancelled => false,
            _ => false,
        };
        if (!allowed)
        {
            throw new SubmissionJournalConflictException(
                $"Invalid submission journal transition from {current} to {next}.");
        }
    }

    private static void ValidateSecretFree(GitHubSubmissionRequest request)
    {
        ValidateSecretFree(
        [
            request.ForkOwner,
            request.CreatedWith,
            request.CustomTitle,
            request.Resolves,
            request.IdempotencyKey,
            request.Policy.DuplicateHashes.OverrideAnnotation,
            .. request.VanityUrlAnnotations,
        ]);
    }

    private static void ValidateSecretFree(SubmissionJournalRemoteRequest request)
    {
        ValidateSecretFree(
        [
            request.ForkOwner,
            request.CreatedWith,
            request.CustomTitle,
            request.Resolves,
            request.IdempotencyKey,
            request.Policy.DuplicateHashes.OverrideAnnotation,
            request.Presentation.CommitTitle,
            request.Presentation.PullRequestTitle,
            request.Presentation.PullRequestBody,
            .. request.VanityUrlAnnotations,
        ]);
    }

    private static void ValidateSecretFree(IEnumerable<string?> storedText)
    {
        foreach (string value in storedText.Where(static value => value is not null)!)
        {
            if (SecretValueRegex().IsMatch(value!)
                || !string.Equals(
                    GitHubSubmissionFormatter.Redact(value!),
                    value,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Submission journal fields must not contain tokens, credentials, or unredacted secret values.",
                    nameof(storedText));
            }

            foreach (Match match in UrlRegex().Matches(value!))
            {
                if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', ']'), UriKind.Absolute, out Uri? uri)
                    && (!string.IsNullOrEmpty(uri.UserInfo)
                        || !string.IsNullOrEmpty(uri.Query)))
                {
                    throw new ArgumentException(
                        "Submission journal fields must not contain credential-bearing or query-string URLs.",
                        nameof(storedText));
                }
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new SubmissionJournalTamperedException(
                $"Submission journal path '{path}' must not be a symbolic link or reparse point.");
        }
    }

    private static void EnsureId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Submission journal IDs must be canonical GUID values.", nameof(id));
        }
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"(?ix)
        \bgh[pousr]_[A-Za-z0-9_]{20,}\b
        |\bgithub_pat_[A-Za-z0-9_]{20,}\b
        |\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{12,}
        |\b[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretValueRegex();
}
