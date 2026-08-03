using System.Collections.Concurrent;
using WinMatsch.Core;

namespace WinMatsch.Downloads;

internal static class DownloadDestination
{
    private const int CopyBufferSize = 81920;
    private const int SharingViolationErrorCode = 32;
    private const int SharingViolationRetryCount = 7;

    private static readonly TimeSpan[] _sharingViolationRetryDelays =
    [
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(160),
        TimeSpan.FromMilliseconds(320),
    ];

    private static readonly ConcurrentDictionary<string, DestinationGate> _destinationGates =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> PublishAsync(
        string temporaryPath,
        string preferredPath,
        DownloadContentIdentity identity,
        DownloadDestinationHooks? hooks,
        CancellationToken cancellationToken)
    {
        string normalizedPreferredPath = Path.GetFullPath(preferredPath);
        PublishOutcome preferredOutcome = await TryPublishAsync(
            temporaryPath,
            normalizedPreferredPath,
            identity,
            hooks,
            cancellationToken).ConfigureAwait(false);
        if (preferredOutcome != PublishOutcome.Conflict)
        {
            return normalizedPreferredPath;
        }

        string contentPath = GetContentAddressedPath(normalizedPreferredPath, identity.Sha256);
        PublishOutcome contentOutcome = await TryPublishAsync(
            temporaryPath,
            contentPath,
            identity,
            hooks,
            cancellationToken).ConfigureAwait(false);
        if (contentOutcome == PublishOutcome.Conflict)
        {
            throw new DownloadFileException(
                contentPath,
                $"The content-addressed destination '{contentPath}' contains bytes that do not match its SHA-256 name.",
                new InvalidDataException("A content-addressed destination collision was detected."));
        }

        return contentPath;
    }

    private static async Task<PublishOutcome> TryPublishAsync(
        string temporaryPath,
        string destinationPath,
        DownloadContentIdentity identity,
        DownloadDestinationHooks? hooks,
        CancellationToken cancellationToken)
    {
        await using DestinationLease lease = await AcquireDestinationLeaseAsync(
            destinationPath,
            hooks,
            cancellationToken).ConfigureAwait(false);

        if (File.Exists(destinationPath))
        {
            DownloadContentIdentity existing = await ComputeIdentityAsync(
                destinationPath,
                hooks,
                cancellationToken).ConfigureAwait(false);
            if (existing == identity)
            {
                TryDelete(temporaryPath);
                return PublishOutcome.Matched;
            }

            return PublishOutcome.Conflict;
        }

        try
        {
            File.Move(temporaryPath, destinationPath);
            if (hooks?.AfterPublishAsync is { } afterPublish)
            {
                await afterPublish(destinationPath, cancellationToken).ConfigureAwait(false);
            }

            return PublishOutcome.Published;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            DownloadContentIdentity existing = await ComputeIdentityAsync(
                destinationPath,
                hooks,
                cancellationToken).ConfigureAwait(false);
            if (existing == identity)
            {
                TryDelete(temporaryPath);
                return PublishOutcome.Matched;
            }

            return PublishOutcome.Conflict;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException(
                destinationPath,
                $"Failed to atomically publish the installer to '{destinationPath}'.",
                exception);
        }
    }

    private static async ValueTask<DestinationLease> AcquireDestinationLeaseAsync(
        string destinationPath,
        DownloadDestinationHooks? hooks,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DestinationGate gate = _destinationGates.GetOrAdd(
                destinationPath,
                static _ => new DestinationGate());
            lock (gate.SyncRoot)
            {
                if (gate.IsRetired)
                {
                    continue;
                }

                gate.ReferenceCount++;
            }

            try
            {
                hooks?.BeforeLockWait?.Invoke(destinationPath);
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new DestinationLease(destinationPath, gate);
            }
            catch
            {
                ReleaseDestinationReference(destinationPath, gate, releaseSemaphore: false);
                throw;
            }
        }
    }

    private static void ReleaseDestinationReference(
        string destinationPath,
        DestinationGate gate,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        lock (gate.SyncRoot)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount != 0)
            {
                return;
            }

            gate.IsRetired = true;
            _ = ((ICollection<KeyValuePair<string, DestinationGate>>)_destinationGates).Remove(
                new KeyValuePair<string, DestinationGate>(destinationPath, gate));
        }
    }

    private static string GetContentAddressedPath(string preferredPath, Sha256Hash sha256)
    {
        string? directory = Path.GetDirectoryName(preferredPath);
        if (directory is null)
        {
            throw new InvalidOperationException($"The destination '{preferredPath}' has no parent directory.");
        }

        string extension = Path.GetExtension(preferredPath);
        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        return Path.Combine(directory, $"sha256-{sha256.Normalized.ToLowerInvariant()}{extension}");
    }

    private static async Task<DownloadContentIdentity> ComputeIdentityAsync(
        string path,
        DownloadDestinationHooks? hooks,
        CancellationToken cancellationToken)
    {
        for (int retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                Sha256Hash sha256 = await Sha256Hash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
                return new DownloadContentIdentity(sha256, stream.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException exception) when (
                IsWindowsSharingViolation(exception)
                && retry < SharingViolationRetryCount)
            {
                if (hooks?.BeforeSharingViolationRetryAsync is { } beforeRetry)
                {
                    await beforeRetry(path, retry + 1, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(_sharingViolationRetryDelays[retry], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DownloadFileException(
                    path,
                    $"Failed to verify the existing destination '{path}'.",
                    exception);
            }
        }
    }

    private static bool IsWindowsSharingViolation(IOException exception)
        => OperatingSystem.IsWindows()
            && (exception.HResult & 0xFFFF) == SharingViolationErrorCode;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DestinationGate
    {
        public object SyncRoot { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool IsRetired { get; set; }
    }

    private sealed class DestinationLease(
        string destinationPath,
        DestinationGate gate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseDestinationReference(
                    destinationPath,
                    gate,
                    releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private enum PublishOutcome
    {
        Published,
        Matched,
        Conflict,
    }
}
