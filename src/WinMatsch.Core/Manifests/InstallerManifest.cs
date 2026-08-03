namespace WinMatsch.Core;

/// <summary>A WinGet installer manifest (<c>ManifestType: installer</c>).</summary>
public sealed class InstallerManifest : InstallerFieldsBase
{
    public PackageIdentifier? PackageIdentifier { get; set; }

    public PackageVersion? PackageVersion { get; set; }

    public string? Channel { get; set; }

    public List<Installer>? Installers { get; set; }

    public ManifestType ManifestType { get; set; } = ManifestType.Installer;

    public ManifestVersion ManifestVersion { get; set; } = ManifestVersion.Default;
}
