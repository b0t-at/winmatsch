namespace WinMatsch.Downloads;

/// <summary>Capacity and expiry limits for <see cref="DownloadCache"/>.</summary>
public sealed class DownloadCacheOptions
{
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(7);

    public int MaxEntries { get; set; } = 64;

    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}
