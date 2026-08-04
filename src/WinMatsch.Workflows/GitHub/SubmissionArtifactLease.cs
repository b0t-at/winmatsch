namespace WinMatsch.Workflows.GitHub;

internal interface ISubmissionArtifactDirectoryCleanup
{
    public ValueTask DeleteAsync(string directory);
}

internal sealed class BoundedSubmissionArtifactDirectoryCleanup :
    ISubmissionArtifactDirectoryCleanup
{
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    ];

    public static BoundedSubmissionArtifactDirectoryCleanup Instance { get; } = new();

    public async ValueTask DeleteAsync(string directory)
    {
        Exception? lastFailure = null;
        foreach (TimeSpan delay in _retryDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
            }
        }

        throw new IOException(
            "The journaled submission artifact directory could not be removed after bounded retries.",
            lastFailure);
    }
}

internal sealed class SubmissionArtifactLease : IAsyncDisposable
{
    private readonly ISubmissionArtifactDirectoryCleanup _cleanup;
    private int _active = 1;

    private SubmissionArtifactLease(
        string directory,
        ISubmissionArtifactDirectoryCleanup cleanup)
    {
        DirectoryPath = directory;
        _cleanup = cleanup;
    }

    public string DirectoryPath { get; }

    public static SubmissionArtifactLease Create(
        string? rootDirectory = null,
        ISubmissionArtifactDirectoryCleanup? cleanup = null)
    {
        string root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Path.GetTempPath(), "winmatsch-journal-artifacts")
                : rootDirectory);
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new(
            directory,
            cleanup ?? BoundedSubmissionArtifactDirectoryCleanup.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        await _cleanup.DeleteAsync(DirectoryPath).ConfigureAwait(false);
    }
}
