using System.Collections.Immutable;
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
