namespace WinMatsch.GitHub.Auth;

/// <summary>Validates a token against the GitHub API without ever exposing the secret.</summary>
/// <remarks>
/// Implemented by the GitHub transport layer. Implementations must send the token only in the
/// Authorization header and must keep it out of URLs, logs, recordings, and error messages.
/// </remarks>
public interface ITokenValidator
{
    public Task<TokenValidationResult> ValidateAsync(GitHubToken token, CancellationToken cancellationToken = default);
}
