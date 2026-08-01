using WinMatsch.GitHub;

namespace WinMatsch.Workflows.Configuration;

/// <summary>The fully resolved, validated configuration the workflows run with.</summary>
public sealed record WinMatschConfiguration
{
    public required RepositoryCoordinates Repository { get; init; }

    public required int ConcurrentDownloads { get; init; }

    public required IReadOnlyList<string> EnabledRules { get; init; }

    public required IReadOnlyList<string> DisabledRules { get; init; }

    public required bool CacheEnabled { get; init; }

    /// <summary>Null selects the platform default cache location.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Null selects the platform default learned-override store.</summary>
    public string? OverrideStoreDirectory { get; init; }

    public required TimeSpan FreshnessDelay { get; init; }

    public required OutputFormat OutputFormat { get; init; }

    /// <summary>Null writes reports to the current directory.</summary>
    public string? OutputDirectory { get; init; }

    public required InteractionMode Interaction { get; init; }
}
