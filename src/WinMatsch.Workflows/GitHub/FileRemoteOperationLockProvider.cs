using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class FileRemoteOperationLockProvider : IRemoteOperationLockProvider
{
    private readonly RemoteOperationLockOptions _options;
    private readonly IWorkflowClock _clock;
    private readonly IRemoteLockIdentityResolver _identityResolver;

    public FileRemoteOperationLockProvider(
        RemoteOperationLockOptions? options = null,
        IWorkflowClock? clock = null)
        : this(options, clock, new CanonicalRemoteLockIdentityResolver())
    {
    }

    public FileRemoteOperationLockProvider(
        RemoteOperationLockOptions? options,
        IWorkflowClock? clock,
        IRemoteLockIdentityResolver identityResolver)
    {
        _options = options ?? new RemoteOperationLockOptions();
        _clock = clock ?? new SystemWorkflowClock();
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string repository,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_options.RootDirectory);
        string key =
            $"{_identityResolver.Resolve(repository)}\n{packageIdentifier.Value.ToUpperInvariant()}";
        string path = Path.Combine(
            _options.RootDirectory,
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.lock");
        CleanupExpiredFiles(path);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new RemoteOperationLockException(
                "Another process is already operating on this package.",
                exception);
        }

        try
        {
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 256, leaveOpen: true);
            writer.WriteLine(_clock.UtcNow.ToString("O"));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(stream));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void CleanupExpiredFiles(string currentPath)
    {
        if (_options.UnusedFileRetention <= TimeSpan.Zero)
        {
            return;
        }

        foreach (string candidate in Directory.EnumerateFiles(
                     _options.RootDirectory,
                     "*.lock",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(candidate, currentPath, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                DateTimeOffset lastWrite = File.GetLastWriteTimeUtc(candidate);
                if (_clock.UtcNow - lastWrite <= _options.UnusedFileRetention)
                {
                    continue;
                }

                string quarantine = $"{candidate}.cleanup-{Guid.NewGuid():N}";
                using (var cleanupLease = new FileStream(
                           candidate,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.Delete,
                           bufferSize: 1,
                           FileOptions.None))
                {
                    lastWrite = File.GetLastWriteTimeUtc(candidate);
                    if (_clock.UtcNow - lastWrite <= _options.UnusedFileRetention)
                    {
                        continue;
                    }

                    File.Move(candidate, quarantine);
                }

                File.Delete(quarantine);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or FileNotFoundException
                    or DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        private FileStream? _stream = stream;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class CanonicalRemoteLockIdentityResolver : IRemoteLockIdentityResolver
{
    public string Resolve(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        string value = repository.Trim();
        if (Path.IsPathRooted(value) || Directory.Exists(value) || File.Exists(value))
        {
            string fullPath = Path.GetFullPath(value);
            if (!OperatingSystem.IsWindows())
            {
                fullPath = ResolveExistingPath(fullPath);
            }

            return OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
        }

        try
        {
            RepositoryCoordinates coordinates = RepositoryCoordinates.Parse(value);
            return coordinates.ToString().ToUpperInvariant();
        }
        catch (FormatException)
        {
            string fullPath = Path.GetFullPath(value);
            return OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
        }
    }

    private static string ResolveExistingPath(string fullPath)
    {
        FileSystemInfo info = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);
        FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            return Path.GetFullPath(target.FullName);
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)
            || string.Equals(parent, fullPath, StringComparison.Ordinal))
        {
            return fullPath;
        }

        string resolvedParent = ResolveExistingPath(parent);
        return string.Equals(parent, resolvedParent, StringComparison.Ordinal)
            ? fullPath
            : Path.Combine(resolvedParent, Path.GetFileName(fullPath));
    }
}

public sealed class RemoteOperationLockException(string message, Exception? innerException = null)
    : Exception(message, innerException);
