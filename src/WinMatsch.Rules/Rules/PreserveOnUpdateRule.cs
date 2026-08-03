using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0007: when previous-version manifests are available, carries over hand-maintained fields
/// that analysis or new release data left null. The copied-field list is deliberately explicit:
/// <list type="bullet">
/// <item><description>Per installer, matched by effective Architecture+InstallerType+Scope:
/// <c>InstallerSwitches</c>, <c>Dependencies</c>, <c>Commands</c>,
/// <c>AppsAndFeaturesEntries</c>, and <c>InstallationMetadata</c> (deep-cloned; previous root
/// defaults are looked through).</description></item>
/// <item><description>On the default locale: <c>Author</c>, <c>Moniker</c>, <c>PublisherUrl</c>,
/// <c>PublisherSupportUrl</c>, <c>PrivacyUrl</c>, <c>PackageUrl</c>, <c>License</c>,
/// <c>LicenseUrl</c>, <c>Copyright</c>, <c>CopyrightUrl</c>, <c>ShortDescription</c>,
/// <c>Description</c>, <c>Tags</c>, <c>Documentations</c>, <c>Icons</c>, <c>PurchaseUrl</c> and
/// <c>InstallationNotes</c>. <c>ReleaseNotesUrl</c> is copied only when the previous value does
/// not embed the previous package version (a version-specific URL would be stale).
/// <c>ReleaseNotes</c> and <c>Agreements</c> are never copied: notes are always
/// version-specific, and agreements must be re-verified rather than silently carried
/// forward.</description></item>
/// </list>
/// Existing (non-null) values are never overwritten, and copies are deep clones so later rules
/// cannot mutate the previous manifests through shared references.
/// </summary>
public sealed class PreserveOnUpdateRule : IRule
{
    public string Id => RuleIds.PreserveOnUpdate;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Carries hand-maintained fields over from the previous version when the new data left them null.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Previous is not { } previous)
        {
            return;
        }

        PreserveInstallerFields(context, previous);
        PreserveDefaultLocaleFields(context, previous);
    }

    private void PreserveInstallerFields(ManifestContext context, PackageManifests previous)
    {
        InstallerManifest manifest = context.Manifests.Installer;
        InstallerManifest previousManifest = previous.Installer;
        if (manifest.Installers is not { } installers || previousManifest.Installers is not { } previousInstallers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            string key = EffectiveInstallerValues.GetEntryKey(manifest, installer);
            Installer? match = null;
            foreach (Installer candidate in previousInstallers)
            {
                if (string.Equals(EffectiveInstallerValues.GetEntryKey(previousManifest, candidate), key, StringComparison.Ordinal))
                {
                    match = candidate;
                    break;
                }
            }

            if (match is null)
            {
                continue;
            }

            InstallerSwitches? switches = match.InstallerSwitches
                ?? (manifest.InstallerSwitches is null ? previousManifest.InstallerSwitches : null);
            if (installer.InstallerSwitches is null && switches is not null)
            {
                installer.InstallerSwitches = ManifestValues.CloneSwitches(switches);
                context.AddTrace(this, $"Installers[{i}]: carried InstallerSwitches over from the previous version.");
            }

            Dependencies? dependencies = match.Dependencies
                ?? (manifest.Dependencies is null ? previousManifest.Dependencies : null);
            if (installer.Dependencies is null && dependencies is not null)
            {
                installer.Dependencies = ManifestValues.CloneDependencies(dependencies);
                context.AddTrace(this, $"Installers[{i}]: carried Dependencies over from the previous version.");
            }

            if (installer.Commands is null && manifest.Commands is null
                && (match.Commands ?? previousManifest.Commands) is { } commands)
            {
                installer.Commands = ManifestValues.CloneStringList(commands);
                context.AddTrace(this, $"Installers[{i}]: carried Commands over from the previous version.");
            }

            if (installer.AppsAndFeaturesEntries is null && manifest.AppsAndFeaturesEntries is null
                && (match.AppsAndFeaturesEntries ?? previousManifest.AppsAndFeaturesEntries) is { } entries)
            {
                installer.AppsAndFeaturesEntries = ManifestValues.CloneList(
                    entries,
                    ManifestValues.CloneAppsAndFeaturesEntry);
                context.AddTrace(this, $"Installers[{i}]: carried AppsAndFeaturesEntries over from the previous version.");
            }

            if (installer.InstallationMetadata is null && manifest.InstallationMetadata is null
                && (match.InstallationMetadata ?? previousManifest.InstallationMetadata) is { } metadata)
            {
                installer.InstallationMetadata = ManifestValues.CloneInstallationMetadata(metadata);
                context.AddTrace(this, $"Installers[{i}]: carried InstallationMetadata over from the previous version.");
            }
        }
    }

    private void PreserveDefaultLocaleFields(ManifestContext context, PackageManifests previous)
    {
        DefaultLocaleManifest locale = context.Manifests.DefaultLocale;
        DefaultLocaleManifest previousLocale = previous.DefaultLocale;

        locale.Author = Copy(context, locale.Author, previousLocale.Author, nameof(locale.Author));
        locale.Moniker = Copy(context, locale.Moniker, previousLocale.Moniker, nameof(locale.Moniker));
        locale.PublisherUrl = Copy(context, locale.PublisherUrl, previousLocale.PublisherUrl, nameof(locale.PublisherUrl));
        locale.PublisherSupportUrl = Copy(context, locale.PublisherSupportUrl, previousLocale.PublisherSupportUrl, nameof(locale.PublisherSupportUrl));
        locale.PrivacyUrl = Copy(context, locale.PrivacyUrl, previousLocale.PrivacyUrl, nameof(locale.PrivacyUrl));
        locale.PackageUrl = Copy(context, locale.PackageUrl, previousLocale.PackageUrl, nameof(locale.PackageUrl));
        locale.License = Copy(context, locale.License, previousLocale.License, nameof(locale.License));
        locale.LicenseUrl = Copy(context, locale.LicenseUrl, previousLocale.LicenseUrl, nameof(locale.LicenseUrl));
        locale.Copyright = Copy(context, locale.Copyright, previousLocale.Copyright, nameof(locale.Copyright));
        locale.CopyrightUrl = Copy(context, locale.CopyrightUrl, previousLocale.CopyrightUrl, nameof(locale.CopyrightUrl));
        locale.ShortDescription = Copy(context, locale.ShortDescription, previousLocale.ShortDescription, nameof(locale.ShortDescription));
        locale.Description = Copy(context, locale.Description, previousLocale.Description, nameof(locale.Description));
        locale.PurchaseUrl = Copy(context, locale.PurchaseUrl, previousLocale.PurchaseUrl, nameof(locale.PurchaseUrl));
        locale.InstallationNotes = Copy(context, locale.InstallationNotes, previousLocale.InstallationNotes, nameof(locale.InstallationNotes));

        if (locale.Tags is null && previousLocale.Tags is { } tags)
        {
            locale.Tags = ManifestValues.CloneStringList(tags);
            context.AddTrace(this, "DefaultLocale: carried Tags over from the previous version.");
        }

        if (locale.Documentations is null && previousLocale.Documentations is { } documentations)
        {
            locale.Documentations = ManifestValues.CloneList(documentations, ManifestValues.CloneDocumentation);
            context.AddTrace(this, "DefaultLocale: carried Documentations over from the previous version.");
        }

        if (locale.Icons is null && previousLocale.Icons is { } icons)
        {
            locale.Icons = ManifestValues.CloneList(icons, ManifestValues.CloneIcon);
            context.AddTrace(this, "DefaultLocale: carried Icons over from the previous version.");
        }

        if (locale.ReleaseNotesUrl is null && previousLocale.ReleaseNotesUrl is { } releaseNotesUrl)
        {
            string? previousVersion = previous.Installer.PackageVersion?.Value;
            bool versionSpecific = previousVersion is not null
                && releaseNotesUrl.Contains(previousVersion, StringComparison.OrdinalIgnoreCase);
            if (!versionSpecific)
            {
                locale.ReleaseNotesUrl = releaseNotesUrl;
                context.AddTrace(this, "DefaultLocale: carried ReleaseNotesUrl over from the previous version (it does not embed the previous version).");
            }
        }
    }

    private string? Copy(ManifestContext context, string? current, string? previous, string fieldName)
    {
        if (current is null && previous is not null)
        {
            context.AddTrace(this, $"DefaultLocale: carried {fieldName} over from the previous version.");
            return previous;
        }

        return current;
    }
}
