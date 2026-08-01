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
    bool RecoveredFromBackup = false);

public sealed record OverridePackWriteRequest(
    PackageIdentifier PackageIdentifier,
    OverridePack Pack,
    string? ExpectedContentSha256,
    int? ExpectedFormatVersion);

public sealed record OverridePackWriteResult(
    string Path,
    string? BeforeSha256,
    string AfterSha256,
    int FormatVersion);

public sealed record OverridePackRestoreRequest(
    PackageIdentifier PackageIdentifier,
    OverridePack? PreviousPack,
    string ExpectedCurrentSha256);

public interface IOverridePackWriteStage : IAsyncDisposable
{
    public OverridePackWriteResult Result { get; }

    public Task<OverridePackWriteResult> CommitAsync(CancellationToken cancellationToken);

    public Task AbortAsync();
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

public sealed class OverridePackStoreConflictException(string message) : IOException(message);

public sealed class WorkflowCommittedLearnedOverrideException(
    string message,
    Exception innerException) : WorkflowCommittedException(message, innerException);

public sealed class FileOverridePackStore : IOverridePackStore
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

        await using FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        return LoadUnderLock(path, recover: true);
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

        string path = ResolvePath(request.PackageIdentifier);
        FileStream fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
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
            string afterHash = Hash(OverridePackYaml.Write(staged));
            return new FileOverridePackWriteStage(
                path,
                pendingPath,
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
            File.Delete(path);
            File.Delete(BackupPath(path));
            return;
        }

        OverridePackYaml.WriteFile(path, request.PreviousPack);
        OverridePackYaml.WriteFile(BackupPath(path), request.PreviousPack);
    }

    internal string ResolvePath(PackageIdentifier packageIdentifier)
    {
        string fileName = $"{packageIdentifier.Value}.yaml";
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
        if (recover)
        {
            File.Delete(PendingPath(path));
        }

        string backupPath = BackupPath(path);
        if (!File.Exists(path))
        {
            if (recover && File.Exists(backupPath))
            {
                OverridePack backup = OverridePackYaml.ReadFile(backupPath);
                OverridePackYaml.WriteFile(path, backup);
                return Snapshot(backup, recovered: true);
            }

            return new(null, null, null);
        }

        try
        {
            return Snapshot(OverridePackYaml.ReadFile(path), recovered: false);
        }
        catch (Exception exception) when (
            recover
            && exception is FormatException or DecoderFallbackException or InvalidDataException
            && File.Exists(backupPath))
        {
            OverridePack backup = OverridePackYaml.ReadFile(backupPath);
            OverridePackYaml.WriteFile(path, backup);
            return Snapshot(backup, recovered: true);
        }
    }

    private static OverridePackStoreSnapshot LoadReadOnly(string path)
    {
        string backupPath = BackupPath(path);
        if (File.Exists(path))
        {
            try
            {
                return Snapshot(OverridePackYaml.ReadFile(path), recovered: false);
            }
            catch (Exception exception) when (
                exception is FormatException or DecoderFallbackException or InvalidDataException
                && File.Exists(backupPath))
            {
                return Snapshot(OverridePackYaml.ReadFile(backupPath), recovered: true);
            }
        }

        return File.Exists(backupPath)
            ? Snapshot(OverridePackYaml.ReadFile(backupPath), recovered: true)
            : new(null, null, null);
    }

    private static OverridePackStoreSnapshot Snapshot(OverridePack pack, bool recovered)
    {
        string canonical = OverridePackYaml.Write(pack);
        return new(pack, Hash(canonical), pack.FormatVersion, recovered);
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

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FileOverridePackWriteStage(
        string path,
        string pendingPath,
        FileStream fileLock,
        OverridePackStoreSnapshot previous,
        OverridePack staged,
        OverridePackWriteResult result) : IOverridePackWriteStage
    {
        private int _completed;

        public OverridePackWriteResult Result { get; } = result;

        public async Task<OverridePackWriteResult> CommitAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                throw new InvalidOperationException("The staged override-pack write is already complete.");
            }

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

                if (previous.Pack is not null)
                {
                    OverridePackYaml.WriteFile(BackupPath(path), previous.Pack);
                }

                OverridePackYaml.WriteFile(path, staged);
                OverridePack verified = OverridePackYaml.ReadFile(path);
                if (!string.Equals(
                        Hash(OverridePackYaml.Write(verified)),
                        Result.AfterSha256,
                        StringComparison.Ordinal))
                {
                    throw new IOException("The committed override pack failed content verification.");
                }

                File.Delete(pendingPath);
                return Result;
            }
            catch
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
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            File.Delete(pendingPath);
            await fileLock.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                await AbortAsync().ConfigureAwait(false);
            }
        }
    }
}
