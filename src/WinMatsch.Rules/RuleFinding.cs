namespace WinMatsch.Rules;

/// <summary>
/// A finding produced by a validation rule: what rule fired, how severe it is, a human-readable
/// message and, when available, a path into the manifest such as <c>Installers[2].ProductCode</c>.
/// </summary>
public sealed record RuleFinding(string RuleId, RuleSeverity Severity, string Message, string? Path = null);

/// <summary>One entry of the explain log: which rule changed or found what.</summary>
public sealed record RuleTraceEntry(string RuleId, string Message);

/// <summary>A structured manifest mutation or log-only proposal.</summary>
public sealed record RuleChange(
    string RuleId,
    RuleMode Mode,
    RuleModeSource ModeSource,
    string ManifestPath,
    string FieldPath,
    string? Before,
    string? After,
    string SourceEvidence,
    RuleChangeConfidence Confidence);

/// <summary>Records the effective runtime decision for one rule invocation.</summary>
public sealed record RuleExecution(string RuleId, RuleMode Mode, RuleModeSource ModeSource);

/// <summary>
/// A three-way comparison showing that generated output would restore the bot's original value
/// over a different value in the merged manifest. Such a run must be reviewed.
/// </summary>
public sealed record HumanCorrectionReview(
    string ManifestPath,
    string FieldPath,
    string? BotValue,
    string? HumanValue,
    string? GeneratedValue);

/// <summary>Evidence attached by a rule to a specific structured change.</summary>
public sealed record RuleChangeEvidence(string Source, RuleChangeConfidence Confidence);
