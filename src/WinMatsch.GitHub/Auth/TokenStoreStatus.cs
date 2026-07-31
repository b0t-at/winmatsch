namespace WinMatsch.GitHub.Auth;

/// <summary>The displayable status of a token store, for the <c>token status</c> command.</summary>
/// <param name="ProviderName">The keyring provider name.</param>
/// <param name="IsAvailable">Whether the keyring can be used on this machine.</param>
/// <param name="HasToken">Whether a token is stored; null when the keyring is unavailable.</param>
public sealed record TokenStoreStatus(string ProviderName, bool IsAvailable, bool? HasToken);
