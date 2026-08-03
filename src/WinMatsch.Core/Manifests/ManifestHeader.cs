namespace WinMatsch.Core;

/// <summary>
/// The minimal header common to every manifest file, used to detect the manifest type before
/// deserializing into the full model. Everything is kept as raw strings so that sniffing never
/// fails on unexpected content.
/// </summary>
public sealed class ManifestHeader
{
    public string? PackageIdentifier { get; set; }

    public string? PackageVersion { get; set; }

    public string? ManifestType { get; set; }

    public string? ManifestVersion { get; set; }
}
