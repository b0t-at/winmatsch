using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.GitHub.Tests.Auth;

public class TokenResolverTests
{
    [Fact]
    public async Task Explicit_option_wins_over_environment_and_keyring()
    {
        var store = new FakeTokenStore { Token = new GitHubToken("ghp_keyring") };
        var resolver = new TokenResolver(store, _ => "ghp_environment");

        ResolvedToken? resolved = await resolver.ResolveAsync(new GitHubToken("ghp_explicit"));

        Assert.NotNull(resolved);
        Assert.Equal(TokenSource.ExplicitOption, resolved.Source);
        Assert.Equal("ghp_explicit", resolved.Token.RevealValue());
    }

    [Fact]
    public async Task Environment_variable_wins_over_keyring()
    {
        var store = new FakeTokenStore { Token = new GitHubToken("ghp_keyring") };
        var resolver = new TokenResolver(store, name =>
            name == TokenResolver.TokenEnvironmentVariable ? "ghp_environment" : null);

        ResolvedToken? resolved = await resolver.ResolveAsync();

        Assert.NotNull(resolved);
        Assert.Equal(TokenSource.EnvironmentVariable, resolved.Source);
        Assert.Equal("ghp_environment", resolved.Token.RevealValue());
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task Environment_value_is_trimmed()
    {
        var resolver = new TokenResolver(new FakeTokenStore(), _ => "  ghp_environment  ");

        ResolvedToken? resolved = await resolver.ResolveAsync();

        Assert.NotNull(resolved);
        Assert.Equal("ghp_environment", resolved.Token.RevealValue());
    }

    [Fact]
    public async Task Whitespace_only_environment_value_is_ignored()
    {
        var store = new FakeTokenStore { Token = new GitHubToken("ghp_keyring") };
        var resolver = new TokenResolver(store, _ => "   ");

        ResolvedToken? resolved = await resolver.ResolveAsync();

        Assert.NotNull(resolved);
        Assert.Equal(TokenSource.Keyring, resolved.Source);
    }

    [Fact]
    public async Task Keyring_is_used_when_no_other_source_is_set()
    {
        var store = new FakeTokenStore { Token = new GitHubToken("ghp_keyring") };
        var resolver = new TokenResolver(store, _ => null);

        ResolvedToken? resolved = await resolver.ResolveAsync();

        Assert.NotNull(resolved);
        Assert.Equal(TokenSource.Keyring, resolved.Source);
        Assert.Equal("ghp_keyring", resolved.Token.RevealValue());
    }

    [Fact]
    public async Task Unavailable_keyring_is_never_queried()
    {
        var store = new FakeTokenStore { Available = false, Token = new GitHubToken("ghp_keyring") };
        var resolver = new TokenResolver(store, _ => null);

        ResolvedToken? resolved = await resolver.ResolveAsync();

        Assert.Null(resolved);
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task Null_is_returned_when_no_source_provides_a_token()
    {
        var resolver = new TokenResolver(new FakeTokenStore(), _ => null);

        Assert.Null(await resolver.ResolveAsync());
    }

    private sealed class FakeTokenStore : ITokenStore
    {
        public GitHubToken? Token { get; set; }

        public bool Available { get; set; } = true;

        public int GetCalls { get; private set; }

        public string ProviderName => "fake";

        public bool IsAvailable => Available;

        public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Token);
        }

        public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default)
        {
            bool removed = Token is not null;
            Token = null;
            return Task.FromResult(removed);
        }
    }
}
