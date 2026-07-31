using WinMatsch.GitHub;

namespace WinMatsch.Workflows.Configuration;

/// <summary>
/// Merges configuration layers into the effective configuration using the fixed precedence
/// command &gt; environment &gt; user configuration &gt; built-in defaults, then validates the result.
/// </summary>
public static class ConfigurationResolver
{
    public static WinMatschConfiguration Resolve(
        ConfigurationLayer? command = null,
        ConfigurationLayer? environment = null,
        ConfigurationLayer? userConfiguration = null)
    {
        command ??= ConfigurationLayer.Empty;
        environment ??= ConfigurationLayer.Empty;
        userConfiguration ??= ConfigurationLayer.Empty;

        string repository = command.Repository
            ?? environment.Repository
            ?? userConfiguration.Repository
            ?? ConfigurationDefaults.Repository;

        int concurrentDownloads = command.ConcurrentDownloads
            ?? environment.ConcurrentDownloads
            ?? userConfiguration.ConcurrentDownloads
            ?? ConfigurationDefaults.ConcurrentDownloads;
        if (concurrentDownloads < 1)
        {
            throw new FormatException("concurrentDownloads must be at least 1.");
        }

        TimeSpan freshnessDelay = command.FreshnessDelay
            ?? environment.FreshnessDelay
            ?? userConfiguration.FreshnessDelay
            ?? ConfigurationDefaults.FreshnessDelay;
        if (freshnessDelay < TimeSpan.Zero)
        {
            throw new FormatException("The freshness delay must not be negative.");
        }

        return new WinMatschConfiguration
        {
            Repository = RepositoryCoordinates.Parse(repository),
            ConcurrentDownloads = concurrentDownloads,
            EnabledRules = ValidateRules(
                command.EnabledRules ?? environment.EnabledRules ?? userConfiguration.EnabledRules ?? [],
                "enabled"),
            DisabledRules = ValidateRules(
                command.DisabledRules ?? environment.DisabledRules ?? userConfiguration.DisabledRules ?? [],
                "disabled"),
            CacheEnabled = command.CacheEnabled
                ?? environment.CacheEnabled
                ?? userConfiguration.CacheEnabled
                ?? ConfigurationDefaults.CacheEnabled,
            CacheDirectory = command.CacheDirectory
                ?? environment.CacheDirectory
                ?? userConfiguration.CacheDirectory,
            FreshnessDelay = freshnessDelay,
            OutputFormat = command.OutputFormat
                ?? environment.OutputFormat
                ?? userConfiguration.OutputFormat
                ?? ConfigurationDefaults.OutputFormat,
            OutputDirectory = command.OutputDirectory
                ?? environment.OutputDirectory
                ?? userConfiguration.OutputDirectory,
            Interaction = command.Interaction
                ?? environment.Interaction
                ?? userConfiguration.Interaction
                ?? ConfigurationDefaults.Interaction,
        };
    }

    private static List<string> ValidateRules(IReadOnlyList<string> rules, string listName)
    {
        var validated = new List<string>(rules.Count);
        foreach (string rule in rules)
        {
            string trimmed = rule?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                throw new FormatException($"The {listName} rules list must not contain empty entries.");
            }

            validated.Add(trimmed);
        }

        return validated;
    }
}
