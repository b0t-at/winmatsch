namespace WinMatsch.Core;

/// <summary>
/// The installer fields that exist both at the root of an installer manifest (as defaults for
/// all installers) and on each individual installer entry. This shared base is what makes
/// hoisting common fields to the root, and pushing root fields down, a natural operation.
/// All fields are optional at the model level; schema requirements are enforced by rules and
/// by the writer for the few structurally required fields.
/// </summary>
public abstract class InstallerFieldsBase
{
    public LanguageTag? InstallerLocale { get; set; }

    public List<Platform>? Platform { get; set; }

    public MinimumOSVersion? MinimumOSVersion { get; set; }

    public InstallerType? InstallerType { get; set; }

    public InstallerType? NestedInstallerType { get; set; }

    public List<NestedInstallerFile>? NestedInstallerFiles { get; set; }

    public Scope? Scope { get; set; }

    public List<InstallMode>? InstallModes { get; set; }

    public InstallerSwitches? InstallerSwitches { get; set; }

    public List<long>? InstallerSuccessCodes { get; set; }

    public List<ExpectedReturnCode>? ExpectedReturnCodes { get; set; }

    public UpgradeBehavior? UpgradeBehavior { get; set; }

    public List<string>? Commands { get; set; }

    public List<string>? Protocols { get; set; }

    public List<string>? FileExtensions { get; set; }

    public Dependencies? Dependencies { get; set; }

    public string? PackageFamilyName { get; set; }

    public string? ProductCode { get; set; }

    public List<string>? Capabilities { get; set; }

    public List<string>? RestrictedCapabilities { get; set; }

    public Markets? Markets { get; set; }

    public bool? InstallerAbortsTerminal { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    public bool? InstallLocationRequired { get; set; }

    public bool? RequireExplicitUpgrade { get; set; }

    public bool? DisplayInstallWarnings { get; set; }

    public List<Architecture>? UnsupportedOSArchitectures { get; set; }

    public List<UnsupportedArgument>? UnsupportedArguments { get; set; }

    public List<AppsAndFeaturesEntry>? AppsAndFeaturesEntries { get; set; }

    public ElevationRequirement? ElevationRequirement { get; set; }

    public InstallationMetadata? InstallationMetadata { get; set; }

    public bool? DownloadCommandProhibited { get; set; }

    public RepairBehavior? RepairBehavior { get; set; }

    public bool? ArchiveBinariesDependOnPath { get; set; }

    public Authentication? Authentication { get; set; }
}
