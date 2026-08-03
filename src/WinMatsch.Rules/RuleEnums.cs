namespace WinMatsch.Rules;

/// <summary>The kind of work a rule performs.</summary>
public enum RuleCategory
{
    /// <summary>Mutates the manifests into their canonical shape.</summary>
    Normalization,

    /// <summary>Inspects the manifests and reports findings without mutating them.</summary>
    Validation,

    /// <summary>Applies package-specific, data-driven fixups from override packs.</summary>
    Quirk,

    /// <summary>Enforces repository or organization policy.</summary>
    Policy,
}

/// <summary>The severity of a finding. Normalization and quirk rules mutate instead of reporting; their declared severity is <see cref="Info"/>.</summary>
public enum RuleSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Controls whether a rule mutates, only proposes, or does not run.</summary>
public enum RuleMode
{
    Apply,
    LogOnly,
    Disabled,
}

/// <summary>The configuration layer that selected an effective rule mode.</summary>
public enum RuleModeSource
{
    Default,
    UserConfig,
    PackageOverride,
    CommandOverride,
}

/// <summary>How strongly the evidence supports a recorded rule change.</summary>
public enum RuleChangeConfidence
{
    Low,
    Medium,
    High,
}
