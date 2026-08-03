using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.GitHub.Tests.Auth;

public class WindowsCredentialManagerTokenStoreTests
{
    [Fact]
    public async Task Round_trip_add_status_remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string targetName = $"winmatsch-test:{Guid.NewGuid():N}";
        var store = new WindowsCredentialManagerTokenStore(targetName);
        try
        {
            Assert.True(store.IsAvailable);
            Assert.Null(await store.GetTokenAsync());

            var token = new GitHubToken("ghp_credman_roundtrip_1234567890");
            await store.SetTokenAsync(token);

            GitHubToken? stored = await store.GetTokenAsync();
            Assert.NotNull(stored);
            Assert.Equal(token, stored);

            TokenStoreStatus status = await TokenStores.GetStatusAsync(store);
            Assert.True(status.HasToken);

            Assert.True(await store.RemoveTokenAsync());
            Assert.Null(await store.GetTokenAsync());
        }
        finally
        {
            _ = await store.RemoveTokenAsync();
        }
    }

    [Fact]
    public async Task Overwriting_replaces_the_stored_token()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string targetName = $"winmatsch-test:{Guid.NewGuid():N}";
        var store = new WindowsCredentialManagerTokenStore(targetName);
        try
        {
            await store.SetTokenAsync(new GitHubToken("ghp_first_value_1234567890"));
            await store.SetTokenAsync(new GitHubToken("ghp_second_value_1234567890"));

            GitHubToken? stored = await store.GetTokenAsync();
            Assert.NotNull(stored);
            Assert.Equal("ghp_second_value_1234567890", stored.RevealValue());
        }
        finally
        {
            _ = await store.RemoveTokenAsync();
        }
    }

    [Fact]
    public async Task Removing_a_missing_credential_returns_false()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialManagerTokenStore($"winmatsch-test:{Guid.NewGuid():N}");

        Assert.False(await store.RemoveTokenAsync());
    }
}
