using WinMatsch.Core;

namespace WinMatsch.Analysis.Inno;

public enum InnoPrivilegeLevel
{
    None,
    PowerUser,
    Admin,
    Lowest,
}

public sealed record InnoLanguage(string? Name, uint LanguageId, uint CodePage, LanguageTag? Locale);

/// <summary>
/// Setup directives and evidence recovered from an Inno Setup header. Raw architecture
/// expressions are retained because expressions can describe more than one valid target.
/// </summary>
public sealed class InnoSetupMetadata
{
    public required Version SetupDataVersion { get; init; }

    public required bool IsUnicode { get; init; }

    public string? AppName { get; init; }

    public string? AppVerName { get; init; }

    public string? AppId { get; init; }

    public string? ProductCode { get; init; }

    public string? AppVersion { get; init; }

    public string? Publisher { get; init; }

    public string? DefaultDirName { get; init; }

    public string? UninstallDisplayName { get; init; }

    public string? CreateUninstallRegKey { get; init; }

    public string? Uninstallable { get; init; }

    public bool? CreatesUninstallRegistryKey { get; init; }

    public string? ArchitecturesAllowed { get; init; }

    public string? ArchitecturesInstallIn64BitMode { get; init; }

    public InnoPrivilegeLevel PrivilegesRequired { get; init; }

    public bool PrivilegesMayBeOverridden { get; init; }

    public Scope? Scope { get; init; }

    public ElevationRequirement? ElevationRequirement { get; init; }

    public IReadOnlyList<InnoLanguage> Languages { get; init; } = [];

    public IReadOnlyList<Architecture> EmbeddedPayloadArchitectures { get; init; } = [];

    public IReadOnlyList<Architecture> UnsupportedOSArchitectures { get; init; } = [];

    public Architecture? EffectiveArchitecture { get; init; }

    public bool ArchitectureIsConclusive { get; init; }

    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];

    internal IReadOnlyList<InnoPayloadCandidate> EmbeddedPayloads { get; init; } = [];

    internal bool PayloadInspectionIsComplete { get; init; }
}
