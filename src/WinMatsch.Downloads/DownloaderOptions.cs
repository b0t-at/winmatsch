namespace WinMatsch.Downloads;

/// <summary>
/// Settings controlling how <see cref="InstallerDownloader"/> performs downloads.
/// The defaults are tuned for fetching installers referenced by WinGet manifests.
/// </summary>
public sealed class DownloaderOptions
{
    /// <summary>
    /// Permits plain-http URLs. Off by default: manifests should only reference https installers,
    /// so an http URL is treated as an error unless explicitly allowed.
    /// </summary>
    public bool AllowInsecureDownloads { get; set; }

    /// <summary>
    /// The User-Agent header sent with every request. Defaults to the user agent winget itself uses
    /// ("Microsoft-Delivery-Optimization/10.1") so that servers which vary their payload by client
    /// return exactly the bytes winget will later download, keeping the manifest hash valid.
    /// </summary>
    public string UserAgent { get; set; } = "Microsoft-Delivery-Optimization/10.1";

    /// <summary>
    /// The maximum number of retries after a transient failure, in addition to the initial attempt.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// The base delay for exponential backoff: the wait before retry <c>n</c> (zero-based) is this value times 2^<c>n</c>.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The timeout for a single request attempt, covering both headers and the full payload stream.
    /// Generous by default because installers can be huge.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
}
