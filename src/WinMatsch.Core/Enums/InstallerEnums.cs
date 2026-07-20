namespace WinMatsch.Core;

/// <summary>Installer target CPU architecture.</summary>
public enum Architecture
{
    X86,
    X64,
    Arm,
    Arm64,
    Neutral,
}

/// <summary>
/// The installer technology. Also used for <c>NestedInstallerType</c> (the schema restricts
/// nested types to a subset; that restriction is enforced by validation rules, not the model).
/// </summary>
public enum InstallerType
{
    Msix,
    Msi,
    Appx,
    Exe,
    Zip,
    Inno,
    Nullsoft,
    Wix,
    Burn,
    Pwa,
    Portable,
    Font,
}

/// <summary>Installation scope.</summary>
public enum Scope
{
    User,
    Machine,
}

/// <summary>Supported installer interaction modes.</summary>
public enum InstallMode
{
    Interactive,
    Silent,
    SilentWithProgress,
}

/// <summary>How WinGet upgrades an installed package.</summary>
public enum UpgradeBehavior
{
    Install,
    UninstallPrevious,
    Deny,
}

/// <summary>Elevation requirements of an installer.</summary>
public enum ElevationRequirement
{
    ElevationRequired,
    ElevationProhibited,
    ElevatesSelf,
}

/// <summary>Windows platform targeted by an installer.</summary>
public enum Platform
{
    /// <summary>YAML value: <c>Windows.Desktop</c>.</summary>
    WindowsDesktop,

    /// <summary>YAML value: <c>Windows.Universal</c>.</summary>
    WindowsUniversal,
}

/// <summary>WinGet arguments an installer does not support.</summary>
public enum UnsupportedArgument
{
    Log,
    Location,
}

/// <summary>How a package is repaired.</summary>
public enum RepairBehavior
{
    Modify,
    Uninstaller,
    Installer,
}

/// <summary>The meaning of a non-success installer exit code.</summary>
public enum ReturnResponse
{
    PackageInUse,
    PackageInUseByApplication,
    InstallInProgress,
    FileInUse,
    MissingDependency,
    DiskFull,
    InsufficientMemory,
    InvalidParameter,
    NoNetwork,
    ContactSupport,
    RebootRequiredToFinish,
    RebootRequiredForInstall,
    RebootInitiated,
    CancelledByUser,
    AlreadyInstalled,
    Downgrade,
    BlockedByPolicy,
    SystemNotSupported,
    Custom,
}

/// <summary>The role of a file listed in <c>InstallationMetadata.Files</c>.</summary>
public enum InstalledFileType
{
    Launch,
    Uninstall,
    Other,
}

/// <summary>Authentication required to download an installer.</summary>
public enum AuthenticationType
{
    None,
    MicrosoftEntraId,
    MicrosoftEntraIdForAzureBlobStorage,
}
