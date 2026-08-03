namespace WinMatsch.Core;

/// <summary>The complete set of manifests describing one version of one package.</summary>
public sealed class PackageManifests
{
    public required InstallerManifest Installer { get; set; }

    public required DefaultLocaleManifest DefaultLocale { get; set; }

    public List<LocaleManifest> Locales { get; set; } = [];

    public required VersionManifest Version { get; set; }
}
