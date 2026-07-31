using WinMatsch.Core;

namespace WinMatsch.Downloads;

/// <summary>
/// The outcome of a completed installer download, carrying the stable content identity and
/// response validators needed to revalidate the artifact immediately before submission.
/// </summary>
public sealed class DownloadResult
{
    /// <summary>The full path of the downloaded file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>The (sanitized) file name the payload was saved under.</summary>
    public required string FileName { get; init; }

    /// <summary>The SHA-256 of the payload, computed incrementally while streaming — the file is never read back.</summary>
    public required Sha256Hash Sha256 { get; init; }

    /// <summary>The size of the downloaded payload in bytes.</summary>
    public required long SizeInBytes { get; init; }

    /// <summary>The value of the Last-Modified response header, or null when the server did not send one.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>The entity tag exactly as returned by the server, including weakness and quotes.</summary>
    public string? ETag { get; init; }

    /// <summary>The value of the HTTP Date response header, or null when absent.</summary>
    public DateTimeOffset? ResponseDate { get; init; }

    /// <summary>The instant after which HTTP freshness metadata requires revalidation, or null when unspecified.</summary>
    public DateTimeOffset? FreshUntil { get; init; }

    /// <summary>The time at which this representation was downloaded or revalidated.</summary>
    public DateTimeOffset RetrievedAt { get; init; }

    /// <summary>The URL exactly as requested.</summary>
    public required string InitialUrl { get; init; }

    /// <summary>
    /// The URL after following redirects. When it differs from <see cref="InitialUrl"/> the initial
    /// URL is likely a vanity/latest URL, which later flows detect and handle specially.
    /// </summary>
    public required string FinalUrl { get; init; }

    /// <summary>The value of the Content-Type response header, or null when absent.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether this result was restored from the persistent cache instead of the network.</summary>
    public bool IsFromCache { get; init; }

    /// <summary>Whether origin cache directives permit this representation to be persisted.</summary>
    public bool MayBeStored { get; init; } = true;

    /// <summary>A validator-independent identity that changes only when the payload bytes change.</summary>
    public DownloadContentIdentity ContentIdentity => new(Sha256, SizeInBytes);

    /// <summary>Returns whether the server-provided freshness metadata considers the result fresh at <paramref name="instant"/>.</summary>
    public bool IsFreshAt(DateTimeOffset instant) => FreshUntil is { } freshUntil && instant < freshUntil;
}
