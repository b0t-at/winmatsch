namespace WinMatsch.Downloads;

/// <summary>Capacity and expiry limits for <see cref="DownloadCache"/>.</summary>
public sealed class DownloadCacheOptions
{
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(7);

    public int MaxEntries { get; set; } = 64;

    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>The clock used for cache TTL and HTTP freshness expiration.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal Func<CancellationToken, Task>? BeforeUnlockedInspectionRecheckAsync { get; set; }

    internal Func<string, CancellationToken, Task>? AfterUnlockedInspectionPayloadOpenAsync { get; set; }
}
