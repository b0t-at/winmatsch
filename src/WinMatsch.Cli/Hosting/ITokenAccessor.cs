using System.ComponentModel;
using WinMatsch.GitHub.Auth;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// Lazy access to the GitHub token for one invocation, honoring the fixed precedence
/// <c>--token &gt; GITHUB_TOKEN &gt; OS keyring</c>. The token stays wrapped in
/// <see cref="GitHubToken"/> end to end, so it is redacted in every formatted output.
/// </summary>
public interface ITokenAccessor
{
    /// <summary>Resolves the token, or null when no source provides one.</summary>
    public Task<ResolvedToken?> ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the token or throws <see cref="MissingInputException"/> (exit code
    /// <see cref="ExitCodes.MissingInput"/>) naming the non-interactive channels that supply it.
    /// </summary>
    public Task<ResolvedToken> RequireAsync(CancellationToken cancellationToken = default);
}

/// <summary>The standard <see cref="ITokenAccessor"/> over <see cref="TokenResolver"/>.</summary>
public sealed class TokenAccessor : ITokenAccessor
{
    private readonly TokenResolver _resolver;
    private readonly GitHubToken? _explicitToken;

    public TokenAccessor(TokenResolver resolver, GitHubToken? explicitToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _explicitToken = explicitToken;
    }

    public async Task<ResolvedToken?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _resolver.ResolveAsync(_explicitToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TokenStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Win32Exception)
        {
            throw new TokenStoreException(
                $"The OS keyring lookup failed: {exception.Message}",
                exception);
        }
    }

    public async Task<ResolvedToken> RequireAsync(CancellationToken cancellationToken = default) =>
        await ResolveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new MissingInputException(
                "A GitHub token is required. Pass --token, set GITHUB_TOKEN, or store one in the OS keyring.");
}
