namespace WinMatsch.GitHub.Auth;

/// <summary>
/// A secure token store backed by an OS keyring. Implementations must never place the secret
/// in exception messages, logs, or child-process arguments.
/// </summary>
public interface ITokenStore
{
    /// <summary>A human-readable name of the backing keyring, safe for display.</summary>
    public string ProviderName { get; }

    /// <summary>Whether the backing keyring can be used on this machine.</summary>
    public bool IsAvailable { get; }

    /// <summary>Reads the stored token, or null when none is stored.</summary>
    public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the token, replacing any previously stored one.</summary>
    public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored token. Returns false when nothing was stored.</summary>
    public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default);
}
