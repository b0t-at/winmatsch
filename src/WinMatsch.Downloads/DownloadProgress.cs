namespace WinMatsch.Downloads;

/// <summary>
/// A point-in-time snapshot of an in-flight download, reported through <see cref="IProgress{T}"/>.
/// </summary>
/// <param name="BytesReceived">The number of payload bytes received so far.</param>
/// <param name="TotalBytes">The expected total size from the Content-Length header, or null when the server did not announce one.</param>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes);
