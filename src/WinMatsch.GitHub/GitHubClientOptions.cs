namespace WinMatsch.GitHub;

/// <summary>Connection and retry settings for <see cref="GitHubRepositoryClient"/>.</summary>
public sealed class GitHubClientOptions
{
    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    public Uri GraphQlUri { get; init; } = new("https://api.github.com/graphql");

    public string UserAgent { get; init; } = "winmatsch";

    public int MaxTransientRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    internal void Validate()
    {
        if (!ApiBaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The GitHub API base URI must be absolute.", nameof(ApiBaseUri));
        }

        if (!GraphQlUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The GitHub GraphQL URI must be absolute.", nameof(GraphQlUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(UserAgent);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTransientRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryBaseDelay, TimeSpan.Zero);
    }
}
