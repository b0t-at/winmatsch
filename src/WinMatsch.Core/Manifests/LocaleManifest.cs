namespace WinMatsch.Core;

/// <summary>
/// A WinGet locale manifest (<c>ManifestType: locale</c>).
/// <see cref="DefaultLocaleManifest"/> derives from this and adds the default-locale-only fields.
/// </summary>
public class LocaleManifest
{
    public PackageIdentifier? PackageIdentifier { get; set; }

    public PackageVersion? PackageVersion { get; set; }

    public LanguageTag? PackageLocale { get; set; }

    public string? Publisher { get; set; }

    public string? PublisherUrl { get; set; }

    public string? PublisherSupportUrl { get; set; }

    public string? PrivacyUrl { get; set; }

    public string? Author { get; set; }

    public string? PackageName { get; set; }

    public string? PackageUrl { get; set; }

    public string? License { get; set; }

    public string? LicenseUrl { get; set; }

    public string? Copyright { get; set; }

    public string? CopyrightUrl { get; set; }

    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    public List<string>? Tags { get; set; }

    public List<PackageAgreement>? Agreements { get; set; }

    public string? ReleaseNotes { get; set; }

    public string? ReleaseNotesUrl { get; set; }

    public string? PurchaseUrl { get; set; }

    public string? InstallationNotes { get; set; }

    public List<Documentation>? Documentations { get; set; }

    public List<Icon>? Icons { get; set; }

    public ManifestType ManifestType { get; set; } = ManifestType.Locale;

    public ManifestVersion ManifestVersion { get; set; } = ManifestVersion.Default;
}

/// <summary>A WinGet default locale manifest (<c>ManifestType: defaultLocale</c>).</summary>
public sealed class DefaultLocaleManifest : LocaleManifest
{
    public string? Moniker { get; set; }

    public DefaultLocaleManifest()
    {
        ManifestType = ManifestType.DefaultLocale;
    }
}
