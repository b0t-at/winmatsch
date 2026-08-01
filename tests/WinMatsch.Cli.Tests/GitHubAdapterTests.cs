using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Tests.Maintenance;
using WinMatsch.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class GitHubAdapterTests
{
    [Fact]
    public async Task Submission_text_is_redacted_by_the_injected_client_contract()
    {
        const string token = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
        const string jwt =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
            + "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ."
            + "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var inner = new FakeMaintenanceGitHubClient();
        using var client = new RedactingGitHubRepositoryClient(inner);
        var repository = new RepositoryCoordinates("owner", "repo");
        var mutation = new MutationRequest("adapter-test");

        _ = await client.CreateCommitAsync(
            repository,
            new ServerCommitRequest(
                "branch",
                "head",
                $"Update {token}",
                $"Authorization: Bearer {jwt}",
                [],
                []),
            mutation);
        _ = await client.CreatePullRequestAsync(
            repository,
            new CreatePullRequestRequest(
                $"Title {token}",
                "Download https://example.test/app?sig=presigned-secret",
                "owner",
                "branch",
                "main"),
            mutation);
        _ = await client.CommentOnPullRequestAsync(
            repository,
            42,
            $"token={token}",
            mutation);

        ServerCommitRequest commit = Assert.IsType<ServerCommitRequest>(inner.LastCommitRequest);
        CreatePullRequestRequest pullRequest = Assert.IsType<CreatePullRequestRequest>(
            inner.LastCreatePullRequestRequest);
        Assert.DoesNotContain(token, commit.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, commit.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(token, pullRequest.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("presigned-secret", pullRequest.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            Assert.Single(
                inner.Mutations,
                value => value.StartsWith("comment:", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }
}
