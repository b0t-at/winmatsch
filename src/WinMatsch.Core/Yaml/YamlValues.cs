namespace WinMatsch.Core.Yaml;

/// <summary>
/// The single source of truth for mapping model enums to and from their YAML string values.
/// Hand-written switches keep this trimmer/AOT-safe and make every accepted value explicit.
/// Parsing is case-insensitive, matching WinGet's own behavior.
/// </summary>
public static class YamlValues
{
    public static string ToYaml(this ManifestType value) => value switch
    {
        ManifestType.Version => "version",
        ManifestType.Installer => "installer",
        ManifestType.DefaultLocale => "defaultLocale",
        ManifestType.Locale => "locale",
        ManifestType.Singleton => "singleton",
        _ => throw UnknownEnumValue(value),
    };

    public static ManifestType ParseManifestType(string value) => Normalize(value) switch
    {
        "version" => ManifestType.Version,
        "installer" => ManifestType.Installer,
        "defaultlocale" => ManifestType.DefaultLocale,
        "locale" => ManifestType.Locale,
        "singleton" => ManifestType.Singleton,
        _ => throw UnknownYamlValue<ManifestType>(value),
    };

    public static string ToYaml(this Architecture value) => value switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        Architecture.Neutral => "neutral",
        _ => throw UnknownEnumValue(value),
    };

    public static Architecture ParseArchitecture(string value) => Normalize(value) switch
    {
        "x86" => Architecture.X86,
        "x64" => Architecture.X64,
        "arm" => Architecture.Arm,
        "arm64" => Architecture.Arm64,
        "neutral" => Architecture.Neutral,
        _ => throw UnknownYamlValue<Architecture>(value),
    };

    public static string ToUnsupportedOSArchitectureYaml(this Architecture value) => value switch
    {
        Architecture.Neutral => throw UnknownEnumValue(value),
        _ => value.ToYaml(),
    };

    public static Architecture ParseUnsupportedOSArchitecture(string value)
    {
        Architecture architecture = ParseArchitecture(value);
        return architecture == Architecture.Neutral
            ? throw UnknownYamlValue<Architecture>(value)
            : architecture;
    }

    public static string ToYaml(this InstallerType value) => value switch
    {
        InstallerType.Msix => "msix",
        InstallerType.Msi => "msi",
        InstallerType.Appx => "appx",
        InstallerType.Exe => "exe",
        InstallerType.Zip => "zip",
        InstallerType.Inno => "inno",
        InstallerType.Nullsoft => "nullsoft",
        InstallerType.Wix => "wix",
        InstallerType.Burn => "burn",
        InstallerType.Pwa => "pwa",
        InstallerType.Portable => "portable",
        InstallerType.Font => "font",
        _ => throw UnknownEnumValue(value),
    };

    public static InstallerType ParseInstallerType(string value) => Normalize(value) switch
    {
        "msix" => InstallerType.Msix,
        "msi" => InstallerType.Msi,
        "appx" => InstallerType.Appx,
        "exe" => InstallerType.Exe,
        "zip" => InstallerType.Zip,
        "inno" => InstallerType.Inno,
        "nullsoft" => InstallerType.Nullsoft,
        "wix" => InstallerType.Wix,
        "burn" => InstallerType.Burn,
        "pwa" => InstallerType.Pwa,
        "portable" => InstallerType.Portable,
        "font" => InstallerType.Font,
        _ => throw UnknownYamlValue<InstallerType>(value),
    };

    public static string ToNestedInstallerTypeYaml(this InstallerType value) => value switch
    {
        InstallerType.Zip or InstallerType.Pwa => throw UnknownEnumValue(value),
        _ => value.ToYaml(),
    };

    public static InstallerType ParseNestedInstallerType(string value)
    {
        InstallerType type = ParseInstallerType(value);
        return type is InstallerType.Zip or InstallerType.Pwa
            ? throw UnknownYamlValue<InstallerType>(value)
            : type;
    }

    public static string ToYaml(this Scope value) => value switch
    {
        Scope.User => "user",
        Scope.Machine => "machine",
        _ => throw UnknownEnumValue(value),
    };

    public static Scope ParseScope(string value) => Normalize(value) switch
    {
        "user" => Scope.User,
        "machine" => Scope.Machine,
        _ => throw UnknownYamlValue<Scope>(value),
    };

    public static string ToYaml(this InstallMode value) => value switch
    {
        InstallMode.Interactive => "interactive",
        InstallMode.Silent => "silent",
        InstallMode.SilentWithProgress => "silentWithProgress",
        _ => throw UnknownEnumValue(value),
    };

    public static InstallMode ParseInstallMode(string value) => Normalize(value) switch
    {
        "interactive" => InstallMode.Interactive,
        "silent" => InstallMode.Silent,
        "silentwithprogress" => InstallMode.SilentWithProgress,
        _ => throw UnknownYamlValue<InstallMode>(value),
    };

    public static string ToYaml(this UpgradeBehavior value) => value switch
    {
        UpgradeBehavior.Install => "install",
        UpgradeBehavior.UninstallPrevious => "uninstallPrevious",
        UpgradeBehavior.Deny => "deny",
        _ => throw UnknownEnumValue(value),
    };

    public static UpgradeBehavior ParseUpgradeBehavior(string value) => Normalize(value) switch
    {
        "install" => UpgradeBehavior.Install,
        "uninstallprevious" => UpgradeBehavior.UninstallPrevious,
        "deny" => UpgradeBehavior.Deny,
        _ => throw UnknownYamlValue<UpgradeBehavior>(value),
    };

    public static string ToYaml(this ElevationRequirement value) => value switch
    {
        ElevationRequirement.ElevationRequired => "elevationRequired",
        ElevationRequirement.ElevationProhibited => "elevationProhibited",
        ElevationRequirement.ElevatesSelf => "elevatesSelf",
        _ => throw UnknownEnumValue(value),
    };

    public static ElevationRequirement ParseElevationRequirement(string value) => Normalize(value) switch
    {
        "elevationrequired" => ElevationRequirement.ElevationRequired,
        "elevationprohibited" => ElevationRequirement.ElevationProhibited,
        "elevatesself" => ElevationRequirement.ElevatesSelf,
        _ => throw UnknownYamlValue<ElevationRequirement>(value),
    };

    public static string ToYaml(this Platform value) => value switch
    {
        Platform.WindowsDesktop => "Windows.Desktop",
        Platform.WindowsUniversal => "Windows.Universal",
        _ => throw UnknownEnumValue(value),
    };

    public static Platform ParsePlatform(string value) => Normalize(value) switch
    {
        "windows.desktop" => Platform.WindowsDesktop,
        "windows.universal" => Platform.WindowsUniversal,
        _ => throw UnknownYamlValue<Platform>(value),
    };

    public static string ToYaml(this UnsupportedArgument value) => value switch
    {
        UnsupportedArgument.Log => "log",
        UnsupportedArgument.Location => "location",
        _ => throw UnknownEnumValue(value),
    };

    public static UnsupportedArgument ParseUnsupportedArgument(string value) => Normalize(value) switch
    {
        "log" => UnsupportedArgument.Log,
        "location" => UnsupportedArgument.Location,
        _ => throw UnknownYamlValue<UnsupportedArgument>(value),
    };

    public static string ToYaml(this RepairBehavior value) => value switch
    {
        RepairBehavior.Modify => "modify",
        RepairBehavior.Uninstaller => "uninstaller",
        RepairBehavior.Installer => "installer",
        _ => throw UnknownEnumValue(value),
    };

    public static RepairBehavior ParseRepairBehavior(string value) => Normalize(value) switch
    {
        "modify" => RepairBehavior.Modify,
        "uninstaller" => RepairBehavior.Uninstaller,
        "installer" => RepairBehavior.Installer,
        _ => throw UnknownYamlValue<RepairBehavior>(value),
    };

    public static string ToYaml(this ReturnResponse value) => value switch
    {
        ReturnResponse.PackageInUse => "packageInUse",
        ReturnResponse.PackageInUseByApplication => "packageInUseByApplication",
        ReturnResponse.InstallInProgress => "installInProgress",
        ReturnResponse.FileInUse => "fileInUse",
        ReturnResponse.MissingDependency => "missingDependency",
        ReturnResponse.DiskFull => "diskFull",
        ReturnResponse.InsufficientMemory => "insufficientMemory",
        ReturnResponse.InvalidParameter => "invalidParameter",
        ReturnResponse.NoNetwork => "noNetwork",
        ReturnResponse.ContactSupport => "contactSupport",
        ReturnResponse.RebootRequiredToFinish => "rebootRequiredToFinish",
        ReturnResponse.RebootRequiredForInstall => "rebootRequiredForInstall",
        ReturnResponse.RebootInitiated => "rebootInitiated",
        ReturnResponse.CancelledByUser => "cancelledByUser",
        ReturnResponse.AlreadyInstalled => "alreadyInstalled",
        ReturnResponse.Downgrade => "downgrade",
        ReturnResponse.BlockedByPolicy => "blockedByPolicy",
        ReturnResponse.SystemNotSupported => "systemNotSupported",
        ReturnResponse.Custom => "custom",
        _ => throw UnknownEnumValue(value),
    };

    public static ReturnResponse ParseReturnResponse(string value) => Normalize(value) switch
    {
        "packageinuse" => ReturnResponse.PackageInUse,
        "packageinusebyapplication" => ReturnResponse.PackageInUseByApplication,
        "installinprogress" => ReturnResponse.InstallInProgress,
        "fileinuse" => ReturnResponse.FileInUse,
        "missingdependency" => ReturnResponse.MissingDependency,
        "diskfull" => ReturnResponse.DiskFull,
        "insufficientmemory" => ReturnResponse.InsufficientMemory,
        "invalidparameter" => ReturnResponse.InvalidParameter,
        "nonetwork" => ReturnResponse.NoNetwork,
        "contactsupport" => ReturnResponse.ContactSupport,
        "rebootrequiredtofinish" => ReturnResponse.RebootRequiredToFinish,
        "rebootrequiredforinstall" => ReturnResponse.RebootRequiredForInstall,
        "rebootinitiated" => ReturnResponse.RebootInitiated,
        "cancelledbyuser" => ReturnResponse.CancelledByUser,
        "alreadyinstalled" => ReturnResponse.AlreadyInstalled,
        "downgrade" => ReturnResponse.Downgrade,
        "blockedbypolicy" => ReturnResponse.BlockedByPolicy,
        "systemnotsupported" => ReturnResponse.SystemNotSupported,
        "custom" => ReturnResponse.Custom,
        _ => throw UnknownYamlValue<ReturnResponse>(value),
    };

    public static string ToYaml(this InstalledFileType value) => value switch
    {
        InstalledFileType.Launch => "launch",
        InstalledFileType.Uninstall => "uninstall",
        InstalledFileType.Other => "other",
        _ => throw UnknownEnumValue(value),
    };

    public static InstalledFileType ParseInstalledFileType(string value) => Normalize(value) switch
    {
        "launch" => InstalledFileType.Launch,
        "uninstall" => InstalledFileType.Uninstall,
        "other" => InstalledFileType.Other,
        _ => throw UnknownYamlValue<InstalledFileType>(value),
    };

    public static string ToYaml(this AuthenticationType value) => value switch
    {
        AuthenticationType.None => "none",
        AuthenticationType.MicrosoftEntraId => "microsoftEntraId",
        AuthenticationType.MicrosoftEntraIdForAzureBlobStorage => "microsoftEntraIdForAzureBlobStorage",
        _ => throw UnknownEnumValue(value),
    };

    public static AuthenticationType ParseAuthenticationType(string value) => Normalize(value) switch
    {
        "none" => AuthenticationType.None,
        "microsoftentraid" => AuthenticationType.MicrosoftEntraId,
        "microsoftentraidforazureblobstorage" => AuthenticationType.MicrosoftEntraIdForAzureBlobStorage,
        _ => throw UnknownYamlValue<AuthenticationType>(value),
    };

    public static string ToYaml(this IconFileType value) => value switch
    {
        IconFileType.Png => "png",
        IconFileType.Jpeg => "jpeg",
        IconFileType.Ico => "ico",
        _ => throw UnknownEnumValue(value),
    };

    public static IconFileType ParseIconFileType(string value) => Normalize(value) switch
    {
        "png" => IconFileType.Png,
        "jpeg" => IconFileType.Jpeg,
        "ico" => IconFileType.Ico,
        _ => throw UnknownYamlValue<IconFileType>(value),
    };

    public static string ToYaml(this IconResolution value) => value switch
    {
        IconResolution.Custom => "custom",
        IconResolution.Size16 => "16x16",
        IconResolution.Size20 => "20x20",
        IconResolution.Size24 => "24x24",
        IconResolution.Size30 => "30x30",
        IconResolution.Size32 => "32x32",
        IconResolution.Size36 => "36x36",
        IconResolution.Size40 => "40x40",
        IconResolution.Size48 => "48x48",
        IconResolution.Size60 => "60x60",
        IconResolution.Size64 => "64x64",
        IconResolution.Size72 => "72x72",
        IconResolution.Size80 => "80x80",
        IconResolution.Size96 => "96x96",
        IconResolution.Size256 => "256x256",
        _ => throw UnknownEnumValue(value),
    };

    public static IconResolution ParseIconResolution(string value) => Normalize(value) switch
    {
        "custom" => IconResolution.Custom,
        "16x16" => IconResolution.Size16,
        "20x20" => IconResolution.Size20,
        "24x24" => IconResolution.Size24,
        "30x30" => IconResolution.Size30,
        "32x32" => IconResolution.Size32,
        "36x36" => IconResolution.Size36,
        "40x40" => IconResolution.Size40,
        "48x48" => IconResolution.Size48,
        "60x60" => IconResolution.Size60,
        "64x64" => IconResolution.Size64,
        "72x72" => IconResolution.Size72,
        "80x80" => IconResolution.Size80,
        "96x96" => IconResolution.Size96,
        "256x256" => IconResolution.Size256,
        _ => throw UnknownYamlValue<IconResolution>(value),
    };

    public static string ToYaml(this IconTheme value) => value switch
    {
        IconTheme.Default => "default",
        IconTheme.Light => "light",
        IconTheme.Dark => "dark",
        IconTheme.HighContrast => "highContrast",
        _ => throw UnknownEnumValue(value),
    };

    public static IconTheme ParseIconTheme(string value) => Normalize(value) switch
    {
        "default" => IconTheme.Default,
        "light" => IconTheme.Light,
        "dark" => IconTheme.Dark,
        "highcontrast" => IconTheme.HighContrast,
        _ => throw UnknownYamlValue<IconTheme>(value),
    };

    private static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant();
    }

    private static ArgumentOutOfRangeException UnknownEnumValue<T>(T value)
        where T : struct, Enum
        => new(nameof(value), value, $"Unknown {typeof(T).Name} value.");

    private static FormatException UnknownYamlValue<T>(string value)
        where T : struct, Enum
        => new($"'{value}' is not a valid {typeof(T).Name} value.");
}
