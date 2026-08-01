using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubRepositorySubmissionEvidenceProviderTests
{
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
}
