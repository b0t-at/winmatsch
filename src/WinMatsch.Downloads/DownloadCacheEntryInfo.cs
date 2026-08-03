namespace WinMatsch.Downloads;

/// <summary>Integrity and lifetime information returned by <see cref="DownloadCache.InspectAsync"/>.</summary>
public sealed class DownloadCacheEntryInfo
{
    public required string Url { get; init; }

    public required string CacheKey { get; init; }

    public DownloadContentIdentity? ContentIdentity { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? LastAccessedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required DownloadCacheEntryState State { get; init; }
}

public enum DownloadCacheEntryState
{
    Fresh,
    Stale,
    Corrupt,
}
