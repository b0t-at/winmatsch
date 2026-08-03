namespace WinMatsch.Core;

/// <summary>
/// A Windows OS version as used in <c>MinimumOSVersion</c>, e.g. <c>10.0.17763.0</c>:
/// one to four numeric parts, each between 0 and 65535.
/// Ordering treats missing parts as zero (<c>10.0</c> is equivalent to <c>10.0.0.0</c>);
/// the raw string is preserved for output.
/// </summary>
public sealed class MinimumOSVersion : IEquatable<MinimumOSVersion>, IComparable<MinimumOSVersion>
{
    private readonly ushort[] _parts;

    /// <summary>Creates an OS version from its manifest string representation.</summary>
    /// <exception cref="ArgumentException">The value is not a valid OS version.</exception>
    public MinimumOSVersion(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string[] segments = value.Split('.');
        if (segments.Length is 0 or > 4)
        {
            throw new ArgumentException("An OS version must consist of one to four dot-separated numeric parts.", nameof(value));
        }

        _parts = new ushort[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0
                || (segments[i].Length > 1 && segments[i][0] == '0')
                || !ushort.TryParse(segments[i], out _parts[i]))
            {
                throw new ArgumentException($"'{segments[i]}' is not a valid OS version part (0-65535, no leading zeros).", nameof(value));
            }
        }

        Value = value;
    }

    /// <summary>The raw OS version exactly as it appears in the manifest.</summary>
    public string Value { get; }

    /// <summary>Attempts to create an OS version, returning <see langword="false"/> instead of throwing on invalid input.</summary>
    public static bool TryCreate(string? value, out MinimumOSVersion? version)
    {
        try
        {
            version = value is null ? null : new MinimumOSVersion(value);
            return version is not null;
        }
        catch (ArgumentException)
        {
            version = null;
            return false;
        }
    }

    /// <inheritdoc />
    public int CompareTo(MinimumOSVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int i = 0; i < 4; i++)
        {
            ushort left = i < _parts.Length ? _parts[i] : (ushort)0;
            ushort right = i < other._parts.Length ? other._parts[i] : (ushort)0;

            int result = left.CompareTo(right);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public bool Equals(MinimumOSVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MinimumOSVersion);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        for (int i = 0; i < 4; i++)
        {
            hash.Add(i < _parts.Length ? _parts[i] : (ushort)0);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(MinimumOSVersion? left, MinimumOSVersion? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(MinimumOSVersion? left, MinimumOSVersion? right) => !(left == right);

    public static bool operator <(MinimumOSVersion left, MinimumOSVersion right) => Compare(left, right) < 0;

    public static bool operator >(MinimumOSVersion left, MinimumOSVersion right) => Compare(left, right) > 0;

    public static bool operator <=(MinimumOSVersion left, MinimumOSVersion right) => Compare(left, right) <= 0;

    public static bool operator >=(MinimumOSVersion left, MinimumOSVersion right) => Compare(left, right) >= 0;

    private static int Compare(MinimumOSVersion left, MinimumOSVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }
}
