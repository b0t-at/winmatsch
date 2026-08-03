namespace WinMatsch.Core;

/// <summary>
/// A WinGet package version.
/// <para>
/// The raw manifest string is preserved verbatim (identity semantics for equality), while
/// <see cref="CompareTo"/> implements WinGet's version ordering semantics as implemented by
/// winget-cli's <c>Version</c> class: the string is split on <c>.</c>, each part is compared
/// by its leading integer first and its remaining suffix second, trailing zero parts are
/// ignored (<c>1.0 == 1.0.0</c>) and a part without a suffix is greater than the same part
/// with one (<c>1.0 &gt; 1.0-rc</c>). The literal string <c>unknown</c> sorts below all
/// other versions.
/// </para>
/// <para>
/// Because distinct strings can be equivalent in WinGet ordering (<c>1.0</c> vs <c>1.0.0</c>),
/// <see cref="CompareTo"/> falls back to an ordinal string comparison to provide a total order
/// that is consistent with <see cref="Equals(PackageVersion)"/>. Use <see cref="IsEquivalentTo"/>
/// to test pure WinGet ordering equivalence.
/// </para>
/// </summary>
public sealed class PackageVersion : IEquatable<PackageVersion>, IComparable<PackageVersion>
{
    /// <summary>Maximum allowed length per the WinGet manifest schema.</summary>
    public const int MaxLength = 128;

    private const string UnknownVersionString = "unknown";

    private readonly VersionPart[] _parts;

    /// <summary>Creates a package version from its manifest string representation.</summary>
    /// <exception cref="ArgumentException">The value is empty, too long or contains characters that are invalid in a package version.</exception>
    public PackageVersion(string value)
    {
        Validate(value);
        Value = value;

        ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
        IsUnknown = trimmed.Equals(UnknownVersionString, StringComparison.OrdinalIgnoreCase);
        _parts = ParseParts(trimmed);
    }

    /// <summary>The raw version string exactly as it appears in the manifest.</summary>
    public string Value { get; }

    /// <summary>Whether this is the special <c>unknown</c> version, which sorts below all other versions.</summary>
    public bool IsUnknown { get; }

    /// <summary>Attempts to create a package version, returning <see langword="false"/> instead of throwing on invalid input.</summary>
    public static bool TryCreate(string? value, out PackageVersion? version)
    {
        if (value is not null && GetValidationError(value) is null)
        {
            version = new PackageVersion(value);
            return true;
        }

        version = null;
        return false;
    }

    /// <summary>Tests WinGet ordering equivalence, ignoring raw string differences (<c>1.0</c> is equivalent to <c>1.0.0</c>).</summary>
    public bool IsEquivalentTo(PackageVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ComparePartsTo(other) == 0;
    }

    /// <inheritdoc />
    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = ComparePartsTo(other);
        return result != 0 ? result : string.CompareOrdinal(Value, other.Value);
    }

    /// <inheritdoc />
    public bool Equals(PackageVersion? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PackageVersion);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(PackageVersion? left, PackageVersion? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(PackageVersion? left, PackageVersion? right) => !(left == right);

    public static bool operator <(PackageVersion left, PackageVersion right) => Compare(left, right) < 0;

    public static bool operator >(PackageVersion left, PackageVersion right) => Compare(left, right) > 0;

    public static bool operator <=(PackageVersion left, PackageVersion right) => Compare(left, right) <= 0;

    public static bool operator >=(PackageVersion left, PackageVersion right) => Compare(left, right) >= 0;

    private static int Compare(PackageVersion left, PackageVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    private int ComparePartsTo(PackageVersion other)
    {
        if (IsUnknown || other.IsUnknown)
        {
            return (IsUnknown ? 0 : 1) - (other.IsUnknown ? 0 : 1);
        }

        int length = Math.Max(_parts.Length, other._parts.Length);
        for (int i = 0; i < length; i++)
        {
            VersionPart left = i < _parts.Length ? _parts[i] : VersionPart.Zero;
            VersionPart right = i < other._parts.Length ? other._parts[i] : VersionPart.Zero;

            int result = left.CompareTo(right);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (GetValidationError(value) is { } error)
        {
            throw new ArgumentException(error, nameof(value));
        }
    }

    private static string? GetValidationError(string value)
    {
        if (value.Length == 0)
        {
            return "A package version must not be empty.";
        }

        if (value.Length > MaxLength)
        {
            return $"A package version must not be longer than {MaxLength} characters.";
        }

        foreach (char c in value)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))
            {
                return $"A package version must not contain the character '{(char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString())}'.";
            }
        }

        return null;
    }

    private static VersionPart[] ParseParts(ReadOnlySpan<char> value)
    {
        var parts = new List<VersionPart>();
        foreach (Range segment in value.Split('.'))
        {
            parts.Add(VersionPart.Parse(value[segment]));
        }

        // Trailing parts that are semantically zero do not participate in ordering: 1.0 == 1.0.0
        while (parts.Count > 0 && parts[^1] == VersionPart.Zero)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return [.. parts];
    }

    private readonly record struct VersionPart(ulong Number, string Suffix)
    {
        public static readonly VersionPart Zero = new(0, string.Empty);

        public static VersionPart Parse(ReadOnlySpan<char> segment)
        {
            segment = segment.Trim();

            int digits = 0;
            while (digits < segment.Length && char.IsAsciiDigit(segment[digits]))
            {
                digits++;
            }

            if (digits > 0 && ulong.TryParse(segment[..digits], out ulong number))
            {
                return new VersionPart(number, segment[digits..].ToString());
            }

            // No leading digits, or the numeric prefix overflows ulong: the whole segment is a suffix.
            return new VersionPart(0, segment.ToString());
        }

        public int CompareTo(VersionPart other)
        {
            int result = Number.CompareTo(other.Number);
            if (result != 0)
            {
                return result;
            }

            if (Suffix.Length == 0 || other.Suffix.Length == 0)
            {
                // A part without a suffix is greater than the same part with one: 1.0 > 1.0-rc
                return other.Suffix.Length - Suffix.Length;
            }

            return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
