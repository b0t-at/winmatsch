namespace WinMatsch.GitHub.Auth;

/// <summary>Where a resolved token came from, in precedence order.</summary>
public enum TokenSource
{
    /// <summary>An explicit command-line option (highest precedence).</summary>
    ExplicitOption,

    /// <summary>The <c>GITHUB_TOKEN</c> environment variable.</summary>
    EnvironmentVariable,

    /// <summary>The operating-system keyring (lowest precedence).</summary>
    Keyring,
}
