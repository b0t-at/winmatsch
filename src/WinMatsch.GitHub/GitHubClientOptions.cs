namespace WinMatsch.GitHub;

/// <summary>Connection and retry settings for <see cref="GitHubRepositoryClient"/>.</summary>
public sealed class GitHubClientOptions
{
    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    public Uri GraphQlUri { get; init; } = new("https://api.github.com/graphql");

    public string UserAgent { get; init; } = "winmatsch";

    public int MaxTransientRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public int ForkAvailabilityMaxAttempts { get; init; } = 6;

    public TimeSpan ForkAvailabilityBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    internal Uri NormalizedApiBaseUri
    {
        get
        {
            string absoluteUri = ApiBaseUri.AbsoluteUri;
            return absoluteUri[^1] == '/'
                ? ApiBaseUri
                : new Uri(absoluteUri + "/", UriKind.Absolute);
        }
    }

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

        if (ApiBaseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(ApiBaseUri.Query) ||
            !string.IsNullOrEmpty(ApiBaseUri.Fragment))
        {
            throw new ArgumentException(
                "The GitHub API base URI must use HTTP or HTTPS and cannot contain a query or fragment.",
                nameof(ApiBaseUri));
        }

        if (GraphQlUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "The GitHub GraphQL URI must use HTTP or HTTPS.",
                nameof(GraphQlUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(UserAgent);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTransientRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryBaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ForkAvailabilityMaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ForkAvailabilityBaseDelay, TimeSpan.Zero);
    }
}
