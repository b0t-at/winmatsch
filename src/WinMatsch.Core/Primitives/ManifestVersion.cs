namespace WinMatsch.Core;

/// <summary>
/// The version of the WinGet manifest schema a manifest conforms to, e.g. <c>1.10.0</c>.
/// Always three numeric parts, each between 0 and 9999.
/// </summary>
public sealed class ManifestVersion : IEquatable<ManifestVersion>, IComparable<ManifestVersion>
{
    /// <summary>The manifest schema version this tool emits by default.</summary>
    public static readonly ManifestVersion Default = new("1.10.0");

    /// <summary>Creates a manifest version from its string representation.</summary>
    /// <exception cref="ArgumentException">The value is not a valid manifest schema version.</exception>
    public ManifestVersion(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string[] parts = value.Split('.');
        if (parts.Length != 3)
        {
            throw new ArgumentException("A manifest version must consist of exactly three dot-separated numeric parts.", nameof(value));
        }

        ushort[] numbers = new ushort[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 4
                || (parts[i].Length > 1 && parts[i][0] == '0')
                || !ushort.TryParse(parts[i], out numbers[i]))
            {
                throw new ArgumentException($"'{parts[i]}' is not a valid manifest version part (0-9999, no leading zeros).", nameof(value));
            }
        }

        Value = value;
        Major = numbers[0];
        Minor = numbers[1];
        Patch = numbers[2];
    }

    /// <summary>The raw version string, e.g. <c>1.10.0</c>.</summary>
    public string Value { get; }

    public ushort Major { get; }

    public ushort Minor { get; }

    public ushort Patch { get; }

    /// <inheritdoc />
    public bool Equals(ManifestVersion? other) => other is not null && Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ManifestVersion);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <inheritdoc />
    public int CompareTo(ManifestVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(ManifestVersion? left, ManifestVersion? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(ManifestVersion? left, ManifestVersion? right) => !(left == right);

    public static bool operator <(ManifestVersion left, ManifestVersion right) => Compare(left, right) < 0;

    public static bool operator >(ManifestVersion left, ManifestVersion right) => Compare(left, right) > 0;

    public static bool operator <=(ManifestVersion left, ManifestVersion right) => Compare(left, right) <= 0;

    public static bool operator >=(ManifestVersion left, ManifestVersion right) => Compare(left, right) >= 0;

    private static int Compare(ManifestVersion left, ManifestVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }
}
