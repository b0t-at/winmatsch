namespace WinMatsch.GitHub.Auth;

/// <summary>The outcome of validating a token against the GitHub API.</summary>
public sealed record TokenValidationResult
{
    private TokenValidationResult()
    {
    }

    public required bool IsValid { get; init; }

    /// <summary>The authenticated login when the token is valid.</summary>
    public string? Login { get; init; }

    /// <summary>The granted OAuth scopes, when the API reports them.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>A display-safe failure description. Must never contain the token.</summary>
    public string? FailureReason { get; init; }

    public static TokenValidationResult Valid(string login, IReadOnlyList<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        return new TokenValidationResult { IsValid = true, Login = login, Scopes = scopes ?? [] };
    }

    public static TokenValidationResult Invalid(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return new TokenValidationResult { IsValid = false, FailureReason = failureReason };
    }
}
