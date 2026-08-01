using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Operations;

public sealed class GitHubWorkflowReleaseSource(
    IGitHubRepositoryClient client,
    RepositoryCoordinates repository) : IWorkflowReleaseSource
{
    private readonly IGitHubRepositoryClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken)
    {
        _ = packageIdentifier ?? throw new ArgumentNullException(nameof(packageIdentifier));
        IReadOnlyList<GitHubRelease> releases = await _client.GetReleasesAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        IEnumerable<GitHubRelease> selected = string.IsNullOrWhiteSpace(request.Release)
            ? releases
            : releases.Where(release =>
                string.Equals(release.TagName, request.Release, StringComparison.Ordinal)
                || string.Equals(release.Name, request.Release, StringComparison.Ordinal));
        ImmutableArray<DiscoveredAsset> discovered = ReleaseAssetDiscovery.Discover(selected);
        if (request.InstallerUrls.IsEmpty)
        {
            return discovered;
        }

        HashSet<string> urls = request.InstallerUrls
            .Select(static uri => uri.AbsoluteUri)
            .ToHashSet(StringComparer.Ordinal);
        var direct = request.InstallerUrls
            .Where(uri => !discovered.Any(asset =>
                string.Equals(asset.DownloadUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal)))
            .OrderBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .Select((uri, index) => new DiscoveredAsset
            {
                ReleaseId = 0,
                ReleaseTag = request.Release ?? "",
                ReleaseName = request.Release ?? "direct URL",
                ReleaseUri = request.ReleaseUrls.FirstOrDefault() ?? uri,
                IsPrerelease = false,
                AssetId = index,
                AssetName = Path.GetFileName(uri.LocalPath),
                DownloadUri = uri,
                DeclaredContentType = "application/octet-stream",
                DeclaredSize = 0,
                AssetCreatedAt = DateTimeOffset.UnixEpoch,
            });
        return
        [
            .. discovered
                .Where(asset => urls.Contains(asset.DownloadUri.AbsoluteUri))
                .Concat(direct)
                .OrderBy(static asset => asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal),
        ];
    }
}

public sealed class InstallerWorkflowArtifactProcessor(
    InstallerDownloader downloader,
    PayloadDependencyAnalyzer? dependencyAnalyzer = null) : IWorkflowArtifactProcessor
{
    private readonly InstallerDownloader _downloader =
        downloader ?? throw new ArgumentNullException(nameof(downloader));
    private readonly PayloadDependencyAnalyzer _dependencyAnalyzer =
        dependencyAnalyzer ?? new PayloadDependencyAnalyzer();

    public async Task<ArtifactSnapshot> AcquireAsync(
        DiscoveredAsset asset,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        DownloadResult download = await _downloader.DownloadAsync(
            asset.DownloadUri.AbsoluteUri,
            artifactDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerAnalysis analysis = await Task.Run(
            () => FileAnalyzer.AnalyzeFile(download.FilePath),
            cancellationToken).ConfigureAwait(false);
        PayloadDependencyAnalysis? dependencies = null;
        if (Path.GetExtension(download.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(download.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            await using FileStream stream = File.OpenRead(download.FilePath);
            dependencies = _dependencyAnalyzer.Analyze(stream, download.FileName);
        }

        AssetContentEvidence content = AssetContentEvidence.FromDownload(download);
        AssetAnalysisEvidence analysisEvidence = AssetAnalysisEvidence.FromAnalysis(
            analysis,
            content,
            dependencies,
            isProductVersionTrustworthy: true);
        return new()
        {
            Asset = asset with { Content = content, Analysis = analysisEvidence },
            Download = download,
            Analysis = analysis,
            DependencyAnalysis = dependencies,
        };
    }
}

public sealed class LocalManifestSnapshotSource : IManifestSnapshotSource
{
    public Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(root))
        {
            return Task.FromResult<PackageSnapshot?>(null);
        }

        string relativeDirectory = ManifestPaths.GetVersionDirectory(packageIdentifier, packageVersion);
        string? versionDirectory = SecurePath.ResolveExactExistingDirectory(root, relativeDirectory);
        if (versionDirectory is null)
        {
            return Task.FromResult<PackageSnapshot?>(null);
        }

        SecurePath.RejectReparsePoints(root, versionDirectory);
        PackageManifests manifests = PackageManifestIO.LoadDirectory(versionDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<RawManifestDocument> documents =
        [
            .. Directory.EnumerateFiles(versionDirectory)
                .Where(static path => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new RawManifestDocument(
                    $"{relativeDirectory}/{Path.GetFileName(path)}",
                    File.ReadAllBytes(path))),
        ];
        return Task.FromResult<PackageSnapshot?>(new PackageSnapshot
        {
            PackageIdentifier = packageIdentifier,
            PackageVersion = packageVersion,
            VersionDirectory = relativeDirectory,
            Manifests = manifests,
            Documents = documents,
        });
    }
}

public sealed class AtomicWorkflowFileTransaction : IWorkflowFileTransaction
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task ApplyAsync(
        string outputDirectory,
        string operationLockKey,
        ImmutableArray<WorkflowFileChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLockKey);
        if (changes.IsEmpty)
        {
            return;
        }

        string root = Path.GetFullPath(outputDirectory);
        string normalizedLockKey = $"{root}\u001f{operationLockKey}";
        SemaphoreSlim gate = _locks.GetOrAdd(normalizedLockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another local operation is already running for this package.");
        }

        string token = Guid.NewGuid().ToString("N");
        string transactionRoot = Path.Combine(root, $".winmatsch-transaction-{token}");
        var installed = new List<TransactionEntry>(changes.Length);
        CrossProcessOperationLock? processLock = null;
        try
        {
            SecurePath.ValidateOutputRoot(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(transactionRoot);
            string stageRoot = Path.Combine(transactionRoot, "stage");
            string backupRoot = Path.Combine(transactionRoot, "backup");
            Directory.CreateDirectory(stageRoot);
            Directory.CreateDirectory(backupRoot);

            foreach (WorkflowFileChange change in changes.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = SecurePath.Resolve(root, change.RepositoryPath, requireExistingLeaf: false);
                SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(destination)!);
                string stage = SecurePath.Resolve(stageRoot, change.RepositoryPath, requireExistingLeaf: false);
                string backup = SecurePath.Resolve(backupRoot, change.RepositoryPath, requireExistingLeaf: false);
                if (change.Kind != PlannedChangeKind.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                    await File.WriteAllBytesAsync(stage, change.Content.ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                }

                installed.Add(new(change, destination, stage, backup, File.Exists(destination)));
            }

            // Named Mutex ownership is thread-affine, so acquire only after the final await.
            processLock = CrossProcessOperationLock.Acquire(normalizedLockKey);
            foreach (TransactionEntry entry in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.HadDestination)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Backup)!);
                    File.Move(entry.Destination, entry.Backup);
                    entry.BackupCreated = true;
                }

                if (entry.Change.Kind != PlannedChangeKind.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                    File.Move(entry.Stage, entry.Destination);
                    entry.DestinationInstalled = true;
                }
            }

            DeleteEmptyManifestDirectories(root, installed);
        }
        catch
        {
            RollBack(installed);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(transactionRoot))
                {
                    Directory.Delete(transactionRoot, recursive: true);
                }
            }
            finally
            {
                processLock?.Dispose();
                gate.Release();
            }
        }
    }

    private static void RollBack(IReadOnlyList<TransactionEntry> entries)
    {
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            TransactionEntry entry = entries[index];
            if (entry.DestinationInstalled && File.Exists(entry.Destination))
            {
                File.Delete(entry.Destination);
            }

            if (entry.BackupCreated && File.Exists(entry.Backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                File.Move(entry.Backup, entry.Destination);
            }
        }
    }

    private static void DeleteEmptyManifestDirectories(string root, IEnumerable<TransactionEntry> entries)
    {
        foreach (string directory in entries
                     .Where(static entry => entry.Change.Kind == PlannedChangeKind.Delete)
                     .Select(static entry => Path.GetDirectoryName(entry.Destination)!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(static path => path.Length))
        {
            string current = directory;
            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(current)
                   && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
    }

    private sealed class TransactionEntry(
        WorkflowFileChange change,
        string destination,
        string stage,
        string backup,
        bool hadDestination)
    {
        public WorkflowFileChange Change { get; } = change;
        public string Destination { get; } = destination;
        public string Stage { get; } = stage;
        public string Backup { get; } = backup;
        public bool HadDestination { get; } = hadDestination;
        public bool BackupCreated { get; set; }
        public bool DestinationInstalled { get; set; }
    }

    private sealed class CrossProcessOperationLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private CrossProcessOperationLock(Mutex mutex, bool ownsMutex)
        {
            _mutex = mutex;
            _ownsMutex = ownsMutex;
        }

        public static CrossProcessOperationLock Acquire(string key)
        {
            string name = $"WinMatsch.Workflow.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
            var mutex = new Mutex(initiallyOwned: false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new WorkflowOperationException(
                    WorkflowResultCode.Conflict,
                    "Another process is already running a local operation for this package.");
            }

            return new(mutex, ownsMutex: true);
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }

            _mutex.Dispose();
        }
    }
}

internal static class SecurePath
{
    public static string? ResolveExactExistingDirectory(string root, string repositoryPath)
    {
        string normalized = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        string current = Path.GetFullPath(root);
        foreach (string expectedSegment in normalized.Split('/'))
        {
            string? actualSegment = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName)
                .SingleOrDefault(name => string.Equals(name, expectedSegment, StringComparison.OrdinalIgnoreCase));
            if (actualSegment is null)
            {
                return null;
            }

            if (!string.Equals(actualSegment, expectedSegment, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package path segment casing is '{actualSegment}', expected '{expectedSegment}'.");
            }

            current = Path.Combine(current, actualSegment);
            if (!Directory.Exists(current))
            {
                return null;
            }
        }

        return current;
    }

    public static void ValidateOutputRoot(string root)
    {
        string? existing = root;
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        if (existing is not null)
        {
            string pathRoot = Path.GetPathRoot(existing)
                ?? throw new InvalidDataException("The output path has no filesystem root.");
            string current = pathRoot;
            foreach (string segment in Path.GetRelativePath(pathRoot, existing).Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Output path '{current}' contains a reparse point.");
                }
            }
        }
    }

    public static string Resolve(string root, string repositoryPath, bool requireExistingLeaf)
    {
        string normalized = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Path '{repositoryPath}' escapes the output directory.");
        }

        if (requireExistingLeaf && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"Path '{repositoryPath}' does not exist.", fullPath);
        }

        return fullPath;
    }

    public static void RejectReparsePoints(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Path is outside the trusted output root.");
        }

        string current = fullRoot;
        if (Directory.Exists(current)
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Output path '{current}' is a reparse point.");
        }

        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Output path '{current}' contains a reparse point.");
            }
        }
    }
}

public sealed class WorkflowOperationException : Exception
{
    public WorkflowOperationException(WorkflowResultCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public WorkflowResultCode Code { get; }
}
