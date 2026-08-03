namespace WinMatsch.Downloads;

/// <summary>Capacity and expiry limits for <see cref="DownloadCache"/>.</summary>
public sealed class DownloadCacheOptions
{
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(7);

    public int MaxEntries { get; set; } = 64;

    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>The maximum time to wait for another process to release the persistent cache lock.</summary>
    public TimeSpan ProcessLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The minimum age of an owned <c>*.tmp.&lt;guid&gt;</c> file before maintenance may remove it.
    /// </summary>
    public TimeSpan AbandonedTemporaryFileAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The clock used for cache TTL and HTTP freshness expiration.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal Func<CancellationToken, Task>? BeforeUnlockedInspectionRecheckAsync { get; set; }

    internal Func<string, CancellationToken, Task>? AfterUnlockedInspectionPayloadOpenAsync { get; set; }

    internal Func<CancellationToken, Task>? AfterProcessLockAcquiredAsync { get; set; }
}
