using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// Hand-written deep equality and deep clone helpers for the composite types used by installer
/// fields. Written out by hand (no reflection) so the rules engine stays AOT-analyzable.
/// String comparisons are ordinal; the immutable primitives (identifiers, versions, hashes,
/// tags) compare via their own value equality and are shared rather than cloned.
/// </summary>
internal static class ManifestValues
{
    public static bool StringEqual(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    public static bool ListEqual<T>(List<T>? a, List<T>? b, Func<T, T, bool> itemEqual)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!itemEqual(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static List<T>? CloneList<T>(List<T>? source, Func<T, T> itemClone) => source?.ConvertAll(x => itemClone(x));

    public static List<string>? CloneStringList(List<string>? source) => source is null ? null : [.. source];

    public static bool SwitchesEqual(InstallerSwitches a, InstallerSwitches b) =>
        StringEqual(a.Silent, b.Silent)
        && StringEqual(a.SilentWithProgress, b.SilentWithProgress)
        && StringEqual(a.Interactive, b.Interactive)
        && StringEqual(a.InstallLocation, b.InstallLocation)
        && StringEqual(a.Log, b.Log)
        && StringEqual(a.Upgrade, b.Upgrade)
        && StringEqual(a.Custom, b.Custom)
        && StringEqual(a.Repair, b.Repair);

    public static InstallerSwitches CloneSwitches(InstallerSwitches source) => new()
    {
        Silent = source.Silent,
        SilentWithProgress = source.SilentWithProgress,
        Interactive = source.Interactive,
        InstallLocation = source.InstallLocation,
        Log = source.Log,
        Upgrade = source.Upgrade,
        Custom = source.Custom,
        Repair = source.Repair,
    };

    public static bool ExpectedReturnCodeEqual(ExpectedReturnCode a, ExpectedReturnCode b) =>
        a.InstallerReturnCode == b.InstallerReturnCode
        && a.ReturnResponse == b.ReturnResponse
        && StringEqual(a.ReturnResponseUrl, b.ReturnResponseUrl);

    public static ExpectedReturnCode CloneExpectedReturnCode(ExpectedReturnCode source) => new()
    {
        InstallerReturnCode = source.InstallerReturnCode,
        ReturnResponse = source.ReturnResponse,
        ReturnResponseUrl = source.ReturnResponseUrl,
    };

    public static bool NestedInstallerFileEqual(NestedInstallerFile a, NestedInstallerFile b) =>
        StringEqual(a.RelativeFilePath, b.RelativeFilePath)
        && StringEqual(a.PortableCommandAlias, b.PortableCommandAlias);

    public static NestedInstallerFile CloneNestedInstallerFile(NestedInstallerFile source) => new()
    {
        RelativeFilePath = source.RelativeFilePath,
        PortableCommandAlias = source.PortableCommandAlias,
    };

    public static bool AppsAndFeaturesEntryEqual(AppsAndFeaturesEntry a, AppsAndFeaturesEntry b) =>
        StringEqual(a.DisplayName, b.DisplayName)
        && StringEqual(a.Publisher, b.Publisher)
        && StringEqual(a.DisplayVersion, b.DisplayVersion)
        && StringEqual(a.ProductCode, b.ProductCode)
        && StringEqual(a.UpgradeCode, b.UpgradeCode)
        && a.InstallerType == b.InstallerType;

    public static AppsAndFeaturesEntry CloneAppsAndFeaturesEntry(AppsAndFeaturesEntry source) => new()
    {
        DisplayName = source.DisplayName,
        Publisher = source.Publisher,
        DisplayVersion = source.DisplayVersion,
        ProductCode = source.ProductCode,
        UpgradeCode = source.UpgradeCode,
        InstallerType = source.InstallerType,
    };

    public static bool PackageDependencyEqual(PackageDependency a, PackageDependency b) =>
        Equals(a.PackageIdentifier, b.PackageIdentifier)
        && Equals(a.MinimumVersion, b.MinimumVersion);

    public static PackageDependency ClonePackageDependency(PackageDependency source) => new()
    {
        PackageIdentifier = source.PackageIdentifier,
        MinimumVersion = source.MinimumVersion,
    };

    public static bool DependenciesEqual(Dependencies a, Dependencies b) =>
        ListEqual(a.WindowsFeatures, b.WindowsFeatures, StringEqual)
        && ListEqual(a.WindowsLibraries, b.WindowsLibraries, StringEqual)
        && ListEqual(a.PackageDependencies, b.PackageDependencies, PackageDependencyEqual)
        && ListEqual(a.ExternalDependencies, b.ExternalDependencies, StringEqual);

    public static Dependencies CloneDependencies(Dependencies source) => new()
    {
        WindowsFeatures = CloneStringList(source.WindowsFeatures),
        WindowsLibraries = CloneStringList(source.WindowsLibraries),
        PackageDependencies = CloneList(source.PackageDependencies, ClonePackageDependency),
        ExternalDependencies = CloneStringList(source.ExternalDependencies),
    };

    public static bool MarketsEqual(Markets a, Markets b) =>
        ListEqual(a.AllowedMarkets, b.AllowedMarkets, StringEqual)
        && ListEqual(a.ExcludedMarkets, b.ExcludedMarkets, StringEqual);

    public static Markets CloneMarkets(Markets source) => new()
    {
        AllowedMarkets = CloneStringList(source.AllowedMarkets),
        ExcludedMarkets = CloneStringList(source.ExcludedMarkets),
    };

    public static bool InstalledFileEqual(InstalledFile a, InstalledFile b) =>
        StringEqual(a.RelativeFilePath, b.RelativeFilePath)
        && Equals(a.FileSha256, b.FileSha256)
        && a.FileType == b.FileType
        && StringEqual(a.InvocationParameter, b.InvocationParameter)
        && StringEqual(a.DisplayName, b.DisplayName);

    public static InstalledFile CloneInstalledFile(InstalledFile source) => new()
    {
        RelativeFilePath = source.RelativeFilePath,
        FileSha256 = source.FileSha256,
        FileType = source.FileType,
        InvocationParameter = source.InvocationParameter,
        DisplayName = source.DisplayName,
    };

    public static bool InstallationMetadataEqual(InstallationMetadata a, InstallationMetadata b) =>
        StringEqual(a.DefaultInstallLocation, b.DefaultInstallLocation)
        && ListEqual(a.Files, b.Files, InstalledFileEqual);

    public static InstallationMetadata CloneInstallationMetadata(InstallationMetadata source) => new()
    {
        DefaultInstallLocation = source.DefaultInstallLocation,
        Files = CloneList(source.Files, CloneInstalledFile),
    };

    public static bool AuthenticationEqual(Authentication a, Authentication b) =>
        a.AuthenticationType == b.AuthenticationType
        && EntraInfoEqual(a.MicrosoftEntraIdAuthenticationInfo, b.MicrosoftEntraIdAuthenticationInfo);

    public static Authentication CloneAuthentication(Authentication source) => new()
    {
        AuthenticationType = source.AuthenticationType,
        MicrosoftEntraIdAuthenticationInfo = source.MicrosoftEntraIdAuthenticationInfo is { } entra
            ? new MicrosoftEntraIdAuthenticationInfo { Resource = entra.Resource, Scope = entra.Scope }
            : null,
    };

    public static bool DocumentationEqual(Documentation a, Documentation b) =>
        StringEqual(a.DocumentLabel, b.DocumentLabel)
        && StringEqual(a.DocumentUrl, b.DocumentUrl);

    public static Documentation CloneDocumentation(Documentation source) => new()
    {
        DocumentLabel = source.DocumentLabel,
        DocumentUrl = source.DocumentUrl,
    };

    public static Icon CloneIcon(Icon source) => new()
    {
        IconUrl = source.IconUrl,
        IconFileType = source.IconFileType,
        IconResolution = source.IconResolution,
        IconTheme = source.IconTheme,
        IconSha256 = source.IconSha256,
    };

    private static bool EntraInfoEqual(MicrosoftEntraIdAuthenticationInfo? a, MicrosoftEntraIdAuthenticationInfo? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return StringEqual(a.Resource, b.Resource) && StringEqual(a.Scope, b.Scope);
    }
}
