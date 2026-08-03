namespace WinMatsch.Downloads;

internal sealed class DownloadDestinationHooks
{
    public Action<string>? BeforeLockWait { get; init; }

    public Func<string, CancellationToken, Task>? AfterPublishAsync { get; init; }

    public Func<string, int, CancellationToken, Task>? BeforeSharingViolationRetryAsync { get; init; }
}
