using WinMatsch.GitHub.Auth;

namespace WinMatsch.Cli.Tests.Harness;

/// <summary>An in-memory <see cref="ITokenStore"/> so tests never touch the OS keyring.</summary>
public sealed class FakeTokenStore : ITokenStore
{
    public string ProviderName => "fake";

    public bool IsAvailable { get; init; } = true;

    public GitHubToken? StoredToken { get; set; }

    public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(StoredToken);

    public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default)
    {
        StoredToken = token;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        bool removed = StoredToken is not null;
        StoredToken = null;
        return Task.FromResult(removed);
    }
}
