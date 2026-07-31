using System.Globalization;

namespace WinMatsch.Workflows.Configuration;

/// <summary>
/// Deterministic scalar parsing shared by the YAML and environment configuration readers.
/// Every parse is explicit and culture-invariant; bad input throws <see cref="FormatException"/>
/// with the offending value so typos surface immediately.
/// </summary>
internal static class ConfigurationValues
{
    public static OutputFormat ParseOutputFormat(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            _ => throw new FormatException($"'{value}' is not a valid output format. Use 'text' or 'json'."),
        };
    }

    public static InteractionMode ParseInteractionMode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => InteractionMode.Auto,
            "always" => InteractionMode.Always,
            "never" => InteractionMode.Never,
            _ => throw new FormatException(
                $"'{value}' is not a valid interaction mode. Use 'auto', 'always', or 'never'."),
        };
    }

    public static bool ParseBoolean(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"'{value}' is not a valid boolean. Use 'true' or 'false'."),
        };
    }

    public static int ParseInt32(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new FormatException($"'{value}' is not a valid integer.");
        }

        return parsed;
    }

    public static TimeSpan ParseFreshnessDelay(string value)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed))
        {
            throw new FormatException($"'{value}' is not a valid time span. Use a format like '2.00:00:00'.");
        }

        if (parsed < TimeSpan.Zero)
        {
            throw new FormatException("The freshness delay must not be negative.");
        }

        return parsed;
    }

    public static IReadOnlyList<string> ParseRuleList(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] entries = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return entries;
    }
}
