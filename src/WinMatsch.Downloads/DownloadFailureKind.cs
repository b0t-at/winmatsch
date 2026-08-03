namespace WinMatsch.Downloads;

/// <summary>Classifies download failures so callers can make safe retry and submission decisions.</summary>
public enum DownloadFailureKind
{
    /// <summary>A transient transport, timeout, throttling, or server failure exhausted its retries.</summary>
    TransientNetwork,

    /// <summary>The server permanently rejected the request.</summary>
    PermanentHttp,

    /// <summary>The bytes on disk no longer match the previously established content identity.</summary>
    ContentChanged,

    /// <summary>A persistent cache entry failed metadata or payload integrity validation.</summary>
    CacheCorruption,

    /// <summary>A local destination file could not be created, written, or atomically replaced.</summary>
    LocalFile,
}
