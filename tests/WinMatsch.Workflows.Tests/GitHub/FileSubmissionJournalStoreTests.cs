using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class FileSubmissionJournalStoreTests
{
    [Fact]
    public async Task Prepared_intent_recovers_only_after_exact_local_commit()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);

        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        (string handleId, string fingerprint) = handle;
        Assert.Equal(handle.Id, handleId);
        Assert.Equal(handle.LocalPlanFingerprint, fingerprint);
        Assert.Empty(await store.ListPendingAsync(default));
        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.ActivateAsync(handle, default));

        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalRecoveryResult recovered =
            await store.RecoverAsync(repository.Path, default);

        SubmissionJournalEntry entry = Assert.Single(recovered.Activated);
        Assert.Equal(handle.Id, entry.Id);
        Assert.Equal(SubmissionJournalState.Pending, entry.State);
        Assert.Equal(entry, Assert.Single(recovered.Pending));
        Assert.Single(await store.ListPendingAsync(default));
    }

    [Fact]
    public async Task Stale_handle_after_uncommitted_recovery_is_a_domain_conflict()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        SubmissionJournalHandle handle = await store.PrepareAsync(
            Request(repository.Path),
            default);

        SubmissionJournalRecoveryResult recovery = await store.RecoverAsync(
            repository.Path,
            default);
        SubmissionJournalConflictException exception =
            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                store.ActivateAsync(handle, default));

        Assert.Empty(recovery.Activated);
        Assert.Contains(
            recovery.Diagnostics,
            diagnostic => diagnostic.Contains(
                "Discarded uncommitted submission intent",
                StringComparison.Ordinal));
        Assert.Contains("no longer exists", exception.Message, StringComparison.Ordinal);
        Assert.IsType<FileNotFoundException>(exception.InnerException);
    }

    [Fact]
    public async Task Journal_enforces_cas_cancel_and_tamper_detection()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        SubmissionJournalEntry branch = await store.RecordRemoteStateAsync(
            entry.Id,
            entry.Revision,
            new()
            {
                Fork = GitHubLifecycleTestSupport.Fork,
                BranchName = "winmatsch/test",
                BranchCreated = true,
            },
            SubmissionJournalState.BranchCreated,
            errorMessage: null,
            default);

        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.RecordRemoteStateAsync(
                entry.Id,
                entry.Revision,
                branch.RemoteState,
                SubmissionJournalState.BranchCreated,
                errorMessage: null,
                default));
        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.CancelAsync(branch.Id, branch.Revision, default));

        string journal = System.IO.Path.Combine(state.Path, $"{entry.Id}.journal");
        byte[] bytes = await File.ReadAllBytesAsync(journal);
        bytes[^2] ^= 0x01;
        await File.WriteAllBytesAsync(journal, bytes);
        await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
            store.GetAsync(entry.Id, default));
        string evidence = Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(evidence));
        Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_artifact_is_quarantined_while_unrelated_package_proceeds(
        bool activate)
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest original = Request(repository.Path);
        SubmissionJournalHandle originalHandle = await store.PrepareAsync(original, default);
        string artifact = System.IO.Path.Combine(
            state.Path,
            $"{originalHandle.Id}.{(activate ? "journal" : "intent")}");
        if (activate)
        {
            WriteCommittedFile(original.LocalPlan);
            _ = await store.ActivateAsync(originalHandle, default);
        }

        byte[] corruptBytes = await File.ReadAllBytesAsync(artifact);
        corruptBytes[^2] ^= 0x01;
        await File.WriteAllBytesAsync(artifact, corruptBytes);

        SubmissionJournalHandle unrelated = await store.PrepareAsync(
            Request(repository.Path, "Other.App"),
            default);

        string evidence = Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
        Assert.EndsWith(".corrupt", evidence, StringComparison.Ordinal);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(evidence));
        Assert.Contains(
            unrelated.Diagnostics,
            diagnostic => diagnostic.Contains(
                "evidence was preserved",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(System.IO.Path.Combine(state.Path, $"{unrelated.Id}.intent")));

        SubmissionJournalTamperedException samePackage =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                store.PrepareAsync(original, default));
        Assert.Contains("unfinished remote work", samePackage.Message, StringComparison.Ordinal);
        Assert.Contains(System.IO.Path.GetFileName(evidence), samePackage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_quarantines_each_corrupt_intent_to_a_unique_evidence_file()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        byte[][] evidence =
        [
            Encoding.UTF8.GetBytes("{not-json-a"),
            Encoding.UTF8.GetBytes("{not-json-b"),
        ];
        for (int index = 0; index < evidence.Length; index++)
        {
            await File.WriteAllBytesAsync(
                System.IO.Path.Combine(state.Path, $"{Guid.NewGuid():N}.intent"),
                evidence[index]);
        }

        SubmissionJournalRecoveryResult recovery = await store.RecoverAsync(
            repository.Path,
            default);

        string[] quarantined = Directory.GetFiles(state.Path, "*.corrupt");
        Assert.Equal(2, quarantined.Length);
        Assert.Equal(2, quarantined.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, recovery.Corruptions.Length);
        Assert.Equal(2, recovery.Diagnostics.Length);
        Assert.All(quarantined, path => Assert.EndsWith(".corrupt", path, StringComparison.Ordinal));
        Assert.All(
            evidence,
            expected => Assert.Contains(
                quarantined,
                path => File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)));
        Assert.Empty(Directory.EnumerateFiles(state.Path, "*.intent"));
    }

    [Fact]
    public async Task Recovery_retains_valid_intent_when_same_id_journal_is_corrupt()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        string intent = System.IO.Path.Combine(state.Path, $"{handle.Id}.intent");
        byte[] intentBytes = await File.ReadAllBytesAsync(intent);
        WriteCommittedFile(request.LocalPlan);
        _ = await store.ActivateAsync(handle, default);
        await File.WriteAllBytesAsync(intent, intentBytes);
        string journal = System.IO.Path.Combine(state.Path, $"{handle.Id}.journal");
        byte[] journalBytes = await File.ReadAllBytesAsync(journal);
        journalBytes[^2] ^= 0x01;
        await File.WriteAllBytesAsync(journal, journalBytes);

        SubmissionJournalRecoveryResult recovery = await store.RecoverAsync(
            repository.Path,
            default);

        Assert.Empty(recovery.Activated);
        Assert.True(File.Exists(intent));
        Assert.False(File.Exists(journal));
        Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
        Assert.Contains(
            recovery.Diagnostics,
            diagnostic => diagnostic.Contains(
                "Retained submission intent",
                StringComparison.Ordinal));

        SubmissionJournalRecoveryResult repeated = await store.RecoverAsync(
            repository.Path,
            default);
        Assert.Empty(repeated.Activated);
        Assert.True(File.Exists(intent));
        await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
            store.ActivateAsync(handle, default));
    }

    [Fact]
    public async Task Recovery_reports_standalone_corrupt_journal_before_pending_selection()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        _ = await store.ActivateAsync(handle, default);
        string journal = System.IO.Path.Combine(state.Path, $"{handle.Id}.journal");
        byte[] bytes = await File.ReadAllBytesAsync(journal);
        bytes[^2] ^= 0x01;
        await File.WriteAllBytesAsync(journal, bytes);

        SubmissionJournalRecoveryResult recovery = await store.RecoverAsync(
            repository.Path,
            default);

        SubmissionJournalCorruption corruption = Assert.Single(recovery.Corruptions);
        Assert.Equal(request.LocalPlan.PackageIdentifier.Value, corruption.PackageIdentifier);
        Assert.Single(recovery.Diagnostics);
        Assert.False(File.Exists(journal));
        Assert.True(File.Exists(corruption.EvidencePath));
    }

    [Fact]
    public async Task Unknown_legacy_corruption_fails_closed_until_evidence_is_reconciled()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(state.Path, $"{Guid.NewGuid():N}.journal"),
            "{legacy-corruption");

        SubmissionJournalTamperedException exception =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                store.PrepareAsync(Request(repository.Path, "Unrelated.App"), default));

        Assert.Contains("cannot be proven unrelated", exception.Message, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
    }

    [Fact]
    public async Task Legacy_migration_preserves_original_evidence_when_scope_is_invalid()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        string journal = System.IO.Path.Combine(state.Path, $"{entry.Id}.journal");
        SubmissionJournalEnvelope envelope = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(journal),
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope)!;
        JsonObject legacy = JsonNode.Parse(Convert.FromBase64String(envelope.Payload))!.AsObject();
        _ = legacy.Remove("remoteRequestFingerprint");
        _ = legacy.Remove("remoteRequestFingerprintVersion");
        byte[] payload = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(
            new SubmissionJournalEnvelope(
                Convert.ToBase64String(payload),
                Convert.ToHexString(SHA256.HashData(payload))),
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope);
        await File.WriteAllBytesAsync(journal, original);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(state.Path, $"{entry.Id}.scope"),
            "{invalid-scope");

        await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
            store.GetAsync(entry.Id, default));

        string evidence = Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
        Assert.Equal(original, await File.ReadAllBytesAsync(evidence));
    }

    [Fact]
    public async Task Readable_mismatched_scope_is_never_trusted_for_package_attribution()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        string scopePath = System.IO.Path.Combine(state.Path, $"{entry.Id}.scope");
        SubmissionJournalEnvelope scopeEnvelope = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(scopePath),
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope)!;
        SubmissionJournalScope scope = JsonSerializer.Deserialize(
            Convert.FromBase64String(scopeEnvelope.Payload),
            SubmissionJournalJsonContext.Default.SubmissionJournalScope)!;
        byte[] mismatchedPayload = JsonSerializer.SerializeToUtf8Bytes(
            scope with { PackageIdentifier = "Other.App" },
            SubmissionJournalJsonContext.Default.SubmissionJournalScope);
        await File.WriteAllBytesAsync(
            scopePath,
            JsonSerializer.SerializeToUtf8Bytes(
                new SubmissionJournalEnvelope(
                    Convert.ToBase64String(mismatchedPayload),
                    Convert.ToHexString(SHA256.HashData(mismatchedPayload))),
                SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope));

        await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
            store.GetAsync(entry.Id, default));
        SubmissionJournalRecoveryResult recovery = await store.RecoverAsync(
            repository.Path,
            default);

        SubmissionJournalCorruption corruption = Assert.Single(recovery.Corruptions);
        Assert.Null(corruption.PackageIdentifier);
        Assert.Null(corruption.RepositoryFileSystemIdentity);
        SubmissionJournalTamperedException originalPackage =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                store.PrepareAsync(request, default));
        SubmissionJournalTamperedException claimedPackage =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                store.PrepareAsync(Request(repository.Path, "Other.App"), default));
        Assert.Contains("cannot be proven unrelated", originalPackage.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be proven unrelated", claimedPackage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interrupted_scope_quarantine_remains_a_durable_blocking_state()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        string scope = System.IO.Path.Combine(state.Path, $"{entry.Id}.scope");
        string conflict = $"{scope}.{Guid.NewGuid():N}.conflict";
        DurableFileSystem.MoveFile(scope, conflict);

        await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
            store.GetAsync(entry.Id, default));

        Assert.True(File.Exists(conflict));
        Assert.False(File.Exists(scope));
        Assert.False(File.Exists(System.IO.Path.Combine(state.Path, $"{entry.Id}.journal")));
        Assert.Single(Directory.EnumerateFiles(state.Path, "*.corrupt"));
        SubmissionJournalTamperedException retry =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                store.PrepareAsync(request, default));
        Assert.Contains("cannot be proven unrelated", retry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_keeps_journal_retryable_when_scope_cleanup_fails()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        bool failScopeDeletion = false;
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path },
            new FakeClock(),
            new ControlledLockWaitStrategy(),
            path =>
            {
                if (failScopeDeletion && path.EndsWith(".scope", StringComparison.Ordinal))
                {
                    failScopeDeletion = false;
                    throw new IOException("scope cleanup failed");
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    DurableFileSystem.FlushDirectory(System.IO.Path.GetDirectoryName(path)!);
                }
            });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        SubmissionJournalEntry branch = await store.RecordRemoteStateAsync(
            entry.Id,
            entry.Revision,
            new RemoteMutationState
            {
                Fork = GitHubLifecycleTestSupport.Fork,
                BranchName = "winmatsch/test",
                BranchCreated = true,
            },
            SubmissionJournalState.BranchCreated,
            null,
            default);
        SubmissionJournalEntry commit = await store.RecordRemoteStateAsync(
            branch.Id,
            branch.Revision,
            branch.RemoteState with
            {
                CommitCreated = true,
                CommitSha = GitHubLifecycleTestSupport.CommitSha,
            },
            SubmissionJournalState.CommitCreated,
            null,
            default);
        SubmissionJournalEntry pullRequest = await store.RecordRemoteStateAsync(
            commit.Id,
            commit.Revision,
            commit.RemoteState with
            {
                PullRequestCreated = true,
                PullRequestNumber = 42,
                PullRequestUri = new Uri("https://example.test/pull/42"),
            },
            SubmissionJournalState.PullRequestCreated,
            null,
            default);
        failScopeDeletion = true;

        await Assert.ThrowsAsync<IOException>(() =>
            store.CompleteAsync(pullRequest.Id, pullRequest.Revision, default));
        Assert.True(File.Exists(System.IO.Path.Combine(state.Path, $"{entry.Id}.journal")));

        await store.CompleteAsync(pullRequest.Id, pullRequest.Revision, default);

        Assert.False(File.Exists(System.IO.Path.Combine(state.Path, $"{entry.Id}.journal")));
        Assert.False(File.Exists(System.IO.Path.Combine(state.Path, $"{entry.Id}.scope")));
    }

    [Fact]
    public async Task Global_lock_waits_for_owner_and_then_serializes_without_sleeping()
    {
        const int raceCount = 25;
        for (int index = 0; index < raceCount; index++)
        {
            using var repository = new TemporaryDirectory();
            using var state = new TemporaryDirectory();
            var wait = new ControlledLockWaitStrategy();
            var store = new FileSubmissionJournalStore(
                new SubmissionJournalOptions
                {
                    RootDirectory = state.Path,
                    LockTimeout = TimeSpan.FromSeconds(1),
                    LockRetryDelay = TimeSpan.FromMilliseconds(10),
                },
                new FakeClock(),
                wait);
            await using var owner = new FileStream(
                System.IO.Path.Combine(state.Path, ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            Task<SubmissionJournalHandle> pending = store.PrepareAsync(
                Request(repository.Path),
                default);
            await wait.Waiting;
            Assert.False(pending.IsCompleted);

            await owner.DisposeAsync();
            wait.Release();
            SubmissionJournalHandle handle = await pending;

            Assert.True(File.Exists(System.IO.Path.Combine(state.Path, $"{handle.Id}.intent")));
        }
    }

    [Fact]
    public async Task Global_lock_timeout_is_deterministic_and_actionable()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var wait = new ControlledLockWaitStrategy(advanceTime: true);
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions
            {
                RootDirectory = state.Path,
                LockTimeout = TimeSpan.FromMilliseconds(30),
                LockRetryDelay = TimeSpan.FromMilliseconds(10),
            },
            new FakeClock(),
            wait);
        await using var owner = new FileStream(
            System.IO.Path.Combine(state.Path, ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        SubmissionJournalConflictException exception =
            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                store.PrepareAsync(Request(repository.Path), default));

        Assert.Equal(3, wait.DelayCount);
        Assert.Contains("Timed out after 30 ms", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Global_lock_wait_honors_cancellation_without_mutating_state()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var wait = new ControlledLockWaitStrategy();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path },
            new FakeClock(),
            wait);
        await using var owner = new FileStream(
            System.IO.Path.Combine(state.Path, ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellation = new CancellationTokenSource();

        Task<SubmissionJournalHandle> pending = store.PrepareAsync(
            Request(repository.Path),
            cancellation.Token);
        await wait.Waiting;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateFiles(state.Path, "*.intent"));
    }

    [Fact]
    public async Task Materializer_reconstructs_only_the_verified_committed_plan()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        var gitHub = new FakeGitHubClient();

        GitHubSubmissionRequest materialized =
            await SubmissionJournalMaterializer.MaterializeAsync(entry, gitHub, default);

        Assert.Equal(entry.LocalPlan.Fingerprint, materialized.LocalPlan.Fingerprint);
        Assert.Equal(request.IdempotencyKey, materialized.IdempotencyKey);
        Assert.True(
            request.LocalPlan.FileChanges[0].Content.AsSpan().SequenceEqual(
                materialized.LocalPlan.FileChanges[0].Content.AsSpan()));
        Assert.True(
            materialized.LocalPlan.FileChanges[0].Content.AsSpan().SequenceEqual(
                materialized.LocalPlan.Preflight.Changes[0].Content.AsSpan()));
        Assert.True(
            materialized.LocalPlan.AfterDocuments[0].Content.AsSpan().SequenceEqual(
                materialized.LocalPlan.Preflight.AfterDocuments[0].Content.AsSpan()));
        Assert.Equal(
            LocalOperationPlanFingerprint.CreatePreflightFingerprint(
                materialized.LocalPlan.Preflight),
            entry.LocalPlan.PreflightEvidenceFingerprint);

        string path = LocalPath(materialized.LocalPlan);
        await File.AppendAllTextAsync(path, "tampered");
        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            SubmissionJournalMaterializer.MaterializeAsync(entry, gitHub, default));
    }

    [Fact]
    public async Task Materializer_restores_case_insensitive_hash_policy()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest source = Request(repository.Path);
        GitHubSubmissionRequest request = source with
        {
            Policy = source.Policy with
            {
                DuplicateHashes = new DuplicateHashPolicy
                {
                    DeniedSha256 =
                        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, new string('a', 64)),
                },
            },
        };
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);

        GitHubSubmissionRequest materialized = await SubmissionJournalMaterializer.MaterializeAsync(
            entry,
            new FakeGitHubClient(),
            default);

        Assert.Contains(
            new string('A', 64),
            materialized.Policy.DuplicateHashes.DeniedSha256);
    }

    [Fact]
    public async Task Active_journal_reuse_requires_the_complete_remote_request()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        _ = await store.ActivateAsync(handle, default);

        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.PrepareAsync(
                request with { CustomTitle = "Different approved title" },
                default));
    }

    [Fact]
    public async Task Journal_rejects_tokens_in_free_text()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path) with
        {
            CustomTitle = "release " + "ghp_" + new string('A', 30),
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PrepareAsync(request, default));
    }

    [Fact]
    public async Task Journal_redacts_remote_error_diagnostics_before_persisting()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        string token = "ghp_" + new string('A', 30);

        SubmissionJournalEntry updated = await store.RecordRemoteStateAsync(
            entry.Id,
            entry.Revision,
            new()
            {
                Fork = GitHubLifecycleTestSupport.Fork,
                BranchName = "winmatsch/test",
                BranchCreated = true,
            },
            SubmissionJournalState.BranchCreated,
            $"request failed for https://example.test/path?token={token}: bearer {token}",
            default);

        Assert.DoesNotContain(token, updated.LastError, StringComparison.Ordinal);
        Assert.Contains("?token=[REDACTED]", updated.LastError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", updated.LastError, StringComparison.Ordinal);
        SubmissionJournalEntry persisted = Assert.IsType<SubmissionJournalEntry>(
            await store.GetAsync(entry.Id, default));
        Assert.Equal(updated.LastError, persisted.LastError);
    }

    [Fact]
    public async Task Legacy_journal_atomically_migrates_remote_request_fingerprint()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        GitHubSubmissionRequest request = Request(repository.Path);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);
        string path = System.IO.Path.Combine(state.Path, $"{entry.Id}.journal");
        SubmissionJournalEnvelope envelope = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(path),
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope)!;
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        JsonObject legacy = JsonNode.Parse(payload)!.AsObject();
        _ = legacy.Remove("remoteRequestFingerprint");
        _ = legacy.Remove("remoteRequestFingerprintVersion");
        byte[] legacyPayload = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        var legacyEnvelope = new SubmissionJournalEnvelope(
            Convert.ToBase64String(legacyPayload),
            Convert.ToHexString(SHA256.HashData(legacyPayload)));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                legacyEnvelope,
                SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope));

        SubmissionJournalEntry migrated = Assert.IsType<SubmissionJournalEntry>(
            await store.GetAsync(entry.Id, default));

        Assert.Equal(
            SubmissionRequestFingerprint.CurrentVersion,
            migrated.RemoteRequestFingerprintVersion);
        Assert.False(string.IsNullOrWhiteSpace(migrated.RemoteRequestFingerprint));
        Assert.Equal(
            SubmissionRequestFingerprint.Create(migrated.RemoteRequest),
            migrated.RemoteRequestFingerprint);
    }

    [Fact]
    public async Task Legacy_artifact_journal_is_readable_but_has_no_redirect_identity()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        var store = new FileSubmissionJournalStore(
            new SubmissionJournalOptions { RootDirectory = state.Path });
        const string finalUrl =
            "https://cdn.example.test/releases/setup.exe?sig=TOPSECRET&expires=1";
        GitHubSubmissionRequest request = RequestWithArtifact(repository.Path, finalUrl);
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);
        string path = System.IO.Path.Combine(state.Path, $"{handle.Id}.intent");
        SubmissionJournalEnvelope envelope = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(path),
            SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope)!;
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        string currentJson = Encoding.UTF8.GetString(payload);
        Assert.DoesNotContain("TOPSECRET", currentJson, StringComparison.Ordinal);
        JsonObject legacy = JsonNode.Parse(payload)!.AsObject();
        JsonObject artifact = legacy["entry"]!["localPlan"]!["installerArtifacts"]![0]!
            .AsObject();
        Assert.Equal(
            SubmissionJournalArtifactIdentity.CurrentFormatVersion,
            artifact["formatVersion"]!.GetValue<int>());
        Assert.Equal(
            DownloadRedirectIdentity.ComputeSha256(finalUrl),
            artifact["approvedFinalUrlSha256"]!.GetValue<string>());
        _ = artifact.Remove("formatVersion");
        _ = artifact.Remove("approvedFinalUrlSha256");
        byte[] legacyPayload = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        var legacyEnvelope = new SubmissionJournalEnvelope(
            Convert.ToBase64String(legacyPayload),
            Convert.ToHexString(SHA256.HashData(legacyPayload)));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                legacyEnvelope,
                SubmissionJournalJsonContext.Default.SubmissionJournalEnvelope));

        SubmissionJournalEntry entry = await store.ActivateAsync(handle, default);

        SubmissionJournalArtifactIdentity identity = Assert.Single(
            entry.LocalPlan.InstallerArtifacts);
        Assert.Equal(0, identity.FormatVersion);
        Assert.Null(identity.ApprovedFinalUrlSha256);
    }

    [Fact]
    public async Task Learned_override_must_be_active_before_journal_promotion()
    {
        using var repository = new TemporaryDirectory();
        using var state = new TemporaryDirectory();
        using var overrides = new TemporaryDirectory();
        GitHubSubmissionRequest source = Request(repository.Path);
        var pack = new OverridePack
        {
            PackageIdentifier = source.LocalPlan.PackageIdentifier,
            PreservedFields = ["Installer.Scope"],
        };
        GitHubSubmissionRequest request = source with
        {
            LocalPlan = source.LocalPlan with
            {
                LearnedOverride = new(pack, null, null, []),
                LearnedOverrideFingerprint =
                    LocalOperationPlanFingerprint.CreateComponent(
                        new LearnedOverridePlan(pack, null, null, [])),
            },
        };
        var store = new FileSubmissionJournalStore(new SubmissionJournalOptions
        {
            RootDirectory = state.Path,
            OverrideStoreDirectory = overrides.Path,
        });
        SubmissionJournalHandle handle = await store.PrepareAsync(request, default);
        WriteCommittedFile(request.LocalPlan);

        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.ActivateAsync(handle, default));

        var overrideStore = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = overrides.Path,
        });
        OverridePackYaml.WriteFile(
            overrideStore.ResolvePath(request.LocalPlan.PackageIdentifier),
            pack);

        SubmissionJournalEntry activated = await store.ActivateAsync(handle, default);
        Assert.Equal(SubmissionJournalState.Pending, activated.State);
    }

    [Fact]
    public async Task Lifecycle_persists_branch_commit_and_pull_request_boundaries()
    {
        var gitHub = new FakeGitHubClient();
        GitHubLifecycleWorkflow workflow = GitHubLifecycleTestSupport.Workflow(gitHub);
        var progress = new RecordingProgressSink();

        GitHubLifecycleResult result = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request(),
            progress,
            default);

        Assert.True(result.Applied);
        Assert.Equal(
            [
                SubmissionJournalState.Pending,
                SubmissionJournalState.BranchCreated,
                SubmissionJournalState.BranchCreated,
                SubmissionJournalState.CommitCreated,
                SubmissionJournalState.CommitCreated,
                SubmissionJournalState.PullRequestCreated,
            ],
            progress.States);
    }

    [Fact]
    public async Task Uncertain_resume_never_retries_a_remote_mutation()
    {
        var gitHub = new FakeGitHubClient();
        GitHubLifecycleWorkflow workflow = GitHubLifecycleTestSupport.Workflow(gitHub);
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            ResumeFrom = new()
            {
                LastAttemptedOperation = RemoteOperationKind.CreateCommit,
                RemoteOutcomeUncertain = true,
            },
        };

        GitHubLifecycleResult result = await workflow.ExecuteAsync(request);

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.Equal(0, gitHub.ContentCalls);
    }

    private static GitHubSubmissionRequest Request(
        string root,
        string packageIdentifier = "Example.App")
    {
        LocalOperationPlan source = GitHubLifecycleTestSupport.Plan();
        var package = new PackageIdentifier(packageIdentifier);
        var version = new PackageVersion("2.0.0");
        ImmutableArray<WorkflowFileChange> changes =
        [
            new(
                PlannedChangeKind.Add,
                $"{ManifestPaths.GetVersionDirectory(package, version)}/{packageIdentifier}.yaml",
                Encoding.UTF8.GetBytes($"PackageIdentifier: {packageIdentifier}"),
                ExpectedFileState.Absent),
        ];
        ImmutableArray<RawManifestDocument> documents =
        [
            new(changes[0].RepositoryPath, changes[0].Content.AsSpan()),
        ];
        LocalOperationPlan plan = source with
        {
            OutputDirectory = root,
            PackageIdentifier = package,
            FileChanges = changes,
            AfterDocuments = documents,
            Preflight = source.Preflight with
            {
                AfterDocuments = documents,
                Changes = changes,
            },
        };
        return GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = plan,
            Policy = new GitHubSubmissionPolicy { MinimumReleaseFreshness = TimeSpan.Zero },
        };
    }

    private static GitHubSubmissionRequest RequestWithArtifact(
        string root,
        string finalUrl)
    {
        GitHubSubmissionRequest source = Request(root);
        var download = new DownloadResult
        {
            FilePath = "setup.exe",
            FileName = "setup.exe",
            Sha256 = new Sha256Hash(new string('A', 64)),
            SizeInBytes = 1,
            InitialUrl = "https://example.test/setup.exe",
            FinalUrl = finalUrl,
        };
        return source with
        {
            LocalPlan = source.LocalPlan with
            {
                Preflight = source.LocalPlan.Preflight with
                {
                    InstallerArtifacts =
                    [
                        new InstallerArtifact(download.InitialUrl, download),
                    ],
                },
            },
        };
    }

    private static void WriteCommittedFile(LocalOperationPlan plan)
    {
        string path = LocalPath(plan);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, plan.FileChanges[0].Content.ToArray());
    }

    private static string LocalPath(LocalOperationPlan plan)
        => System.IO.Path.Combine(
            plan.OutputDirectory,
            plan.FileChanges[0].RepositoryPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-journal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class RecordingProgressSink : ISubmissionProgressSink
    {
        public List<SubmissionJournalState> States { get; } = [];

        public Task RecordAsync(
            RemoteMutationState remoteState,
            SubmissionJournalState state,
            CancellationToken cancellationToken)
        {
            States.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledLockWaitStrategy(bool advanceTime = false) :
        ISubmissionJournalLockWaitStrategy
    {
        private readonly TaskCompletionSource _waiting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TimeSpan _elapsed;

        public int DelayCount { get; private set; }

        public Task Waiting => _waiting.Task;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => _elapsed;

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayCount++;
            _waiting.TrySetResult();
            if (advanceTime)
            {
                _elapsed += delay;
                return;
            }

            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }
}
