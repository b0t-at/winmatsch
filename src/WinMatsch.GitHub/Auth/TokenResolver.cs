namespace WinMatsch.GitHub.Auth;

/// <summary>
/// Resolves the GitHub token using the fixed precedence
/// explicit option &gt; <c>GITHUB_TOKEN</c> environment variable &gt; OS keyring.
/// </summary>
public sealed class TokenResolver
{
    /// <summary>The environment variable consulted between the explicit option and the keyring.</summary>
    public const string TokenEnvironmentVariable = "GITHUB_TOKEN";

    private readonly ITokenStore _store;
    private readonly Func<string, string?> _environment;

    /// <param name="store">The OS keyring adapter, typically from <see cref="TokenStores.CreateDefault"/>.</param>
    /// <param name="environment">
    /// Environment lookup, injectable for tests. Defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </param>
    public TokenResolver(ITokenStore store, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Resolves a token, or returns null when no source provides one. An unavailable or empty
    /// keyring is not an error; callers decide whether a missing token is fatal.
    /// </summary>
    public async Task<ResolvedToken?> ResolveAsync(
        GitHubToken? explicitToken = null,
        CancellationToken cancellationToken = default)
    {
        if (explicitToken is not null)
        {
            return new ResolvedToken(explicitToken, TokenSource.ExplicitOption);
        }

        string? fromEnvironment = _environment(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new ResolvedToken(new GitHubToken(fromEnvironment.Trim()), TokenSource.EnvironmentVariable);
        }

        if (_store.IsAvailable)
        {
            GitHubToken? stored = await _store.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return new ResolvedToken(stored, TokenSource.Keyring);
            }
        }

        return null;
    }
}
