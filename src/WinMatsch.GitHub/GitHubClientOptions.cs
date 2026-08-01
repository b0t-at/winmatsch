namespace WinMatsch.GitHub;

/// <summary>Connection and retry settings for <see cref="GitHubRepositoryClient"/>.</summary>
public sealed class GitHubClientOptions
{
    /// <summary>The REST API root. For GitHub Enterprise Server this is normally <c>https://host/api/v3</c>.</summary>
    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    /// <summary>
    /// The GraphQL endpoint. When omitted, it is derived on the REST API origin
    /// (<c>/graphql</c> for github.com and <c>/api/graphql</c> for GHES).
    /// </summary>
    public Uri? GraphQlUri { get; init; }

    public string UserAgent { get; init; } = "winmatsch";

    public int MaxTransientRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxPaginationPages { get; init; } = 100;

    public int MaxPaginationItems { get; init; } = 10_000;

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

    internal Uri ResolvedGraphQlUri => GraphQlUri ?? DeriveGraphQlUri();

    internal void Validate()
    {
        ValidateHttpUri(ApiBaseUri, nameof(ApiBaseUri), "API base");
        if (!string.IsNullOrEmpty(ApiBaseUri.Query) ||
            !string.IsNullOrEmpty(ApiBaseUri.Fragment))
        {
            throw new ArgumentException(
                "The GitHub API base URI cannot contain a query or fragment.",
                nameof(ApiBaseUri));
        }

        if (GraphQlUri is not null)
        {
            ValidateHttpUri(GraphQlUri, nameof(GraphQlUri), "GraphQL");
            string graphQlPath = GraphQlUri.AbsolutePath.TrimEnd('/');
            if (!SameOrigin(ApiBaseUri, GraphQlUri) ||
                !string.IsNullOrEmpty(GraphQlUri.Query) ||
                !string.IsNullOrEmpty(GraphQlUri.Fragment) ||
                !graphQlPath.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The GitHub GraphQL URI must use the API base origin, end in '/graphql', and cannot contain a query or fragment.",
                    nameof(GraphQlUri));
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(UserAgent);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTransientRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryBaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPaginationPages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPaginationItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ForkAvailabilityMaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ForkAvailabilityBaseDelay, TimeSpan.Zero);
    }

    private Uri DeriveGraphQlUri()
    {
        Uri apiBaseUri = NormalizedApiBaseUri;
        string apiPath = apiBaseUri.AbsolutePath.TrimEnd('/');
        string graphQlPath = apiPath.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase)
            ? apiPath[..^"v3".Length] + "graphql"
            : (apiPath.Length == 0 ? "" : apiPath) + "/graphql";
        return new UriBuilder(apiBaseUri)
        {
            Path = graphQlPath,
            Query = "",
            Fragment = "",
        }.Uri;
    }

    private static void ValidateHttpUri(Uri uri, string parameterName, string description)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                $"The GitHub {description} URI must be an absolute HTTP or HTTPS URI without user information.",
                parameterName);
        }
    }

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            left.Port == right.Port;
}
