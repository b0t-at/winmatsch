using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Workflows.Operations;

public sealed record LocalOperationLockOptions
{
    public string RootDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winmatsch",
        "operation-locks");
}

public sealed class FileLocalOperationLockProvider : ILocalOperationLockProvider
{
    private readonly string _rootDirectory;

    public FileLocalOperationLockProvider(LocalOperationLockOptions? options = null)
    {
        _rootDirectory = Path.GetFullPath(
            (options ?? new LocalOperationLockOptions()).RootDirectory);
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);
        SetPrivateDirectoryMode(_rootDirectory);

        string repository = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            repository = repository.ToUpperInvariant();
        }

        string key = $"{repository}\n{packageIdentifier.Value.ToUpperInvariant()}";
        string path = Path.Combine(
            _rootDirectory,
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256,
                FileOptions.WriteThrough);
            SetPrivateFileMode(path);
            stream.SetLength(0);
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(false),
                       bufferSize: 256,
                       leaveOpen: true))
            {
                writer.WriteLine(repository);
                writer.WriteLine(packageIdentifier.Value);
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(stream));
        }
        catch (IOException exception)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another verified local operation is already running for this package.",
                exception);
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
