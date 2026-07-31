using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.Diagnostics;
using Xunit;

namespace WinMatsch.Workflows.Tests.Diagnostics;

public sealed class DiagnosticServicesTests
{
    [Fact]
    public async Task Analyze_local_pe_reports_file_and_dependency_evidence()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "WinMatsch.Workflows.exe");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.dll"), path);
            var service = new InstallerDiagnosticService();

            InstallerDiagnosticResult result = await service.AnalyzeAsync(
                new InstallerAnalysisRequest(path, CacheEnabled: false, CacheDirectory: null));

            Assert.False(result.IsRemote);
            Assert.Equal(64, result.Sha256.Length);
            Assert.NotEmpty(result.Analysis.Installers);
            Assert.NotEmpty(result.Dependencies.Evidence);
            Assert.All(
                result.Dependencies.Evidence,
                evidence => Assert.Equal(Path.GetFileName(path), evidence.PayloadPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_observes_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new InstallerDiagnosticService();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.AnalyzeAsync(
                new InstallerAnalysisRequest("missing.exe", false, null),
                cancellation.Token));
    }

    [Fact]
    public async Task Analyze_treats_colon_relative_input_as_a_local_path()
    {
        bool downloaderCreated = false;
        var service = new InstallerDiagnosticService(options =>
        {
            downloaderCreated = true;
            return new WinMatsch.Downloads.InstallerDownloader(options);
        });

        Exception? exception = await Record.ExceptionAsync(() =>
            service.AnalyzeAsync(
                new InstallerAnalysisRequest("release:setup.exe", false, null)));

        Assert.NotNull(exception);
        Assert.False(downloaderCreated);
        Assert.DoesNotContain("URI scheme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_validation_never_claims_origin_hash_validation_passed()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WritePackage(directory);
            var service = new ManifestValidationService();

            ManifestValidationResult result = await service.ValidateAsync(
                new ManifestValidationRequest(
                    [directory],
                    Offline: true,
                    WarningPolicy.Allow,
                    CacheEnabled: false,
                    CacheDirectory: null,
                    ConcurrentDownloads: 1));

            Assert.Equal(NetworkValidationMode.Offline, result.NetworkMode);
            Assert.Contains(result.Report.Findings, finding => finding.Code == "VLD5001");
            Assert.Contains(result.Report.Findings, finding => finding.Code == "VLD6001");
            Assert.False(result.Report.CanProceed());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_reads_exact_canonical_version_and_normalizes_files()
    {
        (FakeGitHubRepositoryClient client, PackageManifests manifests) = CreateRepository();
        var service = new RepositoryDiagnosticService(client);

        PackageVersionResult result = await service.GetPackageVersionAsync(
            Repository,
            manifests.Version.PackageIdentifier!,
            manifests.Version.PackageVersion!,
            normalize: true);

        Assert.True(result.Normalized);
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.StartsWith("# yaml-language-server:", file.Content));
        Assert.All(client.RecursiveFlags, Assert.False);
    }

    [Fact]
    public async Task Repository_rejects_identifier_with_wrong_casing()
    {
        (FakeGitHubRepositoryClient client, _) = CreateRepository();
        var service = new RepositoryDiagnosticService(client);

        DiagnosticNotFoundException exception = await Assert.ThrowsAsync<DiagnosticNotFoundException>(
            () => service.ListVersionsAsync(
                Repository,
                new PackageIdentifier("example.App"),
                skip: 0,
                limit: 100));

        Assert.Contains("exact casing 'Example'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_versions_uses_package_version_order_and_paginates()
    {
        (FakeGitHubRepositoryClient client, PackageManifests manifests) = CreateRepository();
        var service = new RepositoryDiagnosticService(client);

        PackageVersionsResult result = await service.ListVersionsAsync(
            Repository,
            manifests.Version.PackageIdentifier!,
            skip: 1,
            limit: 2);

        Assert.Equal(4, result.Total);
        Assert.Equal(["2.0.0", "2.0.0-rc"], result.Versions.Select(static value => value.Value));
        Assert.Equal(1, result.Skip);
        Assert.Equal(2, result.Limit);
    }

    private static RepositoryCoordinates Repository { get; } = new("owner", "repo");

    private static (FakeGitHubRepositoryClient Client, PackageManifests Manifests) CreateRepository()
    {
        PackageManifests manifests = CreateManifests();
        IReadOnlyDictionary<string, string> files = PackageManifestIO.SerializeFiles(manifests);
        var client = new FakeGitHubRepositoryClient();
        client.AddTree("root", Tree("manifests", "manifests-sha"));
        client.AddTree("manifests-sha", Tree("e", "letter-sha"));
        client.AddTree("letter-sha", Tree("Example", "publisher-sha"));
        client.AddTree("publisher-sha", Tree("App", "package-sha"));
        client.AddTree(
            "package-sha",
            Tree("1.0.0", "v1"),
            Tree("2.0.0-rc", "v2rc"),
            Tree("2.0.0", "v2"),
            Tree("10.0", "v10"));
        client.AddTree(
            "v2",
            files.Keys.Select((name, index) => Blob(name, $"file-{index}")).ToArray());

        string versionDirectory = ManifestPaths.GetVersionDirectory(
            manifests.Version.PackageIdentifier!,
            manifests.Version.PackageVersion!);
        foreach ((string name, string content) in files)
        {
            client.AddContent(
                $"{versionDirectory}/{name}",
                content);
        }

        return (client, manifests);
    }

    private static PackageManifests CreateManifests()
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion("2.0.0");
        var locale = new LanguageTag("en-US");
        return new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Exe,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://example.test/app.exe",
                        InstallerSha256 = new Sha256Hash(
                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example app",
            },
            Locales = [],
        };
    }

    private static void WritePackage(string directory)
    {
        foreach ((string name, string content) in PackageManifestIO.SerializeFiles(CreateManifests()))
        {
            File.WriteAllText(Path.Combine(directory, name), content);
        }
    }

    private static RepositoryTreeEntry Tree(string path, string sha)
        => new(path, sha, RepositoryTreeEntryType.Tree, null);

    private static RepositoryTreeEntry Blob(string path, string sha)
        => new(path, sha, RepositoryTreeEntryType.Blob, 10);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "winmatsch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class FakeGitHubRepositoryClient : IGitHubRepositoryClient
{
    private readonly Dictionary<string, IReadOnlyList<RepositoryTreeEntry>> _trees =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryContent> _contents =
        new(StringComparer.Ordinal);

    public RateLimitInfo? LastRateLimit => null;

    public List<bool> RecursiveFlags { get; } = [];

    public event EventHandler<RateLimitInfo>? RateLimitObserved
    {
        add { }
        remove { }
    }

    public void AddTree(string sha, params RepositoryTreeEntry[] entries) =>
        _trees.Add(sha, entries);

    public void AddContent(string path, string content) =>
        _contents.Add(
            path,
            new RepositoryContent(
                Path.GetFileName(path),
                path,
                "content-sha",
                content.Length,
                "base64",
                System.Text.Encoding.UTF8.GetBytes(content)));

    public Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new BranchState("main", "root", false));

    public Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecursiveFlags.Add(recursive);
        return Task.FromResult(
            _trees.TryGetValue(treeish, out IReadOnlyList<RepositoryTreeEntry>? entries)
                ? entries
                : throw new InvalidOperationException($"Unknown tree '{treeish}'."));
    }

    public Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _contents.TryGetValue(path, out RepositoryContent? content)
                ? content
                : throw new InvalidOperationException($"Unknown content '{path}'."));
    }

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
