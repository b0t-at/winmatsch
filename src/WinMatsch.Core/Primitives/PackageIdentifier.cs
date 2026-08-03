namespace WinMatsch.Core;

/// <summary>
/// A WinGet package identifier such as <c>Microsoft.PowerToys</c>: 2 to 8 dot-separated
/// segments of 1 to 32 characters each, at most 128 characters in total.
/// Comparison and equality are case-insensitive (WinGet identifier semantics), while the
/// original casing is preserved for output.
/// </summary>
public sealed class PackageIdentifier : IEquatable<PackageIdentifier>, IComparable<PackageIdentifier>
{
    public const int MaxLength = 128;
    public const int MaxSegmentLength = 32;
    public const int MinSegments = 2;
    public const int MaxSegments = 8;

    /// <summary>Creates a package identifier from its manifest string representation.</summary>
    /// <exception cref="ArgumentException">The value is not a valid WinGet package identifier.</exception>
    public PackageIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (GetValidationError(value) is { } error)
        {
            throw new ArgumentException(error, nameof(value));
        }

        Value = value;
        Segments = value.Split('.');
    }

    /// <summary>The raw identifier exactly as it appears in the manifest.</summary>
    public string Value { get; }

    /// <summary>The dot-separated segments of the identifier; the first segment is the publisher part.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Attempts to create a package identifier, returning <see langword="false"/> instead of throwing on invalid input.</summary>
    public static bool TryCreate(string? value, out PackageIdentifier? identifier)
    {
        if (value is not null && GetValidationError(value) is null)
        {
            identifier = new PackageIdentifier(value);
            return true;
        }

        identifier = null;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(PackageIdentifier? other) => other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PackageIdentifier);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public int CompareTo(PackageIdentifier? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(PackageIdentifier? left, PackageIdentifier? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(PackageIdentifier? left, PackageIdentifier? right) => !(left == right);

    public static bool operator <(PackageIdentifier left, PackageIdentifier right) => Compare(left, right) < 0;

    public static bool operator >(PackageIdentifier left, PackageIdentifier right) => Compare(left, right) > 0;

    public static bool operator <=(PackageIdentifier left, PackageIdentifier right) => Compare(left, right) <= 0;

    public static bool operator >=(PackageIdentifier left, PackageIdentifier right) => Compare(left, right) >= 0;

    private static int Compare(PackageIdentifier left, PackageIdentifier right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    private static string? GetValidationError(string value)
    {
        if (value.Length == 0)
        {
            return "A package identifier must not be empty.";
        }

        if (value.Length > MaxLength)
        {
            return $"A package identifier must not be longer than {MaxLength} characters.";
        }

        string[] segments = value.Split('.');
        if (segments.Length is < MinSegments or > MaxSegments)
        {
            return $"A package identifier must consist of {MinSegments} to {MaxSegments} dot-separated segments.";
        }

        foreach (string segment in segments)
        {
            if (segment.Length is 0 or > MaxSegmentLength)
            {
                return $"Each segment of a package identifier must be 1 to {MaxSegmentLength} characters long.";
            }

            foreach (char c in segment)
            {
                if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    return $"A package identifier must not contain whitespace, control characters or any of \\ / : * ? \" < > |.";
                }
            }
        }

        return null;
    }
}
