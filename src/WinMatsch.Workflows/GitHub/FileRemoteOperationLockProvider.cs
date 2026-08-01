using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class FileRemoteOperationLockProvider : IRemoteOperationLockProvider
{
    private readonly RemoteOperationLockOptions _options;
    private readonly IWorkflowClock _clock;

    public FileRemoteOperationLockProvider(
        RemoteOperationLockOptions? options = null,
        IWorkflowClock? clock = null)
    {
        _options = options ?? new RemoteOperationLockOptions();
        _clock = clock ?? new SystemWorkflowClock();
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
        string key = $"{repository.ToUpperInvariant()}\n{packageIdentifier.Value.ToUpperInvariant()}";
        string path = Path.Combine(
            _options.RootDirectory,
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.lock");

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
            RecoverStaleMetadata(stream);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 256, leaveOpen: true);
            writer.WriteLine(_clock.UtcNow.ToString("O"));
            writer.WriteLine(Environment.ProcessId);
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

    private void RecoverStaleMetadata(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? timestamp = reader.ReadLine();
        stream.Position = 0;
        if (DateTimeOffset.TryParse(timestamp, out DateTimeOffset acquiredAt)
            && _clock.UtcNow - acquiredAt <= _options.StaleAfter)
        {
            return;
        }

        stream.SetLength(0);
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

public sealed class RemoteOperationLockException(string message, Exception? innerException = null)
    : Exception(message, innerException);
