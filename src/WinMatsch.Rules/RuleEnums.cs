namespace WinMatsch.Rules;

/// <summary>The kind of work a rule performs.</summary>
public enum RuleCategory
{
    /// <summary>Mutates the manifests into their canonical shape.</summary>
    Normalization,

    /// <summary>Inspects the manifests and reports findings without mutating them.</summary>
    Validation,

    /// <summary>Applies package-specific, data-driven fixups (see <c>QuirkPack</c>).</summary>
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
