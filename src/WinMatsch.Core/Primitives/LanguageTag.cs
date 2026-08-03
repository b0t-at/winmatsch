namespace WinMatsch.Core;

/// <summary>
/// A BCP 47 language tag as used by WinGet manifests, e.g. <c>en-US</c> or <c>zh-Hans</c>.
/// Validation is intentionally slightly more permissive than the schema pattern (alphanumeric
/// subtags are accepted, so real-world tags like <c>es-419</c> work). Equality is case-insensitive;
/// the original casing is preserved for output.
/// </summary>
public sealed class LanguageTag : IEquatable<LanguageTag>
{
    public const int MaxLength = 20;

    /// <summary>Creates a language tag from its manifest string representation.</summary>
    /// <exception cref="ArgumentException">The value is not a valid language tag.</exception>
    public LanguageTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (GetValidationError(value) is { } error)
        {
            throw new ArgumentException(error, nameof(value));
        }

        Value = value;
    }

    /// <summary>The raw language tag exactly as it appears in the manifest.</summary>
    public string Value { get; }

    /// <summary>Attempts to create a language tag, returning <see langword="false"/> instead of throwing on invalid input.</summary>
    public static bool TryCreate(string? value, out LanguageTag? tag)
    {
        if (value is not null && GetValidationError(value) is null)
        {
            tag = new LanguageTag(value);
            return true;
        }

        tag = null;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(LanguageTag? other) => other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as LanguageTag);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(LanguageTag? left, LanguageTag? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(LanguageTag? left, LanguageTag? right) => !(left == right);

    private static string? GetValidationError(string value)
    {
        if (value.Length == 0)
        {
            return "A language tag must not be empty.";
        }

        if (value.Length > MaxLength)
        {
            return $"A language tag must not be longer than {MaxLength} characters.";
        }

        string[] subtags = value.Split('-');

        string primary = subtags[0];
        bool validPrimary = primary.Length is >= 2 and <= 3 && AllAsciiLetters(primary);
        bool privateUse = subtags.Length > 1 && primary.Length == 1 && (primary[0] is 'x' or 'X' or 'i' or 'I');
        if (!validPrimary && !privateUse)
        {
            return $"'{value}' is not a valid language tag.";
        }

        for (int i = 1; i < subtags.Length; i++)
        {
            if (subtags[i].Length is 0 or > 8 || !AllAsciiLettersOrDigits(subtags[i]))
            {
                return $"'{value}' is not a valid language tag.";
            }
        }

        return null;

        static bool AllAsciiLetters(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsAsciiLetter(c))
                {
                    return false;
                }
            }

            return true;
        }

        static bool AllAsciiLettersOrDigits(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
