using System.Collections.Immutable;
using System.Net;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Operations;
using WinMatsch.Workflows.Tests.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.Discovery;

public sealed class RepositoryReleaseMetadataSourceTests
{
    private static readonly RepositoryCoordinates _repository = new("example", "app");

    [Fact]
    public async Task Available_metadata_preserves_field_and_rate_limit_provenance()
    {
        var client = new FakeGitHubClient
        {
            LastRateLimit = new(
                "core",
                5_000,
                4_990,
                10,
                new DateTimeOffset(2026, 8, 1, 18, 0, 0, TimeSpan.Zero)),
            RepositoryMetadata = new(
                _repository,
                new Uri("https://github.com/example/app"),
                new Uri("https://github.com/example"),
                IsPrivate: false,
                "Apache-2.0",
                new Uri("https://github.com/example/app/blob/main/LICENSE"),
                ["weather", "windows"]),
        };

        RepositoryReleaseMetadata metadata =
            await new GitHubRepositoryReleaseMetadataSource(client).GetAsync(
                _repository,
                CancellationToken.None);

        Assert.Equal(RepositoryMetadataAvailability.Available, metadata.Availability);
        Assert.Equal("Apache-2.0", metadata.License);
        Assert.Equal(["weather", "windows"], metadata.Topics.ToArray());
        Assert.Equal("https://github.com/example", metadata.PublisherUrl?.AbsoluteUri);
        Assert.Contains("page=1/1", metadata.Provenance, StringComparison.Ordinal);
        Assert.Contains("rate=4990/5000", metadata.Provenance, StringComparison.Ordinal);
        Assert.Contains("outcome=available", metadata.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_and_private_failures_are_distinguished()
    {
        var rateLimited = new FakeGitHubClient
        {
            LastRateLimit = new(
                "core",
                5_000,
                0,
                5_000,
                new DateTimeOffset(2026, 8, 1, 18, 0, 0, TimeSpan.Zero)),
            RepositoryMetadataFailure = new GitHubApiException(
                "rate limited",
                HttpStatusCode.Forbidden,
                requestId: "request",
                errorKind: GitHubApiErrorKind.RateLimited,
                retryAfter: TimeSpan.FromSeconds(30)),
        };
        var privateClient = new FakeGitHubClient
        {
            RepositoryMetadataFailure = new GitHubApiException(
                "forbidden",
                HttpStatusCode.Forbidden,
                requestId: "request"),
        };

        RepositoryReleaseMetadata limited =
            await new GitHubRepositoryReleaseMetadataSource(rateLimited).GetAsync(
                _repository,
                CancellationToken.None);
        RepositoryReleaseMetadata unavailablePrivate =
            await new GitHubRepositoryReleaseMetadataSource(privateClient).GetAsync(
                _repository,
                CancellationToken.None);

        Assert.Equal(RepositoryMetadataAvailability.RateLimited, limited.Availability);
        Assert.Contains("rate-limited", limited.Provenance, StringComparison.Ordinal);
        Assert.Contains("00:00:30", limited.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(RepositoryMetadataAvailability.Private, unavailablePrivate.Availability);
        Assert.Contains("private-or-forbidden", unavailablePrivate.Provenance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, RepositoryMetadataAvailability.Absent)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RepositoryMetadataAvailability.Unavailable)]
    public async Task Absence_and_service_errors_keep_explicit_provenance(
        HttpStatusCode status,
        RepositoryMetadataAvailability expected)
    {
        var client = new FakeGitHubClient
        {
            RepositoryMetadataFailure = new GitHubApiException(
                "metadata failure",
                status,
                requestId: "request"),
        };

        RepositoryReleaseMetadata metadata =
            await new GitHubRepositoryReleaseMetadataSource(client).GetAsync(
                _repository,
                CancellationToken.None);

        Assert.Equal(expected, metadata.Availability);
        Assert.Contains("outcome=", metadata.Provenance, StringComparison.Ordinal);
        Assert.NotNull(metadata.Diagnostic);
    }

    [Fact]
    public async Task Workflow_metadata_bounds_topics_and_release_notes_to_schema_limits()
    {
        var releaseUri = new Uri("https://github.com/example/app/releases/tag/v1");
        var client = new FakeGitHubClient
        {
            Releases =
            [
                new GitHubRelease(
                    42,
                    "v1",
                    "Version 1",
                    new string('x', 10_100),
                    releaseUri,
                    IsDraft: false,
                    IsPrerelease: false,
                    DateTimeOffset.UtcNow,
                    []),
            ],
        };
        var repositoryMetadata = new RepositoryReleaseMetadata
        {
            Availability = RepositoryMetadataAvailability.Available,
            Topics =
            [
                .. Enumerable.Range(0, 20).Select(static index => $"topic-{index:D2}"),
            ],
            Provenance = "fixture",
        };
        var source = new GitHubWorkflowReleaseSource(
            client,
            _repository,
            new StaticMetadataSource(repositoryMetadata));
        var asset = new DiscoveredAsset
        {
            ReleaseId = 42,
            ReleaseTag = "v1",
            ReleaseName = "Version 1",
            ReleaseUri = releaseUri,
            IsPrerelease = false,
            AssetId = 1,
            AssetName = "app.exe",
            DownloadUri = new Uri("https://example.test/app.exe"),
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 1,
            AssetCreatedAt = DateTimeOffset.UtcNow,
        };

        WorkflowReleaseMetadata metadata = await source.DiscoverMetadataAsync(
            new PackageIdentifier("Example.App"),
            new ReleaseRequest(null, [], []),
            [asset],
            CancellationToken.None);

        Assert.Equal(10_000, metadata.Metadata.ReleaseNotes?.Length);
        Assert.Equal(16, metadata.Metadata.Tags?.Count);
        Assert.Contains(
            "truncated=10000",
            metadata.Metadata.Provenance[nameof(PackageLocaleMetadata.ReleaseNotes)],
            StringComparison.Ordinal);
    }

    private sealed class StaticMetadataSource(
        RepositoryReleaseMetadata metadata) : IRepositoryReleaseMetadataSource
    {
        public Task<RepositoryReleaseMetadata> GetAsync(
            RepositoryCoordinates repository,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(metadata);
        }
    }
}
