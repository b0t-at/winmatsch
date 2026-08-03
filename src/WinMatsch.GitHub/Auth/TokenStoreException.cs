namespace WinMatsch.GitHub.Auth;

/// <summary>
/// A keyring operation failure. Messages describe the operation and native error code only;
/// they never contain the secret or any keyring payload.
/// </summary>
public sealed class TokenStoreException : Exception
{
    public TokenStoreException()
    {
    }

    public TokenStoreException(string message)
        : base(message)
    {
    }

    public TokenStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
