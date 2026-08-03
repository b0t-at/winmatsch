namespace WinMatsch.Core;

/// <summary>Command-line switches WinGet passes to an installer.</summary>
public sealed class InstallerSwitches
{
    public string? Silent { get; set; }

    public string? SilentWithProgress { get; set; }

    public string? Interactive { get; set; }

    public string? InstallLocation { get; set; }

    public string? Log { get; set; }

    public string? Upgrade { get; set; }

    public string? Custom { get; set; }

    public string? Repair { get; set; }

    /// <summary>Whether no switch is set at all (useful when deciding whether to omit the mapping).</summary>
    public bool IsEmpty =>
        Silent is null && SilentWithProgress is null && Interactive is null && InstallLocation is null
        && Log is null && Upgrade is null && Custom is null && Repair is null;
}

/// <summary>Maps a non-success installer exit code to its meaning.</summary>
public sealed class ExpectedReturnCode
{
    public long? InstallerReturnCode { get; set; }

    public ReturnResponse? ReturnResponse { get; set; }

    public string? ReturnResponseUrl { get; set; }
}

/// <summary>A file inside an archive installer that is the actual installer or portable binary.</summary>
public sealed class NestedInstallerFile
{
    public string? RelativeFilePath { get; set; }

    public string? PortableCommandAlias { get; set; }
}

/// <summary>An Add/Remove Programs (ARP) registry entry produced by an installer.</summary>
public sealed class AppsAndFeaturesEntry
{
    public string? DisplayName { get; set; }

    public string? Publisher { get; set; }

    public string? DisplayVersion { get; set; }

    public string? ProductCode { get; set; }

    public string? UpgradeCode { get; set; }

    public InstallerType? InstallerType { get; set; }
}

/// <summary>Package dependencies of an installer.</summary>
public sealed class Dependencies
{
    public List<string>? WindowsFeatures { get; set; }

    public List<string>? WindowsLibraries { get; set; }

    public List<PackageDependency>? PackageDependencies { get; set; }

    public List<string>? ExternalDependencies { get; set; }
}

/// <summary>A dependency on another WinGet package.</summary>
public sealed class PackageDependency
{
    public PackageIdentifier? PackageIdentifier { get; set; }

    public PackageVersion? MinimumVersion { get; set; }
}

/// <summary>Markets a package may or may not be distributed in.</summary>
public sealed class Markets
{
    public List<string>? AllowedMarkets { get; set; }

    public List<string>? ExcludedMarkets { get; set; }
}

/// <summary>Details about the package's installation footprint.</summary>
public sealed class InstallationMetadata
{
    public string? DefaultInstallLocation { get; set; }

    public List<InstalledFile>? Files { get; set; }
}

/// <summary>A file installed by the package.</summary>
public sealed class InstalledFile
{
    public string? RelativeFilePath { get; set; }

    public Sha256Hash? FileSha256 { get; set; }

    public InstalledFileType? FileType { get; set; }

    public string? InvocationParameter { get; set; }

    public string? DisplayName { get; set; }
}

/// <summary>Authentication required to download an installer.</summary>
public sealed class Authentication
{
    public AuthenticationType? AuthenticationType { get; set; }

    public MicrosoftEntraIdAuthenticationInfo? MicrosoftEntraIdAuthenticationInfo { get; set; }
}

/// <summary>Microsoft Entra ID details for authenticated installer downloads.</summary>
public sealed class MicrosoftEntraIdAuthenticationInfo
{
    public string? Resource { get; set; }

    public string? Scope { get; set; }
}
