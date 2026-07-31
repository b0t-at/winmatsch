namespace WinMatsch.Downloads;

/// <summary>Lightweight origin metadata obtained without downloading the installer body.</summary>
public sealed class DownloadProbeResult
{
    public required string InitialUrl { get; init; }

    public required string FinalUrl { get; init; }

    public required DownloadProbeMethod Method { get; init; }

    public long? SizeInBytes { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public DateTimeOffset? ResponseDate { get; init; }

    public DateTimeOffset? FreshUntil { get; init; }

    public string? ContentType { get; init; }

    public bool SupportsRanges { get; init; }
}

/// <summary>The HTTP method that successfully produced probe metadata.</summary>
public enum DownloadProbeMethod
{
    Head,
    RangeGet,
}
