using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinMatsch.Core;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class FileFeedbackStateStore : IFeedbackStateStore
{
    private readonly string _rootDirectory;
    private readonly Action<string> _flushDirectory;

    public FileFeedbackStateStore(string? rootDirectory = null)
        : this(rootDirectory, DurableFileSystem.FlushDirectory)
    {
    }

    internal FileFeedbackStateStore(
        string? rootDirectory,
        Action<string> flushDirectory)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinMatsch",
            "feedback");
        _flushDirectory = flushDirectory ?? throw new ArgumentNullException(nameof(flushDirectory));
    }

    public async Task PersistAsync(
        FeedbackWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        DurableFileSystem.CreateDirectoryDurably(_rootDirectory);
        string repositoryKey = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(item.Repository.ToUpperInvariant())))[..16];
        string baseName = $"{repositoryKey}-{item.PullRequestNumber}.json";
        string destination = Path.Combine(_rootDirectory, baseName);
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        string lockPath = destination + ".lock";
        try
        {
            await using FileStream writeLock = await AcquireWriteLockAsync(
                lockPath,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(destination))
            {
                await using var currentStream = new FileStream(
                    destination,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                FeedbackWorkItem? current = await JsonSerializer.DeserializeAsync(
                    currentStream,
                    GitHubWorkflowJsonContext.Default.FeedbackWorkItem,
                    cancellationToken).ConfigureAwait(false);
                if (current is not null && !ShouldReplace(current, item))
                {
                    _flushDirectory(_rootDirectory);
                    return;
                }
            }

            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    item,
                    GitHubWorkflowJsonContext.Default.FeedbackWorkItem,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            DurableFileSystem.ReplaceFile(temporary, destination);
            _flushDirectory(_rootDirectory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool ShouldReplace(
        FeedbackWorkItem current,
        FeedbackWorkItem incoming)
    {
        bool currentTerminal = current.State is FeedbackWorkState.Completed
            or FeedbackWorkState.Escalated;
        if (currentTerminal)
        {
            // A blocking validator verdict must still be recorded when an earlier observation
            // of the same pull request already ended in a non-blocking terminal state.
            return incoming.State == FeedbackWorkState.Escalated
                && UpstreamVerdictGate.IsBlocking(incoming.Classification)
                && !UpstreamVerdictGate.IsBlocking(current.Classification);
        }

        bool incomingTerminal = incoming.State is FeedbackWorkState.Completed
            or FeedbackWorkState.Escalated;
        if (incomingTerminal)
        {
            return true;
        }

        return incoming.RecordedAt > current.RecordedAt;
    }

    private static async Task<FileStream> AcquireWriteLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 100;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
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
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new IOException("Timed out acquiring the durable feedback state lock.");
    }

    public async Task<ImmutableArray<FeedbackWorkItem>> GetPendingAsync(
        string repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        Dictionary<long, FeedbackWorkItem> latest = await ReadLatestAsync(
            repository,
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. latest.Values
                .Where(item =>
                    (item.State is FeedbackWorkState.AwaitingApprovedRepair
                        or FeedbackWorkState.RetryScheduled)
                    && item.RetryAfter.GetValueOrDefault(DateTimeOffset.MinValue) <= now)
                .OrderBy(static item => item.PullRequestNumber),
        ];
    }

    public async Task<ImmutableArray<FeedbackWorkItem>> GetByPackageAsync(
        string repository,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        Dictionary<long, FeedbackWorkItem> latest = await ReadLatestAsync(
            repository,
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. latest.Values
                .Where(item => string.Equals(
                    item.PackageIdentifier,
                    packageIdentifier.Value,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static item => item.PullRequestNumber),
        ];
    }

    private async Task<Dictionary<long, FeedbackWorkItem>> ReadLatestAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<long, FeedbackWorkItem>();
        if (!Directory.Exists(_rootDirectory))
        {
            return latest;
        }

        foreach (string path in Directory.EnumerateFiles(
                     _rootDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            FeedbackWorkItem? item = await JsonSerializer.DeserializeAsync(
                stream,
                GitHubWorkflowJsonContext.Default.FeedbackWorkItem,
                cancellationToken).ConfigureAwait(false);
            if (item is null
                || !string.Equals(item.Repository, repository, StringComparison.OrdinalIgnoreCase)
                || latest.TryGetValue(item.PullRequestNumber, out FeedbackWorkItem? current)
                    && current.RecordedAt >= item.RecordedAt)
            {
                continue;
            }

            latest[item.PullRequestNumber] = item;
        }

        return latest;
    }
}
