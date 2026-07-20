using WinMatsch.Core;

namespace WinMatsch.Downloads;

/// <summary>
/// The outcome of a completed installer download, carrying the response metadata that manifest
/// generation needs later (hash, size, redirect target, Last-Modified).
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

    /// <summary>The URL exactly as requested.</summary>
    public required string InitialUrl { get; init; }

    /// <summary>
    /// The URL after following redirects. When it differs from <see cref="InitialUrl"/> the initial
    /// URL is likely a vanity/latest URL, which later flows detect and handle specially.
    /// </summary>
    public required string FinalUrl { get; init; }

    /// <summary>The value of the Content-Type response header, or null when absent.</summary>
    public string? ContentType { get; init; }
}
