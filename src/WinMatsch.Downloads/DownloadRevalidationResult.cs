namespace WinMatsch.Downloads;

/// <summary>The outcome of verifying a downloaded installer against its origin.</summary>
public sealed class DownloadRevalidationResult
{
    /// <summary>Whether the payload identity remained stable.</summary>
    public required DownloadRevalidationStatus Status { get; init; }

    /// <summary>The current downloaded representation and response metadata.</summary>
    public required DownloadResult Result { get; init; }

    /// <summary>Whether the server confirmed the representation with HTTP 304.</summary>
    public bool WasNotModifiedResponse { get; init; }
}

/// <summary>Content-level revalidation outcomes.</summary>
public enum DownloadRevalidationStatus
{
    /// <summary>The payload bytes are identical to the original download.</summary>
    Unchanged,

    /// <summary>The payload bytes changed and submission must stop.</summary>
    ContentChanged,
}
