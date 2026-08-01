using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
            isProductVersionTrustworthy: analysis.Format is
                DetectedInstallerFormat.Msi
                or DetectedInstallerFormat.Msix
                or DetectedInstallerFormat.MsixBundle);
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
        AtomicWorkflowFileTransaction.RecoverPending(root, packageIdentifier.Value);
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

    public async Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(outputDirectory);
        AtomicWorkflowFileTransaction.RecoverPending(root, packageIdentifier.Value);
        if (!Directory.Exists(root))
        {
            return [];
        }

        string packageDirectory = ManifestPaths.GetPackageDirectory(packageIdentifier);
        string? fullPackageDirectory = SecurePath.ResolveExactExistingDirectory(root, packageDirectory);
        if (fullPackageDirectory is null)
        {
            return [];
        }

        var snapshots = ImmutableArray.CreateBuilder<PackageSnapshot>();
        foreach (string versionDirectory in Directory.EnumerateDirectories(fullPackageDirectory)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PackageVersion.TryCreate(Path.GetFileName(versionDirectory), out PackageVersion? version))
            {
                continue;
            }

            PackageSnapshot? snapshot = await LoadAsync(
                outputDirectory,
                packageIdentifier,
                version!,
                cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots.ToImmutable();
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
        string normalizedLockKey = $"{root}\u001f{operationLockKey.ToUpperInvariant()}";
        SemaphoreSlim gate = _locks.GetOrAdd(normalizedLockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another local operation is already running for this package.");
        }

        string token = Guid.NewGuid().ToString("N");
        string transactionPrefix =
            $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationLockKey.ToUpperInvariant())))[..16]}";
        string transactionRoot = Path.Combine(root, $"{transactionPrefix}-{token}");
        var installed = new List<TransactionEntry>(changes.Length);
        var directoryPins = new List<IDisposable>();
        var pinnedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RepositoryOperationLock? processLock = null;
        bool cleanupAllowed = false;
        bool committed = false;
        Exception? committedCleanupFailure = null;
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

                installed.Add(new(change, destination, stage, backup));
            }

            processLock = RepositoryOperationLock.Acquire(root, operationLockKey);
            RecoverAbandonedTransactions(root, transactionPrefix, transactionRoot);
            foreach (TransactionEntry entry in installed)
            {
                PinExistingParent(root, entry.Destination, directoryPins, pinnedDirectories);
                VerifyPrecondition(entry);
            }

            WriteJournal(transactionRoot, "prepared", installed);
            foreach (TransactionEntry entry in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(entry.Destination)!);
                if (entry.HadDestination)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Backup)!);
                    File.Move(entry.Destination, entry.Backup);
                    entry.BackupCreated = true;
                    VerifyCapturedBackup(entry);
                }

                if (entry.Change.Kind != PlannedChangeKind.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                    SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(entry.Destination)!);
                    PinDirectory(
                        Path.GetDirectoryName(entry.Destination)!,
                        directoryPins,
                        pinnedDirectories);
                    File.Move(entry.Stage, entry.Destination);
                    entry.DestinationInstalled = true;
                    FlushFile(entry.Destination);
                }
            }

            DisposePins(directoryPins);
            DeleteEmptyManifestDirectories(root, installed);
            WriteJournal(transactionRoot, "committed", installed);
            committed = true;
            cleanupAllowed = true;
        }
        catch
        {
            try
            {
                RollBack(installed);
                cleanupAllowed = true;
            }
            catch
            {
                cleanupAllowed = false;
                throw;
            }

            throw;
        }
        finally
        {
            try
            {
                if (cleanupAllowed && Directory.Exists(transactionRoot))
                {
                    try
                    {
                        Directory.Delete(transactionRoot, recursive: true);
                    }
                    catch (IOException exception) when (committed)
                    {
                        committedCleanupFailure = exception;
                    }
                    catch (UnauthorizedAccessException exception) when (committed)
                    {
                        committedCleanupFailure = exception;
                    }
                }
            }
            finally
            {
                DisposePins(directoryPins);

                processLock?.Dispose();
                gate.Release();
            }
        }

        if (committedCleanupFailure is not null)
        {
            throw new WorkflowCommittedCleanupException(
                "The manifest transaction committed, but its recovery directory could not be removed.",
                committedCleanupFailure);
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

    private static void VerifyPrecondition(TransactionEntry entry)
    {
        bool exists = File.Exists(entry.Destination);
        entry.HadDestination = exists;
        if (entry.Change.ExpectedState == ExpectedFileState.Absent && exists)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' was created after planning.");
        }

        if (entry.Change.ExpectedState != ExpectedFileState.Present)
        {
            return;
        }

        if (!exists)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' was removed after planning.");
        }

    }

    private static void VerifyCapturedBackup(TransactionEntry entry)
    {
        if (entry.Change.ExpectedState != ExpectedFileState.Present)
        {
            return;
        }

        using FileStream stream = File.OpenRead(entry.Backup);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, entry.Change.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' changed after planning.");
        }
    }

    private static void FlushFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void DisposePins(List<IDisposable> pins)
    {
        foreach (IDisposable pin in pins)
        {
            pin.Dispose();
        }

        pins.Clear();
    }

    private static void PinExistingParent(
        string root,
        string destination,
        ICollection<IDisposable> pins,
        ISet<string> pinnedDirectories)
    {
        string? current = Path.GetDirectoryName(destination);
        while (current is not null && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        current ??= root;
        string relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Destination parent escapes the output root.");
        }

        PinDirectory(current, pins, pinnedDirectories);
    }

    private static void PinDirectory(
        string path,
        ICollection<IDisposable> pins,
        ISet<string> pinnedDirectories)
    {
        string fullPath = Path.GetFullPath(path);
        if (pinnedDirectories.Add(fullPath))
        {
            pins.Add(DirectoryPin.Acquire(fullPath));
        }
    }

    internal static void RecoverPending(string root, string operationLockKey)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        string transactionPrefix =
            $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationLockKey.ToUpperInvariant())))[..16]}";
        if (!Directory.EnumerateDirectories(root, $"{transactionPrefix}-*").Any())
        {
            return;
        }

        using RepositoryOperationLock operationLock = RepositoryOperationLock.Acquire(root, operationLockKey);
        RecoverAbandonedTransactions(root, transactionPrefix, currentTransaction: "");
    }

    private static void WriteJournal(
        string transactionRoot,
        string status,
        IReadOnlyList<TransactionEntry> entries)
    {
        string journalPath = Path.Combine(transactionRoot, "journal");
        string temporaryPath = $"{journalPath}.tmp";
        string content = string.Join(
            '\n',
            [
                status,
                .. entries.Select(static entry => string.Join(
                    '|',
                    entry.Change.Kind,
                    entry.HadDestination ? "1" : "0",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Change.RepositoryPath)))),
                "",
            ]);
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        using (FileStream stream = new(
                   temporaryPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void RecoverAbandonedTransactions(
        string root,
        string transactionPrefix,
        string currentTransaction)
    {
        foreach (string transaction in Directory.EnumerateDirectories(root, $"{transactionPrefix}-*")
                     .Where(path => !string.Equals(path, currentTransaction, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            string journalPath = Path.Combine(transaction, "journal");
            if (!File.Exists(journalPath))
            {
                Directory.Delete(transaction, recursive: true);
                continue;
            }

            string[] lines = File.ReadAllLines(journalPath);
            bool committed = lines.Length > 0 && string.Equals(lines[0], "committed", StringComparison.Ordinal);
            if (!committed)
            {
                foreach (string line in lines.Skip(1).Where(static line => line.Length > 0).Reverse())
                {
                    string[] parts = line.Split('|');
                    if (parts.Length != 3
                        || !Enum.TryParse(parts[0], out PlannedChangeKind kind))
                    {
                        throw new InvalidDataException($"Invalid transaction journal '{journalPath}'.");
                    }

                    bool hadDestination = parts[1] == "1";
                    string repositoryPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    string destination = SecurePath.Resolve(root, repositoryPath, requireExistingLeaf: false);
                    string backup = SecurePath.Resolve(
                        Path.Combine(transaction, "backup"),
                        repositoryPath,
                        requireExistingLeaf: false);
                    if (hadDestination && File.Exists(backup))
                    {
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(backup, destination);
                    }
                    else if (!hadDestination && kind != PlannedChangeKind.Delete && File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
            }

            Directory.Delete(transaction, recursive: true);
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
        string backup)
    {
        public WorkflowFileChange Change { get; } = change;
        public string Destination { get; } = destination;
        public string Stage { get; } = stage;
        public string Backup { get; } = backup;
        public bool HadDestination { get; set; }
        public bool BackupCreated { get; set; }
        public bool DestinationInstalled { get; set; }
    }

    private sealed class RepositoryOperationLock : IDisposable
    {
        private readonly FileStream _stream;
        private readonly IDisposable _rootPin;
        private readonly IDisposable _lockDirectoryPin;

        private RepositoryOperationLock(
            FileStream stream,
            IDisposable rootPin,
            IDisposable lockDirectoryPin)
        {
            _stream = stream;
            _rootPin = rootPin;
            _lockDirectoryPin = lockDirectoryPin;
        }

        public static RepositoryOperationLock Acquire(string root, string key)
        {
            string lockDirectory = Path.Combine(root, ".winmatsch-locks");
            SecurePath.RejectReparsePoints(root, root);
            IDisposable rootPin = DirectoryPin.Acquire(root);
            IDisposable? lockDirectoryPin = null;
            try
            {
                Directory.CreateDirectory(lockDirectory);
                SecurePath.RejectReparsePoints(root, lockDirectory);
                lockDirectoryPin = DirectoryPin.Acquire(lockDirectory);
                string lockPath = Path.Combine(
                    lockDirectory,
                    $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))}.lock");
                return new RepositoryOperationLock(
                    new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough),
                    rootPin,
                    lockDirectoryPin);
            }
            catch (IOException exception)
            {
                lockDirectoryPin?.Dispose();
                rootPin.Dispose();
                throw new WorkflowOperationException(
                    WorkflowResultCode.Conflict,
                    "Another process is already running a local operation for this package.",
                    exception);
            }
            catch
            {
                lockDirectoryPin?.Dispose();
                rootPin.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _lockDirectoryPin.Dispose();
            _rootPin.Dispose();
        }
    }
}

internal static class DirectoryPin
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static IDisposable Acquire(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NoopDisposable.Instance;
        }

        SafeFileHandle handle = CreateFile(
            path,
            FileListDirectory,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Unable to pin directory '{path}' against replacement (Win32 error {error}).");
        }

        return handle;
    }

#pragma warning disable SYSLIB1054 // Source-generated interop would require enabling unsafe blocks project-wide.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
#pragma warning restore SYSLIB1054

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
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

public sealed class WorkflowCommittedCleanupException : IOException
{
    public WorkflowCommittedCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
