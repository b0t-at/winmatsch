namespace WinMatsch.Core;

/// <summary>A WinGet version manifest (<c>ManifestType: version</c>).</summary>
public sealed class VersionManifest
{
    public PackageIdentifier? PackageIdentifier { get; set; }

    public PackageVersion? PackageVersion { get; set; }

    public LanguageTag? DefaultLocale { get; set; }

    public ManifestType ManifestType { get; set; } = ManifestType.Version;

    public ManifestVersion ManifestVersion { get; set; } = ManifestVersion.Default;
}
