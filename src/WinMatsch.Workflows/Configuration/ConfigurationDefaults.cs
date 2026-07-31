namespace WinMatsch.Workflows.Configuration;

/// <summary>The built-in defaults applied when no other layer sets a value.</summary>
public static class ConfigurationDefaults
{
    public const string Repository = "microsoft/winget-pkgs";

    public const int ConcurrentDownloads = 2;

    public const bool CacheEnabled = true;

    public static readonly TimeSpan FreshnessDelay = TimeSpan.Zero;

    public const OutputFormat OutputFormat = Configuration.OutputFormat.Text;

    public const InteractionMode Interaction = InteractionMode.Auto;
}
