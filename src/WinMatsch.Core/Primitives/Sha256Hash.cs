using System.Security.Cryptography;

namespace WinMatsch.Core;

/// <summary>
/// A SHA-256 hash as used in WinGet manifests (64 hexadecimal characters).
/// The original casing from the manifest is preserved; hashes computed by this tool are uppercase.
/// Equality is case-insensitive.
/// </summary>
public sealed class Sha256Hash : IEquatable<Sha256Hash>
{
    public const int Length = 64;

    /// <summary>Creates a hash from its 64-character hexadecimal string representation.</summary>
    /// <exception cref="ArgumentException">The value is not a 64-character hexadecimal string.</exception>
    public Sha256Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != Length)
        {
            throw new ArgumentException($"A SHA-256 hash must be exactly {Length} hexadecimal characters long.", nameof(value));
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                throw new ArgumentException("A SHA-256 hash must consist of hexadecimal characters only.", nameof(value));
            }
        }

        Value = value;
    }

    /// <summary>The raw hash exactly as it appears in the manifest.</summary>
    public string Value { get; }

    /// <summary>The hash normalized to uppercase, the convention used by WinGet tooling.</summary>
    public string Normalized => Value.ToUpperInvariant();

    /// <summary>Creates a hash from raw hash bytes (uppercase representation).</summary>
    public static Sha256Hash FromHashBytes(ReadOnlySpan<byte> hashBytes)
    {
        if (hashBytes.Length != Length / 2)
        {
            throw new ArgumentException($"A SHA-256 hash consists of {Length / 2} bytes.", nameof(hashBytes));
        }

        return new Sha256Hash(Convert.ToHexString(hashBytes));
    }

    /// <summary>Computes the SHA-256 hash of a stream, reading it from its current position to the end.</summary>
    public static async Task<Sha256Hash> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return FromHashBytes(hash);
    }

    /// <inheritdoc />
    public bool Equals(Sha256Hash? other) => other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Sha256Hash);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(Sha256Hash? left, Sha256Hash? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(Sha256Hash? left, Sha256Hash? right) => !(left == right);
}
