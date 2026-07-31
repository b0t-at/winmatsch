namespace WinMatsch.Workflows.Configuration;

/// <summary>
/// One partially specified configuration source (command line, environment, or user config
/// file). Null means "not set here"; the resolver falls through to the next layer.
/// </summary>
public sealed record ConfigurationLayer
{
    /// <summary>A layer that sets nothing.</summary>
    public static readonly ConfigurationLayer Empty = new();

    /// <summary>The target repository in <c>owner/name</c> form.</summary>
    public string? Repository { get; init; }

    public int? ConcurrentDownloads { get; init; }

    public IReadOnlyList<string>? EnabledRules { get; init; }

    public IReadOnlyList<string>? DisabledRules { get; init; }

    public bool? CacheEnabled { get; init; }

    public string? CacheDirectory { get; init; }

    /// <summary>How long a package must remain unchanged before it is checked.</summary>
    public TimeSpan? FreshnessDelay { get; init; }

    public OutputFormat? OutputFormat { get; init; }

    public string? OutputDirectory { get; init; }

    public InteractionMode? Interaction { get; init; }
}
