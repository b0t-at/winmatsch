namespace WinMatsch.Rules;

/// <summary>
/// A finding produced by a validation rule: what rule fired, how severe it is, a human-readable
/// message and, when available, a path into the manifest such as <c>Installers[2].ProductCode</c>.
/// </summary>
public sealed record RuleFinding(string RuleId, RuleSeverity Severity, string Message, string? Path = null);

/// <summary>One entry of the explain log: which rule changed or found what.</summary>
public sealed record RuleTraceEntry(string RuleId, string Message);
