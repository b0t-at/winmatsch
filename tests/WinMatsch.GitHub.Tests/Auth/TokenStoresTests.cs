using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.GitHub.Tests.Auth;

public class TokenStoresTests
{
    [Fact]
    public void CreateDefault_returns_the_platform_store()
    {
        ITokenStore store = TokenStores.CreateDefault();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialManagerTokenStore>(store);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacOSKeychainTokenStore>(store);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxSecretServiceTokenStore>(store);
        }
        else
        {
            Assert.False(store.IsAvailable);
        }
    }

    [Fact]
    public async Task GetStatusAsync_reports_unavailable_store_with_unknown_token_state()
    {
        var store = new StubStore { Available = false };

        TokenStoreStatus status = await TokenStores.GetStatusAsync(store);

        Assert.Equal("stub", status.ProviderName);
        Assert.False(status.IsAvailable);
        Assert.Null(status.HasToken);
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task GetStatusAsync_reports_stored_token()
    {
        var store = new StubStore { Token = new GitHubToken("ghp_stored") };

        TokenStoreStatus status = await TokenStores.GetStatusAsync(store);

        Assert.True(status.IsAvailable);
        Assert.True(status.HasToken);
    }

    [Fact]
    public async Task GetStatusAsync_reports_missing_token()
    {
        var store = new StubStore();

        TokenStoreStatus status = await TokenStores.GetStatusAsync(store);

        Assert.True(status.IsAvailable);
        Assert.False(status.HasToken);
    }

    private sealed class StubStore : ITokenStore
    {
        public GitHubToken? Token { get; set; }

        public bool Available { get; set; } = true;

        public int GetCalls { get; private set; }

        public string ProviderName => "stub";

        public bool IsAvailable => Available;

        public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Token);
        }

        public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
