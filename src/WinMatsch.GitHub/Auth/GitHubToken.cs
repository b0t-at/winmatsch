using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WinMatsch.GitHub.Auth;

/// <summary>
/// A GitHub token held in a wrapper that redacts itself everywhere except the explicit
/// <see cref="RevealValue"/> call. <see cref="ToString"/>, debugger display, and record
/// formatting all print <see cref="RedactedPlaceholder"/>, and no public property exposes
/// the secret, so the token can never leak into logs, exception messages, JSON output,
/// configuration files, recordings, or process arguments by accident.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class GitHubToken : IEquatable<GitHubToken>
{
    /// <summary>The text printed in place of the secret wherever the token is formatted.</summary>
    public const string RedactedPlaceholder = "[REDACTED]";

    private readonly string _value;

    public GitHubToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw new ArgumentException(
                    "A token must not contain whitespace or control characters.",
                    nameof(value));
            }
        }

        _value = value;
    }

    /// <summary>The token length. Exposed for diagnostics; reveals no secret content.</summary>
    public int Length => _value.Length;

    /// <summary>
    /// Returns the raw secret. Call sites must pass the value only to authentication headers
    /// or OS keyrings — never to logs, exceptions, serialized output, or process arguments.
    /// </summary>
    public string RevealValue() => _value;

    /// <summary>Always returns <see cref="RedactedPlaceholder"/>; the secret is never formatted.</summary>
    public override string ToString() => RedactedPlaceholder;

    /// <summary>Compares tokens in fixed time to avoid timing side channels.</summary>
    public bool Equals(GitHubToken? other)
    {
        if (other is null)
        {
            return false;
        }

        byte[] left = Encoding.UTF8.GetBytes(_value);
        byte[] right = Encoding.UTF8.GetBytes(other._value);
        try
        {
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    public override bool Equals(object? obj) => Equals(obj as GitHubToken);

    public override int GetHashCode() => string.GetHashCode(_value, StringComparison.Ordinal);
}
