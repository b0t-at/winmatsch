using System.Collections.Immutable;
using System.Text;
using WinMatsch.Rules.OverridePacks;
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
        Assert.Empty(await store.ListPendingAsync(default));
        await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
            store.ActivateAsync(handle, default));

        WriteCommittedFile(request.LocalPlan);
        SubmissionJournalRecoveryResult recovered =
            await store.RecoverAsync(repository.Path, default);

        SubmissionJournalEntry entry = Assert.Single(recovered.Activated);
        Assert.Equal(handle.Id, entry.Id);
        Assert.Equal(SubmissionJournalState.Pending, entry.State);
        Assert.Single(await store.ListPendingAsync(default));
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

    private static GitHubSubmissionRequest Request(string root)
    {
        LocalOperationPlan source = GitHubLifecycleTestSupport.Plan();
        LocalOperationPlan plan = source with { OutputDirectory = root };
        return GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = plan,
            Policy = new GitHubSubmissionPolicy { MinimumReleaseFreshness = TimeSpan.Zero },
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
}
