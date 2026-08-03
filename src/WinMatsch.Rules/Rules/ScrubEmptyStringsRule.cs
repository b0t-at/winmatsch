using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0004: recursively (via a hand-written per-property walk, no reflection) nulls out empty
/// and whitespace-only strings, removes empty items from lists, drops lists that become empty
/// and prunes composite objects whose members all became null (empty <c>InstallerSwitches</c>,
/// <c>AppsAndFeaturesEntries: [- {}]</c>, <c>InstallationMetadata: {}</c> and friends — all
/// junk shapes observed in real winget-pkgs fix PRs). Covers all four manifest types.
/// </summary>
public sealed class ScrubEmptyStringsRule : IRule
{
    public string Id => RuleIds.ScrubEmptyStrings;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Nulls out empty strings, drops empty lists and prunes objects that become empty.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int changes = 0;
        InstallerManifest manifest = context.Manifests.Installer;
        manifest.Channel = Clean(manifest.Channel, ref changes);
        ScrubInstallerFields(manifest, ref changes);
        if (manifest.Installers is { } installers)
        {
            foreach (Installer installer in installers)
            {
                installer.InstallerUrl = Clean(installer.InstallerUrl, ref changes);
                ScrubInstallerFields(installer, ref changes);
            }
        }

        ScrubLocale(context.Manifests.DefaultLocale, ref changes);
        foreach (LocaleManifest locale in context.Manifests.Locales)
        {
            ScrubLocale(locale, ref changes);
        }

        if (changes > 0)
        {
            context.AddTrace(this, $"Scrubbed {changes} empty value(s).");
        }
    }

    private static void ScrubInstallerFields(InstallerFieldsBase fields, ref int changes)
    {
        fields.PackageFamilyName = Clean(fields.PackageFamilyName, ref changes);
        fields.ProductCode = Clean(fields.ProductCode, ref changes);

        fields.Platform = DropIfEmpty(fields.Platform, ref changes);
        fields.InstallModes = DropIfEmpty(fields.InstallModes, ref changes);
        fields.InstallerSuccessCodes = DropIfEmpty(fields.InstallerSuccessCodes, ref changes);
        fields.UnsupportedOSArchitectures = DropIfEmpty(fields.UnsupportedOSArchitectures, ref changes);
        fields.UnsupportedArguments = DropIfEmpty(fields.UnsupportedArguments, ref changes);

        fields.Commands = CleanStringList(fields.Commands, ref changes);
        fields.Protocols = CleanStringList(fields.Protocols, ref changes);
        fields.FileExtensions = CleanStringList(fields.FileExtensions, ref changes);
        fields.Capabilities = CleanStringList(fields.Capabilities, ref changes);
        fields.RestrictedCapabilities = CleanStringList(fields.RestrictedCapabilities, ref changes);

        if (fields.InstallerSwitches is { } switches)
        {
            switches.Silent = Clean(switches.Silent, ref changes);
            switches.SilentWithProgress = Clean(switches.SilentWithProgress, ref changes);
            switches.Interactive = Clean(switches.Interactive, ref changes);
            switches.InstallLocation = Clean(switches.InstallLocation, ref changes);
            switches.Log = Clean(switches.Log, ref changes);
            switches.Upgrade = Clean(switches.Upgrade, ref changes);
            switches.Custom = Clean(switches.Custom, ref changes);
            switches.Repair = Clean(switches.Repair, ref changes);
            if (switches.IsEmpty)
            {
                fields.InstallerSwitches = null;
                changes++;
            }
        }

        if (fields.ExpectedReturnCodes is { } returnCodes)
        {
            foreach (ExpectedReturnCode code in returnCodes)
            {
                code.ReturnResponseUrl = Clean(code.ReturnResponseUrl, ref changes);
            }

            changes += returnCodes.RemoveAll(static c => c.InstallerReturnCode is null && c.ReturnResponse is null && c.ReturnResponseUrl is null);
            fields.ExpectedReturnCodes = DropIfEmpty(fields.ExpectedReturnCodes, ref changes);
        }

        if (fields.NestedInstallerFiles is { } nestedFiles)
        {
            foreach (NestedInstallerFile file in nestedFiles)
            {
                file.RelativeFilePath = Clean(file.RelativeFilePath, ref changes);
                file.PortableCommandAlias = Clean(file.PortableCommandAlias, ref changes);
            }

            changes += nestedFiles.RemoveAll(static f => f.RelativeFilePath is null && f.PortableCommandAlias is null);
            fields.NestedInstallerFiles = DropIfEmpty(fields.NestedInstallerFiles, ref changes);
        }

        if (fields.AppsAndFeaturesEntries is { } arpEntries)
        {
            foreach (AppsAndFeaturesEntry entry in arpEntries)
            {
                entry.DisplayName = Clean(entry.DisplayName, ref changes);
                entry.Publisher = Clean(entry.Publisher, ref changes);
                entry.DisplayVersion = Clean(entry.DisplayVersion, ref changes);
                entry.ProductCode = Clean(entry.ProductCode, ref changes);
                entry.UpgradeCode = Clean(entry.UpgradeCode, ref changes);
            }

            changes += arpEntries.RemoveAll(static e =>
                e.DisplayName is null && e.Publisher is null && e.DisplayVersion is null
                && e.ProductCode is null && e.UpgradeCode is null && e.InstallerType is null);
            fields.AppsAndFeaturesEntries = DropIfEmpty(fields.AppsAndFeaturesEntries, ref changes);
        }

        if (fields.Dependencies is { } dependencies)
        {
            dependencies.WindowsFeatures = CleanStringList(dependencies.WindowsFeatures, ref changes);
            dependencies.WindowsLibraries = CleanStringList(dependencies.WindowsLibraries, ref changes);
            dependencies.ExternalDependencies = CleanStringList(dependencies.ExternalDependencies, ref changes);
            if (dependencies.PackageDependencies is { } packageDependencies)
            {
                changes += packageDependencies.RemoveAll(static d => d.PackageIdentifier is null && d.MinimumVersion is null);
                dependencies.PackageDependencies = DropIfEmpty(dependencies.PackageDependencies, ref changes);
            }

            if (dependencies.WindowsFeatures is null && dependencies.WindowsLibraries is null
                && dependencies.PackageDependencies is null && dependencies.ExternalDependencies is null)
            {
                fields.Dependencies = null;
                changes++;
            }
        }

        if (fields.Markets is { } markets)
        {
            markets.AllowedMarkets = CleanStringList(markets.AllowedMarkets, ref changes);
            markets.ExcludedMarkets = CleanStringList(markets.ExcludedMarkets, ref changes);
            if (markets.AllowedMarkets is null && markets.ExcludedMarkets is null)
            {
                fields.Markets = null;
                changes++;
            }
        }

        if (fields.InstallationMetadata is { } metadata)
        {
            metadata.DefaultInstallLocation = Clean(metadata.DefaultInstallLocation, ref changes);
            if (metadata.Files is { } files)
            {
                foreach (InstalledFile file in files)
                {
                    file.RelativeFilePath = Clean(file.RelativeFilePath, ref changes);
                    file.InvocationParameter = Clean(file.InvocationParameter, ref changes);
                    file.DisplayName = Clean(file.DisplayName, ref changes);
                }

                changes += files.RemoveAll(static f =>
                    f.RelativeFilePath is null && f.FileSha256 is null && f.FileType is null
                    && f.InvocationParameter is null && f.DisplayName is null);
                metadata.Files = DropIfEmpty(metadata.Files, ref changes);
            }

            if (metadata.DefaultInstallLocation is null && metadata.Files is null)
            {
                fields.InstallationMetadata = null;
                changes++;
            }
        }

        if (fields.Authentication is { } authentication)
        {
            if (authentication.MicrosoftEntraIdAuthenticationInfo is { } entra)
            {
                entra.Resource = Clean(entra.Resource, ref changes);
                entra.Scope = Clean(entra.Scope, ref changes);
                if (entra.Resource is null && entra.Scope is null)
                {
                    authentication.MicrosoftEntraIdAuthenticationInfo = null;
                    changes++;
                }
            }

            if (authentication.AuthenticationType is null && authentication.MicrosoftEntraIdAuthenticationInfo is null)
            {
                fields.Authentication = null;
                changes++;
            }
        }
    }

    private static void ScrubLocale(LocaleManifest locale, ref int changes)
    {
        locale.Publisher = Clean(locale.Publisher, ref changes);
        locale.PublisherUrl = Clean(locale.PublisherUrl, ref changes);
        locale.PublisherSupportUrl = Clean(locale.PublisherSupportUrl, ref changes);
        locale.PrivacyUrl = Clean(locale.PrivacyUrl, ref changes);
        locale.Author = Clean(locale.Author, ref changes);
        locale.PackageName = Clean(locale.PackageName, ref changes);
        locale.PackageUrl = Clean(locale.PackageUrl, ref changes);
        locale.License = Clean(locale.License, ref changes);
        locale.LicenseUrl = Clean(locale.LicenseUrl, ref changes);
        locale.Copyright = Clean(locale.Copyright, ref changes);
        locale.CopyrightUrl = Clean(locale.CopyrightUrl, ref changes);
        locale.ShortDescription = Clean(locale.ShortDescription, ref changes);
        locale.Description = Clean(locale.Description, ref changes);
        locale.ReleaseNotes = Clean(locale.ReleaseNotes, ref changes);
        locale.ReleaseNotesUrl = Clean(locale.ReleaseNotesUrl, ref changes);
        locale.PurchaseUrl = Clean(locale.PurchaseUrl, ref changes);
        locale.InstallationNotes = Clean(locale.InstallationNotes, ref changes);
        locale.Tags = CleanStringList(locale.Tags, ref changes);

        if (locale is DefaultLocaleManifest defaultLocale)
        {
            defaultLocale.Moniker = Clean(defaultLocale.Moniker, ref changes);
        }

        if (locale.Agreements is { } agreements)
        {
            foreach (PackageAgreement agreement in agreements)
            {
                agreement.AgreementLabel = Clean(agreement.AgreementLabel, ref changes);
                agreement.Agreement = Clean(agreement.Agreement, ref changes);
                agreement.AgreementUrl = Clean(agreement.AgreementUrl, ref changes);
            }

            changes += agreements.RemoveAll(static a => a.AgreementLabel is null && a.Agreement is null && a.AgreementUrl is null);
            locale.Agreements = DropIfEmpty(locale.Agreements, ref changes);
        }

        if (locale.Documentations is { } documentations)
        {
            foreach (Documentation documentation in documentations)
            {
                documentation.DocumentLabel = Clean(documentation.DocumentLabel, ref changes);
                documentation.DocumentUrl = Clean(documentation.DocumentUrl, ref changes);
            }

            changes += documentations.RemoveAll(static d => d.DocumentLabel is null && d.DocumentUrl is null);
            locale.Documentations = DropIfEmpty(locale.Documentations, ref changes);
        }

        if (locale.Icons is { } icons)
        {
            foreach (Icon icon in icons)
            {
                icon.IconUrl = Clean(icon.IconUrl, ref changes);
            }

            changes += icons.RemoveAll(static i =>
                i.IconUrl is null && i.IconFileType is null && i.IconResolution is null
                && i.IconTheme is null && i.IconSha256 is null);
            locale.Icons = DropIfEmpty(locale.Icons, ref changes);
        }
    }

    private static string? Clean(string? value, ref int changes)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            changes++;
            return null;
        }

        return value;
    }

    private static List<string>? CleanStringList(List<string>? list, ref int changes)
    {
        if (list is null)
        {
            return null;
        }

        changes += list.RemoveAll(string.IsNullOrWhiteSpace);
        return DropIfEmpty(list, ref changes);
    }

    private static List<T>? DropIfEmpty<T>(List<T>? list, ref int changes)
    {
        if (list is { Count: 0 })
        {
            changes++;
            return null;
        }

        return list;
    }
}
