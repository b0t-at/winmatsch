using System.Net;

namespace WinMatsch.Downloads;

/// <summary>Base class for categorized download failures. Cancellation remains an <see cref="OperationCanceledException"/>.</summary>
public abstract class DownloadException : Exception
{
    protected DownloadException(string message, DownloadFailureKind failureKind, Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    /// <summary>The actionable failure category.</summary>
    public DownloadFailureKind FailureKind { get; }
}

/// <summary>A transient network or server failure that persisted after configured retries.</summary>
public sealed class DownloadNetworkException : DownloadException
{
    public DownloadNetworkException(string message, Exception innerException)
        : base(message, DownloadFailureKind.TransientNetwork, innerException)
    {
    }
}

/// <summary>A permanent HTTP response that must not be retried without changing the request.</summary>
public sealed class DownloadHttpException : DownloadException
{
    public DownloadHttpException(HttpStatusCode statusCode, string requestUrl)
        : base($"The server returned HTTP {(int)statusCode} ({statusCode}) for '{requestUrl}'.", DownloadFailureKind.PermanentHttp)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status returned by the server.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>The local artifact no longer matches the identity that was previously downloaded.</summary>
public sealed class DownloadContentChangedException : DownloadException
{
    public DownloadContentChangedException(
        DownloadContentIdentity expected,
        DownloadContentIdentity actual,
        string filePath)
        : base($"The installer at '{filePath}' changed after download.", DownloadFailureKind.ContentChanged)
    {
        Expected = expected;
        Actual = actual;
        FilePath = filePath;
    }

    /// <summary>The identity established by the original download.</summary>
    public DownloadContentIdentity Expected { get; }

    /// <summary>The identity computed immediately before revalidation.</summary>
    public DownloadContentIdentity Actual { get; }

    /// <summary>The local file that failed verification.</summary>
    public string FilePath { get; }
}

/// <summary>A cache entry was malformed, incomplete, or did not match its recorded integrity data.</summary>
public sealed class DownloadCacheCorruptionException : DownloadException
{
    public DownloadCacheCorruptionException(string cachePath, string message, Exception? innerException = null)
        : base(message, DownloadFailureKind.CacheCorruption, innerException)
    {
        CachePath = cachePath;
    }

    /// <summary>The corrupt metadata or payload path.</summary>
    public string CachePath { get; }
}

/// <summary>A local filesystem operation failed independently of the network transport.</summary>
public sealed class DownloadFileException : DownloadException
{
    public DownloadFileException(string filePath, string message, Exception innerException)
        : base(message, DownloadFailureKind.LocalFile, innerException)
    {
        FilePath = filePath;
    }

    /// <summary>The destination file involved in the failed operation.</summary>
    public string FilePath { get; }
}
