using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubRepositorySubmissionEvidenceProviderTests
{
    [Fact]
    public async Task Code_search_reports_installer_hashes_published_under_another_identifier()
    {
        var client = new FakeGitHubClient();
        ConfigureTrees(client);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes("{}"));
        string hash = new('C', 64);
        GitHubSubmissionRequest request = RequestWithInstaller(hash);
        client.CodeSearchMatches[hash] =
        [
            new("manifests/s/StacksLabs/Clarinet/3.23.1/StacksLabs.Clarinet.installer.yaml", "abc"),
            new("manifests/e/Example/App/2.0.0/Example.App.installer.yaml", "def"),
            new("README.md", "ghi"),
        ];

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                request,
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        RepositoryInstallerEvidence moved = Assert.Single(
            evidence.InstallerEvidence,
            item => item.PackageIdentifier == new PackageIdentifier("StacksLabs.Clarinet"));
        Assert.Equal(hash, moved.InstallerSha256);
        Assert.Equal("3.23.1", moved.PackageVersion.Value);
        Assert.DoesNotContain(
            evidence.InstallerEvidence,
            static item => item.ManifestPath.EndsWith("Example.App.installer.yaml", StringComparison.Ordinal));
        Assert.Empty(evidence.Notes);
    }

    [Fact]
    public async Task Unavailable_code_search_only_adds_a_note()
    {
        var client = new FakeGitHubClient
        {
            CodeSearchFailure = new GitHubApiException("GitHub code search returned incomplete results."),
        };
        ConfigureTrees(client);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes("{}"));

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                RequestWithInstaller(new string('D', 64)),
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        string note = Assert.Single(evidence.Notes);
        Assert.Contains("installer-hash search", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blind_code_search_is_probed_with_a_published_hash_and_skipped()
    {
        // GitHub code search does not index microsoft/winget-pkgs: every query returns nothing,
        // so GH1011 never fired for PatrickHener.Goshs or Grandpied33.STH. A published hash
        // that is not found proves the index is blind; the empty answer must not read as clean.
        PackageIdentifier package = new("MongoDB.Compass.Community");
        var client = new FakeGitHubClient();
        ConfigureTrees(client);
        string communityPath =
            "manifests/m/MongoDB/Compass/Community/1.0.0/MongoDB.Compass.Community.installer.yaml";
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            communityPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(package, new string('A', 64)));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            "manifests/m/MongoDB/Compass/Full/1.0.0/MongoDB.Compass.Full.installer.yaml",
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(new PackageIdentifier("MongoDB.Compass.Full"), new string('B', 64)));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes("{}"));
        string hash = new('E', 64);
        client.CodeSearchMatches[hash] =
        [
            new("manifests/o/Other/Compass/2.0.0/Other.Compass.installer.yaml", "abc"),
        ];
        GitHubSubmissionRequest request = RequestWithInstaller(hash);
        request = request with
        {
            LocalPlan = request.LocalPlan with { PackageIdentifier = package },
        };

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                request,
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        Assert.Contains(
            evidence.Notes,
            static note => note.Contains("does not index", StringComparison.Ordinal));
        Assert.DoesNotContain(
            evidence.InstallerEvidence,
            static item => item.PackageIdentifier == new PackageIdentifier("Other.Compass"));
    }

    [Fact]
    public async Task Moved_identifier_is_found_through_pull_request_titles_and_its_manifest()
    {
        // PatrickHener.Goshs 2.1.6 was submitted although GoshsLabs.Goshs 2.1.6 already
        // carried the same installers; moderators had to remove it again.
        var client = new FakeGitHubClient();
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes("{}"));
        string hash = new('F', 64);
        var moved = new PackageIdentifier("GoshsLabs.Goshs");
        var version = new PackageVersion("2.1.6");
        string movedPath = $"{ManifestPaths.GetVersionDirectory(moved, version)}/GoshsLabs.Goshs.installer.yaml";
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            movedPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(moved, hash));
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(423160, PullRequestState.Closed) with
        {
            Title = "New version: GoshsLabs.Goshs 2.1.6",
            Body = "",
        });
        GitHubSubmissionRequest request = RequestWithInstaller(hash);
        request = request with
        {
            LocalPlan = request.LocalPlan with
            {
                PackageIdentifier = new PackageIdentifier("PatrickHener.Goshs"),
                PackageVersion = version,
            },
        };

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                request,
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        RepositoryInstallerEvidence duplicate = Assert.Single(
            evidence.InstallerEvidence,
            item => item.PackageIdentifier == moved);
        Assert.Equal(hash, duplicate.InstallerSha256);
        Assert.Equal(movedPath, duplicate.ManifestPath);
    }

    [Fact]
    public async Task Reads_policy_and_sibling_hashes_from_pinned_upstream_sha()
    {
        PackageIdentifier package = new("MongoDB.Compass.Community");
        var client = new FakeGitHubClient();
        ConfigureTrees(client);
        string communityPath =
            "manifests/m/MongoDB/Compass/Community/1.0.0/MongoDB.Compass.Community.installer.yaml";
        string fullPath =
            "manifests/m/MongoDB/Compass/Full/1.0.0/MongoDB.Compass.Full.installer.yaml";
        string sharedHash = new string('A', 64);
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            communityPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(package, sharedHash));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            fullPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(new PackageIdentifier("MongoDB.Compass.Full"), sharedHash));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes(
                $$"""
                {
                  "retiredIdentifiers": ["{{package.Value}}"],
                  "duplicateHashes": {
                    "deniedSha256": ["{{new string('B', 64)}}"],
                    "allowedSha256": ["{{new string('C', 64)}}"],
                    "overrideAnnotation": "Repository-approved duplicate."
                  },
                  "vanityUrlAnnotations": {
                    "{{package.Value}}": ["Stable vanity URL revalidated at submission."]
                  }
                }
                """));
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = GitHubLifecycleTestSupport.Plan() with
            {
                PackageIdentifier = package,
            },
        };

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                request,
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        Assert.Contains(
            evidence.InstallerEvidence,
            item => item.PackageIdentifier == package && item.RetiredIdentifier);
        Assert.Contains(
            evidence.InstallerEvidence,
            item => item.PackageIdentifier == new PackageIdentifier("MongoDB.Compass.Full")
                && item.InstallerSha256 == sharedHash);
        Assert.Contains(new string('B', 64), evidence.DuplicateHashes.DeniedSha256);
        Assert.Contains(new string('C', 64), evidence.DuplicateHashes.AllowedSha256);
        Assert.Equal(
            "Repository-approved duplicate.",
            evidence.DuplicateHashes.OverrideAnnotation);
        Assert.Equal(
            ["Stable vanity URL revalidated at submission."],
            evidence.VanityUrlAnnotations.ToArray());
        Assert.All(
            client.ContentRequests,
            request => Assert.Equal(
                GitHubLifecycleTestSupport.UpstreamSha,
                request.Reference));
        Assert.All(
            client.TreeCalls,
            request => Assert.Contains(
                request.Treeish,
                new[]
                {
                    GitHubLifecycleTestSupport.UpstreamSha,
                    "tree-manifests",
                    "tree-m",
                    "tree-mongodb",
                    "tree-compass",
                    "tree-community",
                    "tree-full",
                }));
    }

    [Fact]
    public async Task Missing_policy_and_package_tree_return_empty_evidence()
    {
        var client = new FakeGitHubClient();

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                GitHubLifecycleTestSupport.Request(),
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        Assert.Empty(evidence.InstallerEvidence);
        Assert.Empty(evidence.DuplicateHashes.DeniedSha256);
        Assert.Empty(evidence.DuplicateHashes.AllowedSha256);
        Assert.Null(evidence.DuplicateHashes.OverrideAnnotation);
        Assert.Empty(evidence.VanityUrlAnnotations);
    }

    [Fact]
    public async Task Resolves_package_tree_case_insensitively_but_reads_canonical_repository_paths()
    {
        PackageIdentifier requestedPackage = new("mongodb.compass.community");
        var client = new FakeGitHubClient();
        ConfigureTrees(client);
        string canonicalPath =
            "manifests/m/MongoDB/Compass/Community/1.0.0/MongoDB.Compass.Community.installer.yaml";
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            canonicalPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(new PackageIdentifier("MongoDB.Compass.Community"), new string('A', 64)));
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            "manifests/m/MongoDB/Compass/Full/1.0.0/MongoDB.Compass.Full.installer.yaml",
            GitHubLifecycleTestSupport.UpstreamSha,
            InstallerYaml(new PackageIdentifier("MongoDB.Compass.Full"), new string('A', 64)));
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = GitHubLifecycleTestSupport.Plan() with
            {
                PackageIdentifier = requestedPackage,
            },
        };

        RepositorySubmissionEvidence evidence =
            await new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                request,
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None);

        Assert.Contains(
            evidence.InstallerEvidence,
            item => item.PackageIdentifier == new PackageIdentifier("MongoDB.Compass.Full"));
        Assert.Contains(
            client.ContentRequests,
            item => string.Equals(item.Path, canonicalPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_policy_is_reported_as_bounded_repository_evidence_failure()
    {
        var client = new FakeGitHubClient();
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            "not-json"u8);

        await Assert.ThrowsAsync<RepositorySubmissionEvidenceException>(() =>
            new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                GitHubLifecycleTestSupport.Request(),
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_pinned_tree_is_reported_as_bounded_repository_evidence_failure()
    {
        var client = new FakeGitHubClient
        {
            TreeFailure = new GitHubApiException(
                "GitHub truncated tree.",
                statusCode: null,
                requestId: null,
                errorKind: GitHubApiErrorKind.TreeTruncated),
        };

        await Assert.ThrowsAsync<RepositorySubmissionEvidenceException>(() =>
            new GitHubRepositorySubmissionEvidenceProvider(client).GetEvidenceAsync(
                GitHubLifecycleTestSupport.Request(),
                GitHubLifecycleTestSupport.UpstreamSha,
                CancellationToken.None));
    }

    private static void ConfigureTrees(FakeGitHubClient client)
    {
        RepositoryCoordinates repository = GitHubLifecycleTestSupport.Upstream;
        client.SetTree(
            repository,
            GitHubLifecycleTestSupport.UpstreamSha,
            recursive: false,
            new RepositoryTreeEntry(
                "manifests",
                "tree-manifests",
                RepositoryTreeEntryType.Tree,
                null));
        client.SetTree(
            repository,
            "tree-manifests",
            recursive: false,
            new RepositoryTreeEntry("m", "tree-m", RepositoryTreeEntryType.Tree, null));
        client.SetTree(
            repository,
            "tree-m",
            recursive: false,
            new RepositoryTreeEntry(
                "MongoDB",
                "tree-mongodb",
                RepositoryTreeEntryType.Tree,
                null));
        client.SetTree(
            repository,
            "tree-mongodb",
            recursive: false,
            new RepositoryTreeEntry(
                "Compass",
                "tree-compass",
                RepositoryTreeEntryType.Tree,
                null));
        client.SetTree(
            repository,
            "tree-compass",
            recursive: false,
            new RepositoryTreeEntry(
                "Community",
                "tree-community",
                RepositoryTreeEntryType.Tree,
                null),
            new RepositoryTreeEntry(
                "Full",
                "tree-full",
                RepositoryTreeEntryType.Tree,
                null));
        client.SetTree(
            repository,
            "tree-community",
            recursive: true,
            new RepositoryTreeEntry(
                "1.0.0/MongoDB.Compass.Community.installer.yaml",
                "blob-community",
                RepositoryTreeEntryType.Blob,
                1));
        client.SetTree(
            repository,
            "tree-full",
            recursive: true,
            new RepositoryTreeEntry(
                "1.0.0/MongoDB.Compass.Full.installer.yaml",
                "blob-full",
                RepositoryTreeEntryType.Blob,
                1));
    }

    private static byte[] InstallerYaml(
        PackageIdentifier packageIdentifier,
        string hash)
    {
        var manifest = new InstallerManifest
        {
            PackageIdentifier = packageIdentifier,
            PackageVersion = new PackageVersion("1.0.0"),
            Installers =
            [
                new Installer
                {
                    Architecture = Architecture.X64,
                    InstallerType = InstallerType.Exe,
                    InstallerUrl = "https://example.test/app.exe",
                    InstallerSha256 = new Sha256Hash(hash),
                },
            ],
        };
        return Encoding.UTF8.GetBytes(ManifestYamlWriter.Serialize(manifest));
    }

    private static GitHubSubmissionRequest RequestWithInstaller(string sha256)
    {
        const string installerUrl = "https://example.invalid/app.exe";
        LocalOperationPlan plan = GitHubLifecycleTestSupport.Plan();
        return GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = plan with
            {
                Preflight = plan.Preflight with
                {
                    InstallerArtifacts =
                    [
                        new(installerUrl, new WinMatsch.Downloads.DownloadResult
                        {
                            FilePath = "app.exe",
                            FileName = "app.exe",
                            Sha256 = new Sha256Hash(sha256),
                            SizeInBytes = 1,
                            RetrievedAt = DateTimeOffset.UtcNow,
                            InitialUrl = installerUrl,
                            FinalUrl = installerUrl,
                        }),
                    ],
                },
            },
        };
    }
}
