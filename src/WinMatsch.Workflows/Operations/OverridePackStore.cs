using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Workflows.Operations;

public sealed record OverridePackStoreOptions
{
    public required string RootDirectory { get; init; }

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static OverridePackStoreOptions CreateDefault()
        => new()
        {
            RootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "winmatsch",
                "overrides"),
        };
}

public sealed record OverridePackStoreSnapshot(
    OverridePack? Pack,
    string? ContentSha256,
    int? FormatVersion,
    bool RecoveredFromBackup = false,
    bool PendingActivation = false,
    bool ActivatedFromRecovery = false,
    bool QuarantinedCorruptPrimary = false);

public sealed record OverridePackWriteRequest(
    PackageIdentifier PackageIdentifier,
    OverridePack Pack,
    string? ExpectedContentSha256,
    int? ExpectedFormatVersion,
    string? OutputDirectory = null,
    ImmutableArray<WorkflowFileChange> ManifestChanges = default);

public sealed record OverridePackWriteResult(
    string Path,
    string? BeforeSha256,
    string AfterSha256,
    int FormatVersion,
    string? Warning = null,
    bool RecoveryRetained = false);

public sealed record OverridePackRestoreRequest(
    PackageIdentifier PackageIdentifier,
    OverridePack? PreviousPack,
    string ExpectedCurrentSha256);

public interface IOverridePackWriteStage : IAsyncDisposable
{
    public OverridePackWriteResult Result { get; }

    public bool RecoveryRetained { get; }

    public Task MarkManifestCommittedAsync();

    public Task<OverridePackWriteResult> CommitAsync(CancellationToken cancellationToken);

    public Task AbortAsync();

    public Task RetainForRecoveryAsync();
}

public interface IOverridePackStore
{
    public Task<OverridePackStoreSnapshot> LoadAsync(
        PackageIdentifier packageIdentifier,
        bool allowRecoveryWrites,
        CancellationToken cancellationToken);

    public Task<OverridePackWriteResult> WriteAsync(
        OverridePackWriteRequest request,
        CancellationToken cancellationToken);

    public Task<IOverridePackWriteStage> StageAsync(
        OverridePackWriteRequest request,
        CancellationToken cancellationToken);

    public Task RestoreAsync(
        OverridePackRestoreRequest request,
        CancellationToken cancellationToken);
}

public interface IOverridePackStoreRecovery
{
    public Task<OverridePackStoreSnapshot> LoadAfterManifestRecoveryAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
}

public interface IOverridePackRecoveryLease : IAsyncDisposable
{
    public string? PendingOutputDirectory { get; }

    public Task<OverridePackStoreSnapshot> CompleteAfterManifestRecoveryAsync();
}

public interface IOverridePackCoordinatedRecovery
{
    public Task<IOverridePackRecoveryLease> AcquireRecoveryLeaseAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
}

public sealed class OverridePackStoreConflictException(string message) : IOException(message);

public sealed class OverridePackStoreRecoveryException : IOException
{
    public OverridePackStoreRecoveryException(
        string message,
        Exception? innerException = null,
        bool journalRetained = false)
        : base(message, innerException)
    {
        JournalRetained = journalRetained;
    }

    public bool JournalRetained { get; }
}

public sealed class WorkflowCommittedLearnedOverrideException(
    string message,
    Exception innerException) : WorkflowCommittedException(message, innerException);

public sealed class FileOverridePackStore :
    IOverridePackStore,
    IOverridePackStoreRecovery,
    IOverridePackCoordinatedRecovery
{
    private readonly string _rootDirectory;
    private readonly TimeSpan _lockTimeout;

    public FileOverridePackStore(OverridePackStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        if (options.LockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LockTimeout must be positive.");
        }

        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        _lockTimeout = options.LockTimeout;
    }

    public async Task<OverridePackStoreSnapshot> LoadAsync(
        PackageIdentifier packageIdentifier,
        bool allowRecoveryWrites,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        string path = ResolvePath(packageIdentifier);
        if (!allowRecoveryWrites)
        {
            return LoadReadOnly(path);
        }

        return await LoadRecoveringAsync(
            path,
            allowPreparedActivation: false,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OverridePackStoreSnapshot> LoadAfterManifestRecoveryAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        return LoadRecoveringAsync(
            ResolvePath(packageIdentifier),
            allowPreparedActivation: true,
            cancellationToken);
    }

    public async Task<IOverridePackRecoveryLease> AcquireRecoveryLeaseAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        string path = ResolvePath(packageIdentifier);
        FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            string? pendingOutputDirectory = null;
            string journalPath = JournalPath(path);
            if (File.Exists(journalPath))
            {
                try
                {
                    pendingOutputDirectory = ReadJournal(journalPath).OutputDirectory;
                }
                catch (OverridePackStoreRecoveryException exception)
                {
                    throw new OverridePackStoreRecoveryException(
                        exception.Message,
                        exception,
                        journalRetained: true);
                }
            }

            return new FileOverridePackRecoveryLease(
                path,
                fileLock,
                pendingOutputDirectory);
        }
        catch
        {
            await fileLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OverridePackStoreSnapshot> LoadRecoveringAsync(
        string path,
        bool allowPreparedActivation,
        CancellationToken cancellationToken)
    {
        await using FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        return RecoverAndLoadUnderLock(path, allowPreparedActivation);
    }

    private static OverridePackStoreSnapshot RecoverAndLoadUnderLock(
        string path,
        bool allowPreparedActivation)
    {
        RecoveryOutcome recovery = RecoverPendingUnderLock(path, allowPreparedActivation);
        OverridePackStoreSnapshot snapshot = LoadUnderLock(path, recover: true);
        return snapshot with
        {
            PendingActivation = recovery == RecoveryOutcome.Pending,
            ActivatedFromRecovery = recovery == RecoveryOutcome.Activated,
        };
    }

    public async Task<OverridePackWriteResult> WriteAsync(
        OverridePackWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using IOverridePackWriteStage stage = await StageAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        return await stage.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IOverridePackWriteStage> StageAsync(
        OverridePackWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PackageIdentifier);
        ArgumentNullException.ThrowIfNull(request.Pack);
        if (request.PackageIdentifier != request.Pack.PackageIdentifier)
        {
            throw new ArgumentException(
                "The write request and override pack identify different packages.",
                nameof(request));
        }

        bool hasManifestChanges = !request.ManifestChanges.IsDefaultOrEmpty;
        bool hasOutputDirectory = !string.IsNullOrWhiteSpace(request.OutputDirectory);
        if (hasManifestChanges != hasOutputDirectory)
        {
            throw new ArgumentException(
                "Manifest changes and their output directory must be supplied together for durable learned-override journaling.",
                nameof(request));
        }

        string path = ResolvePath(request.PackageIdentifier);
        FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (RecoverPendingUnderLock(path, allowPreparedActivation: false)
                == RecoveryOutcome.Pending)
            {
                throw new OverridePackStoreRecoveryException(
                    "A previously approved learned override is pending manifest/provenance recovery.",
                    journalRetained: true);
            }

            OverridePackStoreSnapshot current = LoadUnderLock(path, recover: true);
            if (!string.Equals(
                    current.ContentSha256,
                    request.ExpectedContentSha256,
                    StringComparison.Ordinal)
                || current.FormatVersion != request.ExpectedFormatVersion)
            {
                throw new OverridePackStoreConflictException(
                    $"Override pack '{request.PackageIdentifier.Value}' changed after review; reload and review the merged corrections again.");
            }

            string pendingPath = PendingPath(path);
            OverridePackYaml.WriteFile(pendingPath, request.Pack);
            OverridePack staged = OverridePackYaml.ReadFile(pendingPath);
            VerifyPackPath(path, staged);
            string afterHash = Hash(OverridePackYaml.Write(staged));
            string? journalPath = null;
            if (hasManifestChanges)
            {
                journalPath = JournalPath(path);
                WriteJournal(
                    journalPath,
                    OverridePackTransactionJournal.Create(
                        request.OutputDirectory!,
                        current,
                        afterHash,
                        request.ManifestChanges));
            }

            return new FileOverridePackWriteStage(
                path,
                pendingPath,
                journalPath,
                fileLock,
                current,
                staged,
                new(path, current.ContentSha256, afterHash, staged.FormatVersion));
        }
        catch
        {
            await fileLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestoreAsync(
        OverridePackRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = ResolvePath(request.PackageIdentifier);
        await using FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        OverridePackStoreSnapshot current = LoadUnderLock(path, recover: false);
        if (!string.Equals(
                current.ContentSha256,
                request.ExpectedCurrentSha256,
                StringComparison.Ordinal))
        {
            throw new OverridePackStoreConflictException(
                $"Override pack '{request.PackageIdentifier.Value}' changed before recovery could restore the reviewed state.");
        }

        if (request.PreviousPack is null)
        {
            DeleteDurably(path);
            DeleteDurably(BackupPath(path));
            return;
        }

        VerifyPackPath(path, request.PreviousPack);
        OverridePackYaml.WriteFile(path, request.PreviousPack);
        OverridePackYaml.WriteFile(BackupPath(path), request.PreviousPack);
    }

    internal string ResolvePath(PackageIdentifier packageIdentifier)
    {
        string fileName = CanonicalFileName(packageIdentifier);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException(
                "Package identifier cannot be represented as a safe override-pack file name.");
        }

        string path = Path.GetFullPath(Path.Combine(_rootDirectory, fileName));
        string relative = Path.GetRelativePath(_rootDirectory, path);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Override-pack path escapes the configured store root.");
        }

        return path;
    }

    private static OverridePackStoreSnapshot LoadUnderLock(string path, bool recover)
    {
        string backupPath = BackupPath(path);
        if (!File.Exists(path))
        {
            if (recover && File.Exists(backupPath))
            {
                OverridePack backup = ReadBackupOrThrow(path, backupPath, primaryFailure: null);
                OverridePackYaml.WriteFile(path, backup);
                return Snapshot(path, backup, recovered: true);
            }

            return new(null, null, null);
        }

        try
        {
            return Snapshot(path, OverridePackYaml.ReadFile(path), recovered: false);
        }
        catch (Exception exception) when (IsPackParseFailure(exception))
        {
            if (!recover || !File.Exists(backupPath))
            {
                throw new OverridePackStoreRecoveryException(
                    $"Learned override pack '{path}' is corrupt and has no verified backup.",
                    exception);
            }

            OverridePack backup = ReadBackupOrThrow(path, backupPath, exception);
            File.Move(path, $"{path}.corrupt", overwrite: true);
            OverridePackYaml.WriteFile(path, backup);
            return Snapshot(path, backup, recovered: true) with
            {
                QuarantinedCorruptPrimary = true,
            };
        }
    }

    private static OverridePackStoreSnapshot LoadReadOnly(string path)
    {
        bool pendingActivation = File.Exists(JournalPath(path));
        string backupPath = BackupPath(path);
        if (File.Exists(path))
        {
            try
            {
                return Snapshot(path, OverridePackYaml.ReadFile(path), recovered: false) with
                {
                    PendingActivation = pendingActivation,
                };
            }
            catch (Exception exception) when (IsPackParseFailure(exception))
            {
                if (!File.Exists(backupPath))
                {
                    throw new OverridePackStoreRecoveryException(
                        $"Learned override pack '{path}' is corrupt and has no verified backup.",
                        exception);
                }

                return Snapshot(
                    path,
                    ReadBackupOrThrow(path, backupPath, exception),
                    recovered: true) with
                {
                    PendingActivation = pendingActivation,
                };
            }
        }

        return File.Exists(backupPath)
            ? Snapshot(
                path,
                ReadBackupOrThrow(path, backupPath, primaryFailure: null),
                recovered: true) with
            {
                PendingActivation = pendingActivation,
            }
            : new(null, null, null, PendingActivation: pendingActivation);
    }

    private static OverridePack ReadBackupOrThrow(
        string primaryPath,
        string backupPath,
        Exception? primaryFailure)
    {
        try
        {
            OverridePack backup = OverridePackYaml.ReadFile(backupPath);
            VerifyPackPath(primaryPath, backup);
            return backup;
        }
        catch (Exception backupFailure) when (IsPackParseFailure(backupFailure))
        {
            Exception inner = primaryFailure is null
                ? backupFailure
                : new AggregateException(primaryFailure, backupFailure);
            throw new OverridePackStoreRecoveryException(
                $"Learned override pack '{primaryPath}' and its backup are corrupt.",
                inner);
        }
    }

    private static bool IsPackParseFailure(Exception exception)
        => exception is FormatException
            or DecoderFallbackException
            or InvalidDataException
            or ArgumentException;

    private static RecoveryOutcome RecoverPendingUnderLock(
        string path,
        bool allowPreparedActivation)
    {
        string pendingPath = PendingPath(path);
        string journalPath = JournalPath(path);
        if (!File.Exists(journalPath))
        {
            DeleteDurably(pendingPath);
            return RecoveryOutcome.None;
        }

        OverridePackTransactionJournal journal;
        try
        {
            journal = ReadJournal(journalPath);
        }
        catch (OverridePackStoreRecoveryException exception)
        {
            throw new OverridePackStoreRecoveryException(
                exception.Message,
                exception,
                journalRetained: true);
        }
        ManifestCommitState manifestState = GetManifestCommitState(journal);
        OverridePackStoreSnapshot current = LoadUnderLock(path, recover: true);
        if (manifestState == ManifestCommitState.Before)
        {
            DeleteDurably(pendingPath);
            DeleteDurably(journalPath);
            return RecoveryOutcome.Aborted;
        }

        if (manifestState != ManifestCommitState.After)
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction '{journalPath}' is retained because the manifest state is neither fully committed nor fully rolled back.",
                journalRetained: true);
        }

        if (string.Equals(journal.Status, "prepared", StringComparison.Ordinal)
            && !allowPreparedActivation)
        {
            return RecoveryOutcome.Pending;
        }

        if (string.Equals(journal.Status, "prepared", StringComparison.Ordinal))
        {
            UpdateJournalStatus(journalPath, "manifest-committed");
            journal = journal with { Status = "manifest-committed" };
        }

        if (string.Equals(current.ContentSha256, journal.AfterSha256, StringComparison.Ordinal))
        {
            DeleteDurably(pendingPath);
            DeleteDurably(journalPath);
            return RecoveryOutcome.Activated;
        }

        if (!string.Equals(
                current.ContentSha256,
                journal.BeforeSha256,
                StringComparison.Ordinal)
            || current.FormatVersion != journal.BeforeFormatVersion)
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction '{journalPath}' is retained because the active override pack no longer matches its reviewed CAS state.",
                journalRetained: true);
        }

        if (!File.Exists(pendingPath))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction '{journalPath}' is retained because its approved pending pack is missing.",
                journalRetained: true);
        }

        OverridePack pending;
        try
        {
            pending = OverridePackYaml.ReadFile(pendingPath);
            VerifyPackPath(path, pending);
        }
        catch (Exception exception) when (IsPackParseFailure(exception))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction '{journalPath}' is retained because its approved pending pack is corrupt.",
                exception,
                journalRetained: true);
        }
        if (!string.Equals(
                Hash(OverridePackYaml.Write(pending)),
                journal.AfterSha256,
                StringComparison.Ordinal))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction '{journalPath}' is retained because its approved pending pack failed fingerprint verification.",
                journalRetained: true);
        }

        Activate(path, pending, current, journal.AfterSha256);
        DeleteDurably(pendingPath);
        DeleteDurably(journalPath);
        return RecoveryOutcome.Activated;
    }

    private static ManifestCommitState GetManifestCommitState(
        OverridePackTransactionJournal journal)
    {
        bool before = true;
        bool after = true;
        foreach (OverridePackManifestChange change in journal.ManifestChanges)
        {
            string path = SecurePath.Resolve(
                journal.OutputDirectory,
                change.RepositoryPath,
                requireExistingLeaf: false);
            bool exists = File.Exists(path);
            string? hash = exists ? HashFile(path) : null;
            bool isBefore = change.ExpectedState switch
            {
                ExpectedFileState.Absent => !exists,
                ExpectedFileState.Present => exists
                    && string.Equals(hash, change.ExpectedSha256, StringComparison.Ordinal),
                _ => false,
            };
            bool isAfter = change.Kind == PlannedChangeKind.Delete
                ? !exists
                : exists && string.Equals(hash, change.AfterSha256, StringComparison.Ordinal);
            before &= isBefore;
            after &= isAfter;
        }

        if (after)
        {
            return ManifestCommitState.After;
        }

        return before ? ManifestCommitState.Before : ManifestCommitState.Mixed;
    }

    private static void Activate(
        string path,
        OverridePack staged,
        OverridePackStoreSnapshot previous,
        string expectedAfterSha256)
    {
        VerifyPackPath(path, staged);
        if (previous.Pack is not null)
        {
            OverridePackYaml.WriteFile(BackupPath(path), previous.Pack);
        }

        OverridePackYaml.WriteFile(path, staged);
        OverridePack verified = OverridePackYaml.ReadFile(path);
        if (!string.Equals(
                Hash(OverridePackYaml.Write(verified)),
                expectedAfterSha256,
                StringComparison.Ordinal))
        {
            throw new IOException("The committed override pack failed content verification.");
        }
    }

    private static OverridePackTransactionJournal ReadJournal(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 5
            || !string.Equals(lines[0], "version|1", StringComparison.Ordinal)
            || !lines[1].StartsWith("status|", StringComparison.Ordinal)
            || !lines[2].StartsWith("root|", StringComparison.Ordinal)
            || !lines[3].StartsWith("before|", StringComparison.Ordinal)
            || !lines[4].StartsWith("after|", StringComparison.Ordinal))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction journal '{path}' is invalid.");
        }

        string status = lines[1]["status|".Length..];
        if (status is not ("prepared" or "manifest-committed" or "activated"))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction journal '{path}' has unknown status '{status}'.");
        }

        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(
                Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]["root|".Length..])));
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or NotSupportedException)
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction journal '{path}' has an invalid output root.");
        }

        string[] before = lines[3].Split('|');
        string[] after = lines[4].Split('|');
        if (before.Length != 3
            || after.Length != 2
            || (before[1] != "-" && before[1].Length != 64)
            || (after[1].Length != 64)
            || (before[2] != "-" && !int.TryParse(before[2], out _)))
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction journal '{path}' has invalid CAS metadata.");
        }

        var changes = ImmutableArray.CreateBuilder<OverridePackManifestChange>();
        foreach (string line in lines.Skip(5).Where(static value => value.Length > 0))
        {
            string[] parts = line.Split('|');
            if (parts.Length != 6
                || !string.Equals(parts[0], "change", StringComparison.Ordinal)
                || !Enum.TryParse(parts[1], out PlannedChangeKind kind)
                || !Enum.TryParse(parts[2], out ExpectedFileState expectedState)
                || (parts[3] != "-" && parts[3].Length != 64)
                || (parts[4] != "-" && parts[4].Length != 64))
            {
                throw new OverridePackStoreRecoveryException(
                    $"Learned override transaction journal '{path}' has an invalid manifest entry.");
            }

            string repositoryPath;
            try
            {
                repositoryPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[5]));
                _ = SecurePath.Resolve(outputDirectory, repositoryPath, requireExistingLeaf: false);
            }
            catch (Exception exception) when (
                exception is FormatException
                    or ArgumentException
                    or InvalidDataException
                    or NotSupportedException)
            {
                throw new OverridePackStoreRecoveryException(
                    $"Learned override transaction journal '{path}' has an unsafe manifest path.");
            }

            changes.Add(new(
                kind,
                expectedState,
                parts[3] == "-" ? null : parts[3],
                parts[4] == "-" ? null : parts[4],
                repositoryPath));
        }

        if (changes.Count == 0)
        {
            throw new OverridePackStoreRecoveryException(
                $"Learned override transaction journal '{path}' contains no manifest changes.");
        }

        return new(
            status,
            outputDirectory,
            before[1] == "-" ? null : before[1],
            before[2] == "-" ? null : int.Parse(before[2]),
            after[1],
            changes.ToImmutable());
    }

    private static void WriteJournal(
        string path,
        OverridePackTransactionJournal journal)
    {
        string temporary = $"{path}.tmp";
        string content = string.Join(
            '\n',
            [
                "version|1",
                $"status|{journal.Status}",
                $"root|{Convert.ToBase64String(Encoding.UTF8.GetBytes(journal.OutputDirectory))}",
                $"before|{journal.BeforeSha256 ?? "-"}|{journal.BeforeFormatVersion?.ToString() ?? "-"}",
                $"after|{journal.AfterSha256}",
                .. journal.ManifestChanges.Select(static change => string.Join(
                    '|',
                    "change",
                    change.Kind,
                    change.ExpectedState,
                    change.ExpectedSha256 ?? "-",
                    change.AfterSha256 ?? "-",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(change.RepositoryPath)))),
                "",
            ]);
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        using (FileStream stream = new(
                   temporary,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            stream.Flush(flushToDisk: true);
        }

        DurableFileSystem.ReplaceFile(temporary, path);
    }

    private static void UpdateJournalStatus(string path, string status)
    {
        OverridePackTransactionJournal journal = ReadJournal(path);
        WriteJournal(path, journal with { Status = status });
    }

    private static OverridePackStoreSnapshot Snapshot(
        string path,
        OverridePack pack,
        bool recovered)
    {
        VerifyPackPath(path, pack);
        string canonical = OverridePackYaml.Write(pack);
        return new(pack, Hash(canonical), pack.FormatVersion, recovered);
    }

    private static string CanonicalFileName(PackageIdentifier packageIdentifier)
        => $"{packageIdentifier.Value.ToUpperInvariant()}.yaml";

    private static void VerifyPackPath(string path, OverridePack pack)
    {
        if (!string.Equals(
                Path.GetFileName(path),
                CanonicalFileName(pack.PackageIdentifier),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Learned override pack '{path}' contains package identity '{pack.PackageIdentifier}' that does not match its canonical store path.");
        }
    }

    private async Task<FileStream> AcquireLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootDirectory);
        string lockPath = $"{path}.lock";
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _lockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string BackupPath(string path) => $"{path}.bak";

    private static string PendingPath(string path) => $"{path}.pending";

    private static string JournalPath(string path) => $"{path}.transaction";

    private static void DeleteDurably(string path)
    {
        bool existed = File.Exists(path);
        File.Delete(path);
        if (existed)
        {
            DurableFileSystem.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private enum RecoveryOutcome
    {
        None,
        Pending,
        Aborted,
        Activated,
    }

    private enum ManifestCommitState
    {
        Before,
        After,
        Mixed,
    }

    private sealed record OverridePackManifestChange(
        PlannedChangeKind Kind,
        ExpectedFileState ExpectedState,
        string? ExpectedSha256,
        string? AfterSha256,
        string RepositoryPath);

    private sealed record OverridePackTransactionJournal(
        string Status,
        string OutputDirectory,
        string? BeforeSha256,
        int? BeforeFormatVersion,
        string AfterSha256,
        ImmutableArray<OverridePackManifestChange> ManifestChanges)
    {
        public static OverridePackTransactionJournal Create(
            string outputDirectory,
            OverridePackStoreSnapshot before,
            string afterSha256,
            ImmutableArray<WorkflowFileChange> manifestChanges)
        {
            string root = Path.GetFullPath(outputDirectory);
            return new(
                "prepared",
                root,
                before.ContentSha256,
                before.FormatVersion,
                afterSha256,
                [
                    .. manifestChanges
                        .OrderBy(static change => change.RepositoryPath, StringComparer.Ordinal)
                        .Select(change => new OverridePackManifestChange(
                            change.Kind,
                            change.ExpectedState,
                            change.ExpectedSha256,
                            change.Kind == PlannedChangeKind.Delete
                                ? null
                                : WorkflowFileChange.Hash(change.Content.AsSpan()),
                            change.RepositoryPath)),
                ]);
        }
    }

    private sealed class FileOverridePackWriteStage(
        string path,
        string pendingPath,
        string? journalPath,
        FileStream fileLock,
        OverridePackStoreSnapshot previous,
        OverridePack staged,
        OverridePackWriteResult result) : IOverridePackWriteStage
    {
        private int _completed;
        private int _manifestCommitted;

        public OverridePackWriteResult Result { get; } = result;

        public bool RecoveryRetained
            => File.Exists(pendingPath)
                || Directory.Exists(pendingPath)
                || journalPath is not null
                    && (File.Exists(journalPath) || Directory.Exists(journalPath));

        public Task MarkManifestCommittedAsync()
        {
            if (Volatile.Read(ref _completed) != 0)
            {
                throw new InvalidOperationException("The staged override-pack write is already complete.");
            }

            if (journalPath is not null)
            {
                UpdateJournalStatus(journalPath, "manifest-committed");
            }

            Volatile.Write(ref _manifestCommitted, 1);
            return Task.CompletedTask;
        }

        public async Task<OverridePackWriteResult> CommitAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journalPath is not null && Volatile.Read(ref _manifestCommitted) == 0)
            {
                throw new InvalidOperationException(
                    "A learned override pack cannot become active before its manifest transaction commits.");
            }

            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                throw new InvalidOperationException("The staged override-pack write is already complete.");
            }

            bool activated = false;
            try
            {
                OverridePackStoreSnapshot current = LoadUnderLock(path, recover: false);
                if (!string.Equals(
                        current.ContentSha256,
                        previous.ContentSha256,
                        StringComparison.Ordinal)
                    || current.FormatVersion != previous.FormatVersion
                    || !File.Exists(pendingPath)
                    || !string.Equals(
                        Hash(OverridePackYaml.Write(OverridePackYaml.ReadFile(pendingPath))),
                        Result.AfterSha256,
                        StringComparison.Ordinal))
                {
                    throw new OverridePackStoreConflictException(
                        "The staged override pack changed before it could be committed.");
                }

                if (journalPath is not null)
                {
                    OverridePackTransactionJournal journal = ReadJournal(journalPath);
                    if (!string.Equals(
                            journal.Status,
                            "manifest-committed",
                            StringComparison.Ordinal)
                        || GetManifestCommitState(journal) != ManifestCommitState.After)
                    {
                        throw new OverridePackStoreRecoveryException(
                            "The approved learned override remains inactive because the manifest transaction is not durably committed.",
                            journalRetained: true);
                    }

                }

                Activate(path, staged, previous, Result.AfterSha256);
                activated = true;
                try
                {
                    if (journalPath is not null)
                    {
                        UpdateJournalStatus(journalPath, "activated");
                    }
                    DeleteDurably(pendingPath);
                    if (journalPath is not null)
                    {
                        DeleteDurably(journalPath);
                    }
                }
                catch (Exception cleanupException)
                    when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    return Result with
                    {
                        Warning = "The learned override is active, but its recovery artifacts could not be removed.",
                        RecoveryRetained = RecoveryRetained,
                    };
                }

                return Result;
            }
            catch when (!activated)
            {
                Interlocked.Exchange(ref _completed, 0);
                throw;
            }
            finally
            {
                if (Volatile.Read(ref _completed) == 1)
                {
                    await fileLock.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public async Task AbortAsync()
        {
            if (Volatile.Read(ref _manifestCommitted) != 0)
            {
                await RetainForRecoveryAsync().ConfigureAwait(false);
                return;
            }

            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            Exception? cleanupFailure = null;
            try
            {
                DeleteDurably(pendingPath);
                if (journalPath is not null)
                {
                    DeleteDurably(journalPath);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            try
            {
                await fileLock.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanupFailure is null
                    ? exception
                    : new AggregateException(cleanupFailure, exception);
            }

            if (cleanupFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }

        public async Task RetainForRecoveryAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            await fileLock.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                if (Volatile.Read(ref _manifestCommitted) == 0)
                {
                    await AbortAsync().ConfigureAwait(false);
                }
                else
                {
                    await RetainForRecoveryAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class FileOverridePackRecoveryLease(
        string path,
        FileStream fileLock,
        string? pendingOutputDirectory) : IOverridePackRecoveryLease
    {
        private int _completed;

        public string? PendingOutputDirectory { get; } = pendingOutputDirectory;

        public Task<OverridePackStoreSnapshot> CompleteAfterManifestRecoveryAsync()
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "The learned-override recovery lease is already complete.");
            }

            return Task.FromResult(RecoverAndLoadUnderLock(
                path,
                allowPreparedActivation: true));
        }

        public async ValueTask DisposeAsync()
        {
            await fileLock.DisposeAsync().ConfigureAwait(false);
        }
    }
}
