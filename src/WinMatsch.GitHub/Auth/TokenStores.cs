namespace WinMatsch.GitHub.Auth;

/// <summary>Creates the token store for the current operating system.</summary>
public static class TokenStores
{
    /// <summary>The keyring service name shared by all adapters.</summary>
    public const string ServiceName = "winmatsch";

    /// <summary>The keyring account name shared by all adapters.</summary>
    public const string AccountName = "github";

    /// <summary>
    /// Returns the keyring adapter for the current platform, or a store whose
    /// <see cref="ITokenStore.IsAvailable"/> is false on unsupported platforms.
    /// </summary>
    public static ITokenStore CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialManagerTokenStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOSKeychainTokenStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceTokenStore();
        }

        return new UnavailableTokenStore();
    }

    /// <summary>Probes a store for the <c>token status</c> command.</summary>
    public static async Task<TokenStoreStatus> GetStatusAsync(
        ITokenStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.IsAvailable)
        {
            return new TokenStoreStatus(store.ProviderName, IsAvailable: false, HasToken: null);
        }

        GitHubToken? token = await store.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return new TokenStoreStatus(store.ProviderName, IsAvailable: true, HasToken: token is not null);
    }

    private sealed class UnavailableTokenStore : ITokenStore
    {
        public string ProviderName => "none";

        public bool IsAvailable => false;

        public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubToken?>(null);

        public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default) =>
            throw new TokenStoreException("No OS keyring is available on this platform.");

        public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default) =>
            throw new TokenStoreException("No OS keyring is available on this platform.");
    }
}
