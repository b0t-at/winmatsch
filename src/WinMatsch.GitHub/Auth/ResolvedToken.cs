namespace WinMatsch.GitHub.Auth;

/// <summary>A token together with the source it was resolved from.</summary>
public sealed record ResolvedToken(GitHubToken Token, TokenSource Source);
