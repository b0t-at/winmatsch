namespace BinMatch.Workflows.Configuration;

/// <summary>
/// Reads the <c>BINMATCH_*</c> environment variables into a <see cref="ConfigurationLayer"/>.
/// Unset, empty, and whitespace-only variables are treated as "not set".
/// </summary>
public static class EnvironmentConfiguration
{
    public const string RepositoryVariable = "BINMATCH_REPOSITORY";
    public const string ConcurrentDownloadsVariable = "BINMATCH_CONCURRENT_DOWNLOADS";
    public const string EnabledRulesVariable = "BINMATCH_RULES_ENABLED";
    public const string DisabledRulesVariable = "BINMATCH_RULES_DISABLED";
    public const string CacheEnabledVariable = "BINMATCH_CACHE_ENABLED";
    public const string CacheDirectoryVariable = "BINMATCH_CACHE_DIRECTORY";
    public const string OverrideStoreDirectoryVariable = "BINMATCH_OVERRIDE_STORE_DIRECTORY";
    public const string FreshnessDelayVariable = "BINMATCH_FRESHNESS_DELAY";
    public const string OutputFormatVariable = "BINMATCH_OUTPUT_FORMAT";
    public const string OutputDirectoryVariable = "BINMATCH_OUTPUT_DIRECTORY";
    public const string InteractionVariable = "BINMATCH_INTERACTION";

    /// <param name="environment">
    /// Environment lookup, injectable for tests. Defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </param>
    public static ConfigurationLayer Read(Func<string, string?>? environment = null)
    {
        Func<string, string?> lookup = environment ?? Environment.GetEnvironmentVariable;
        return new ConfigurationLayer
        {
            Repository = GetValue(lookup, RepositoryVariable),
            ConcurrentDownloads = ParseValue(lookup, ConcurrentDownloadsVariable, ConfigurationValues.ParseInt32),
            EnabledRules = ParseValue(lookup, EnabledRulesVariable, ConfigurationValues.ParseRuleList),
            DisabledRules = ParseValue(lookup, DisabledRulesVariable, ConfigurationValues.ParseRuleList),
            CacheEnabled = ParseValue(lookup, CacheEnabledVariable, ConfigurationValues.ParseBoolean),
            CacheDirectory = GetValue(lookup, CacheDirectoryVariable),
            OverrideStoreDirectory = GetValue(lookup, OverrideStoreDirectoryVariable),
            FreshnessDelay = ParseValue(lookup, FreshnessDelayVariable, ConfigurationValues.ParseFreshnessDelay),
            OutputFormat = ParseValue(lookup, OutputFormatVariable, ConfigurationValues.ParseOutputFormat),
            OutputDirectory = GetValue(lookup, OutputDirectoryVariable),
            Interaction = ParseValue(lookup, InteractionVariable, ConfigurationValues.ParseInteractionMode),
        };
    }

    private static string? GetValue(Func<string, string?> lookup, string variable)
    {
        string? value = lookup(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static T? ParseValue<T>(Func<string, string?> lookup, string variable, Func<string, T> parser)
        where T : struct
    {
        string? value = GetValue(lookup, variable);
        if (value is null)
        {
            return null;
        }

        try
        {
            return parser(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"{variable}: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<string>? ParseValue(
        Func<string, string?> lookup,
        string variable,
        Func<string, IReadOnlyList<string>> parser)
    {
        string? value = GetValue(lookup, variable);
        return value is null ? null : parser(value);
    }
}
