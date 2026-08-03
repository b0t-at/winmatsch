namespace WinMatsch.Core.Yaml;

/// <summary>Options controlling manifest YAML output.</summary>
public sealed class ManifestWriteOptions
{
    public static readonly ManifestWriteOptions Default = new();

    /// <summary>
    /// Tool attribution written as a <c># Created with ...</c> comment header, e.g. <c>winmatsch v0.1.0</c>.
    /// Omitted when null.
    /// </summary>
    public string? CreatedWith { get; init; }

    /// <summary>Whether to write the <c># yaml-language-server: $schema=...</c> header line.</summary>
    public bool IncludeSchemaHeader { get; init; } = true;
}
