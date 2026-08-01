using System.Collections.Immutable;
using System.Net;
using WinMatsch.GitHub;

namespace WinMatsch.Workflows.Discovery;

public enum RepositoryMetadataAvailability
{
    Available,
    Absent,
    Private,
    RateLimited,
    Unavailable,
}

public sealed record RepositoryReleaseMetadata
{
    public RepositoryMetadataAvailability Availability { get; init; }

    public string? License { get; init; }

    public Uri? LicenseUrl { get; init; }

    public ImmutableArray<string> Topics { get; init; } = [];

    public Uri? PublisherUrl { get; init; }

    public Uri? RepositoryUrl { get; init; }

    public string? Diagnostic { get; init; }

    public required string Provenance { get; init; }
}

public interface IRepositoryReleaseMetadataSource
{
    public Task<RepositoryReleaseMetadata> GetAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken);
}

public sealed class GitHubRepositoryReleaseMetadataSource(
    IGitHubRepositoryClient client) : IRepositoryReleaseMetadataSource
{
    private readonly IGitHubRepositoryClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public async Task<RepositoryReleaseMetadata> GetAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            RepositoryMetadataInfo metadata = await _client.GetRepositoryMetadataAsync(
                repository,
                cancellationToken).ConfigureAwait(false);
            string provenance = Provenance(
                repository,
                metadata.IsPrivate ? "private" : "available");
            return new()
            {
                Availability = metadata.IsPrivate
                    ? RepositoryMetadataAvailability.Private
                    : RepositoryMetadataAvailability.Available,
                License = metadata.LicenseSpdxId,
                LicenseUrl = metadata.LicenseUri,
                Topics = [.. metadata.Topics],
                PublisherUrl = metadata.OwnerUri,
                RepositoryUrl = metadata.WebUri,
                Provenance = provenance,
                Diagnostic = metadata.IsPrivate
                    ? "Repository metadata was read from an authenticated private repository."
                    : null,
            };
        }
        catch (GitHubApiException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return Unavailable(
                repository,
                RepositoryMetadataAvailability.Absent,
                "not-found",
                "GitHub repository metadata was absent or not visible to the authenticated identity.");
        }
        catch (GitHubApiException exception)
            when (exception.ErrorKind == GitHubApiErrorKind.RateLimited)
        {
            return new()
            {
                Availability = RepositoryMetadataAvailability.RateLimited,
                Provenance = Provenance(
                    repository,
                    "rate-limited",
                    exception.RateLimit,
                    useClientFallback: false,
                    exception.RetryAfter),
                Diagnostic = RateLimitDiagnostic(
                    exception.RateLimit,
                    exception.RetryAfter),
            };
        }
        catch (GitHubApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unavailable(
                repository,
                RepositoryMetadataAvailability.Private,
                "private-or-forbidden",
                "GitHub repository metadata requires additional repository access.");
        }
        catch (Exception exception)
            when (exception is GitHubApiException
                or HttpRequestException
                or NotSupportedException)
        {
            return Unavailable(
                repository,
                RepositoryMetadataAvailability.Unavailable,
                $"error:{exception.GetType().Name}",
                "GitHub repository metadata could not be retrieved; explicit package metadata remains authoritative.");
        }
    }

    private RepositoryReleaseMetadata Unavailable(
        RepositoryCoordinates repository,
        RepositoryMetadataAvailability availability,
        string outcome,
        string diagnostic)
        => new()
        {
            Availability = availability,
            Provenance = Provenance(repository, outcome),
            Diagnostic = diagnostic,
        };

    private string Provenance(
        RepositoryCoordinates repository,
        string outcome,
        RateLimitInfo? responseRateLimit = null,
        bool useClientFallback = true,
        TimeSpan? retryAfter = null)
    {
        RateLimitInfo? rate = responseRateLimit
            ?? (useClientFallback ? _client.LastRateLimit : null);
        string rateProvenance = rate is null
            ? "rate=unknown"
            : $"rate={rate.Remaining}/{rate.Limit};reset={rate.ResetAt:O}";
        string cooldown = retryAfter is null
            ? ""
            : $";retry-after={retryAfter.Value:c}";
        return $"github-rest:repos/{repository.Owner}/{repository.Name}:page=1/1;{rateProvenance}{cooldown};outcome={outcome}";
    }

    private static string RateLimitDiagnostic(
        RateLimitInfo? rate,
        TimeSpan? retryAfter)
        => retryAfter is not null
                ? $"GitHub repository metadata was rate limited; retry after {retryAfter.Value:c}."
            : rate is not null
                ? $"GitHub repository metadata was rate limited until {rate.ResetAt:O}."
                : "GitHub repository metadata was rate limited.";
}
