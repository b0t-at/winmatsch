using WinMatsch.Core;

namespace WinMatsch.Rules;

internal static class ManifestClone
{
    private static readonly PackageIdentifier _placeholderIdentifier = new("WinMatsch.Missing");
    private static readonly PackageVersion _placeholderVersion = new("0.0.0");
    private static readonly LanguageTag _placeholderLocale = new("und");
    private static readonly Sha256Hash _placeholderHash = new(new string('E', Sha256Hash.Length));

    public static PackageManifests CreateSerializable(PackageManifests source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PackageManifests clone = Clone(source);
        EnsureSerializable(clone);
        return clone;
    }

    public static PackageManifests Clone(PackageManifests source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new()
        {
            Installer = CloneInstallerManifest(source.Installer),
            DefaultLocale = CloneDefaultLocale(source.DefaultLocale),
            Locales = [.. source.Locales.Select(CloneLocale)],
            Version = new()
            {
                PackageIdentifier = source.Version.PackageIdentifier,
                PackageVersion = source.Version.PackageVersion,
                DefaultLocale = source.Version.DefaultLocale,
                ManifestType = source.Version.ManifestType,
                ManifestVersion = source.Version.ManifestVersion,
            },
        };
    }

    private static InstallerManifest CloneInstallerManifest(InstallerManifest source)
    {
        var clone = new InstallerManifest
        {
            PackageIdentifier = source.PackageIdentifier,
            PackageVersion = source.PackageVersion,
            Channel = source.Channel,
            Installers = source.Installers?.ConvertAll(CloneInstaller),
            ManifestType = source.ManifestType,
            ManifestVersion = source.ManifestVersion,
        };
        CloneInstallerFields(source, clone);
        return clone;
    }

    private static Installer CloneInstaller(Installer source)
    {
        var clone = new Installer
        {
            Architecture = source.Architecture,
            InstallerUrl = source.InstallerUrl,
            InstallerSha256 = source.InstallerSha256,
            SignatureSha256 = source.SignatureSha256,
        };
        CloneInstallerFields(source, clone);
        return clone;
    }

    private static void CloneInstallerFields(InstallerFieldsBase source, InstallerFieldsBase target)
    {
        target.InstallerLocale = source.InstallerLocale;
        target.Platform = source.Platform is null ? null : [.. source.Platform];
        target.MinimumOSVersion = source.MinimumOSVersion;
        target.InstallerType = source.InstallerType;
        target.NestedInstallerType = source.NestedInstallerType;
        target.NestedInstallerFiles = ManifestValues.CloneList(
            source.NestedInstallerFiles,
            ManifestValues.CloneNestedInstallerFile);
        target.Scope = source.Scope;
        target.InstallModes = source.InstallModes is null ? null : [.. source.InstallModes];
        target.InstallerSwitches = source.InstallerSwitches is null
            ? null
            : ManifestValues.CloneSwitches(source.InstallerSwitches);
        target.InstallerSuccessCodes = source.InstallerSuccessCodes is null
            ? null
            : [.. source.InstallerSuccessCodes];
        target.ExpectedReturnCodes = ManifestValues.CloneList(
            source.ExpectedReturnCodes,
            ManifestValues.CloneExpectedReturnCode);
        target.UpgradeBehavior = source.UpgradeBehavior;
        target.Commands = ManifestValues.CloneStringList(source.Commands);
        target.Protocols = ManifestValues.CloneStringList(source.Protocols);
        target.FileExtensions = ManifestValues.CloneStringList(source.FileExtensions);
        target.Dependencies = source.Dependencies is null
            ? null
            : ManifestValues.CloneDependencies(source.Dependencies);
        target.PackageFamilyName = source.PackageFamilyName;
        target.ProductCode = source.ProductCode;
        target.Capabilities = ManifestValues.CloneStringList(source.Capabilities);
        target.RestrictedCapabilities = ManifestValues.CloneStringList(source.RestrictedCapabilities);
        target.Markets = source.Markets is null ? null : ManifestValues.CloneMarkets(source.Markets);
        target.InstallerAbortsTerminal = source.InstallerAbortsTerminal;
        target.ReleaseDate = source.ReleaseDate;
        target.InstallLocationRequired = source.InstallLocationRequired;
        target.RequireExplicitUpgrade = source.RequireExplicitUpgrade;
        target.DisplayInstallWarnings = source.DisplayInstallWarnings;
        target.UnsupportedOSArchitectures = source.UnsupportedOSArchitectures is null
            ? null
            : [.. source.UnsupportedOSArchitectures];
        target.UnsupportedArguments = source.UnsupportedArguments is null ? null : [.. source.UnsupportedArguments];
        target.AppsAndFeaturesEntries = ManifestValues.CloneList(
            source.AppsAndFeaturesEntries,
            ManifestValues.CloneAppsAndFeaturesEntry);
        target.ElevationRequirement = source.ElevationRequirement;
        target.InstallationMetadata = source.InstallationMetadata is null
            ? null
            : ManifestValues.CloneInstallationMetadata(source.InstallationMetadata);
        target.DownloadCommandProhibited = source.DownloadCommandProhibited;
        target.RepairBehavior = source.RepairBehavior;
        target.ArchiveBinariesDependOnPath = source.ArchiveBinariesDependOnPath;
        target.Authentication = source.Authentication is null
            ? null
            : ManifestValues.CloneAuthentication(source.Authentication);
    }

    private static DefaultLocaleManifest CloneDefaultLocale(DefaultLocaleManifest source)
    {
        var clone = new DefaultLocaleManifest { Moniker = source.Moniker };
        CloneLocaleFields(source, clone);
        return clone;
    }

    private static LocaleManifest CloneLocale(LocaleManifest source)
    {
        var clone = new LocaleManifest();
        CloneLocaleFields(source, clone);
        return clone;
    }

    private static void CloneLocaleFields(LocaleManifest source, LocaleManifest target)
    {
        target.PackageIdentifier = source.PackageIdentifier;
        target.PackageVersion = source.PackageVersion;
        target.PackageLocale = source.PackageLocale;
        target.Publisher = source.Publisher;
        target.PublisherUrl = source.PublisherUrl;
        target.PublisherSupportUrl = source.PublisherSupportUrl;
        target.PrivacyUrl = source.PrivacyUrl;
        target.Author = source.Author;
        target.PackageName = source.PackageName;
        target.PackageUrl = source.PackageUrl;
        target.License = source.License;
        target.LicenseUrl = source.LicenseUrl;
        target.Copyright = source.Copyright;
        target.CopyrightUrl = source.CopyrightUrl;
        target.ShortDescription = source.ShortDescription;
        target.Description = source.Description;
        target.Tags = ManifestValues.CloneStringList(source.Tags);
        target.Agreements = source.Agreements?.ConvertAll(static value => new PackageAgreement
        {
            AgreementLabel = value.AgreementLabel,
            Agreement = value.Agreement,
            AgreementUrl = value.AgreementUrl,
        });
        target.ReleaseNotes = source.ReleaseNotes;
        target.ReleaseNotesUrl = source.ReleaseNotesUrl;
        target.PurchaseUrl = source.PurchaseUrl;
        target.InstallationNotes = source.InstallationNotes;
        target.Documentations = ManifestValues.CloneList(source.Documentations, ManifestValues.CloneDocumentation);
        target.Icons = ManifestValues.CloneList(source.Icons, ManifestValues.CloneIcon);
        target.ManifestType = source.ManifestType;
        target.ManifestVersion = source.ManifestVersion;
    }

    private static void EnsureSerializable(PackageManifests manifests)
    {
        manifests.Version.PackageIdentifier ??= _placeholderIdentifier;
        manifests.Version.PackageVersion ??= _placeholderVersion;
        manifests.Version.DefaultLocale ??= _placeholderLocale;
        manifests.Version.ManifestType = ManifestType.Version;
        manifests.Version.ManifestVersion ??= ManifestVersion.Default;

        EnsureSerializable(manifests.Installer);
        EnsureSerializable(manifests.DefaultLocale);
        foreach (LocaleManifest locale in manifests.Locales)
        {
            locale.ManifestType = ManifestType.Locale;
            EnsureSerializable(locale);
        }
    }

    private static void EnsureSerializable(InstallerManifest manifest)
    {
        manifest.PackageIdentifier ??= _placeholderIdentifier;
        manifest.PackageVersion ??= _placeholderVersion;
        manifest.ManifestType = ManifestType.Installer;
        manifest.ManifestVersion ??= ManifestVersion.Default;
        manifest.Installers ??= [];
        if (manifest.Installers.Count == 0)
        {
            manifest.Installers.Add(new Installer());
        }

        EnsureSerializableFields(manifest);
        foreach (Installer installer in manifest.Installers)
        {
            installer.Architecture ??= Architecture.Neutral;
            installer.InstallerUrl ??= "https://invalid.example/placeholder";
            installer.InstallerSha256 ??= _placeholderHash;
            EnsureSerializableFields(installer);
        }
    }

    private static void EnsureSerializable(LocaleManifest manifest)
    {
        manifest.PackageIdentifier ??= _placeholderIdentifier;
        manifest.PackageVersion ??= _placeholderVersion;
        manifest.PackageLocale ??= _placeholderLocale;
        manifest.ManifestVersion ??= ManifestVersion.Default;
        if (manifest is DefaultLocaleManifest defaultLocale)
        {
            defaultLocale.ManifestType = ManifestType.DefaultLocale;
            defaultLocale.Publisher ??= "Missing";
            defaultLocale.PackageName ??= "Missing";
            defaultLocale.License ??= "Unknown";
            defaultLocale.ShortDescription ??= "Missing";
        }

        if (manifest.Icons is { } icons)
        {
            foreach (Icon icon in icons)
            {
                icon.IconUrl ??= "https://invalid.example/placeholder.ico";
                icon.IconFileType ??= IconFileType.Ico;
            }
        }
    }

    private static void EnsureSerializableFields(InstallerFieldsBase fields)
    {
        if (fields.ExpectedReturnCodes is { } expectedReturnCodes)
        {
            foreach (ExpectedReturnCode code in expectedReturnCodes)
            {
                code.InstallerReturnCode ??= 1;
                code.ReturnResponse ??= ReturnResponse.Custom;
            }
        }

        if (fields.NestedInstallerFiles is { } nestedInstallerFiles)
        {
            foreach (NestedInstallerFile file in nestedInstallerFiles)
            {
                file.RelativeFilePath ??= "missing.exe";
            }
        }

        if (fields.Dependencies?.PackageDependencies is { } packageDependencies)
        {
            foreach (PackageDependency dependency in packageDependencies)
            {
                dependency.PackageIdentifier ??= _placeholderIdentifier;
            }
        }

        if (fields.InstallationMetadata?.Files is { } installedFiles)
        {
            foreach (InstalledFile file in installedFiles)
            {
                file.RelativeFilePath ??= "missing.exe";
            }
        }

        if (fields.Authentication is { } authentication)
        {
            authentication.AuthenticationType ??= AuthenticationType.None;
        }

        if (fields.Markets is { } markets)
        {
            if (markets.AllowedMarkets is null && markets.ExcludedMarkets is null)
            {
                markets.AllowedMarkets = [];
            }
            else if (markets.AllowedMarkets is not null && markets.ExcludedMarkets is not null)
            {
                markets.ExcludedMarkets = null;
            }
        }
    }
}
